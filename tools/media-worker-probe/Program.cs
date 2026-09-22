using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

const byte KeyFrame = 2;

string executable = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.GetFullPath("Arsenal.UI/bin/Release/net10.0-windows/Arsenal.exe");
int rounds = args.Length > 1 && int.TryParse(args[1], out int parsed) ? parsed : 4;
if (!File.Exists(executable))
{
    Console.Error.WriteLine("Arsenal executable not found: " + executable);
    return 1;
}

// Warm the runtime, named-pipe async machinery and process APIs before taking the
// baseline. Their one-time handles belong to this probe, not to a worker cycle.
await using (WorkerRun warmup = await WorkerRun.StartAsync(executable))
{
    await warmup.WaitForReadyAsync();
    await warmup.SendAsync(KeyFrame);
    await warmup.WaitForVideoAsync(TimeSpan.FromSeconds(20));
    await warmup.StopAsync();
    if (!warmup.Process.WaitForExit(10_000)) return 2;
}

using Process parent = Process.GetCurrentProcess();
parent.Refresh();
int firstHandles = parent.HandleCount;
long firstPrivate = parent.PrivateMemorySize64;
Console.WriteLine($"Parent after warmup: private {Mb(firstPrivate)} MB, handles {firstHandles}");

for (int round = 1; round <= rounds; round++)
{
    await using WorkerRun worker = await WorkerRun.StartAsync(executable, audio: round == 1);
    JsonElement ready = await worker.WaitForReadyAsync();
    await worker.SendAsync(KeyFrame);
    int videoBytes = await worker.WaitForVideoAsync(TimeSpan.FromSeconds(20));

    worker.Process.Refresh();
    Console.WriteLine(
        $"Round {round}: {ready.GetProperty("codec").GetString()} "
        + $"{ready.GetProperty("width").GetInt32()}x{ready.GetProperty("height").GetInt32()}, "
        + $"audio {(ready.TryGetProperty("audio", out JsonElement audio) && audio.ValueKind == JsonValueKind.Object ? "ready" : "off")}, "
        + $"video {videoBytes} bytes, worker private {Mb(worker.Process.PrivateMemorySize64)} MB, "
        + $"handles {worker.Process.HandleCount}");

    int processId = worker.Process.Id;
    await worker.StopAsync();
    if (!worker.Process.WaitForExit(10_000))
    {
        Console.Error.WriteLine($"Worker {processId} did not exit after stop.");
        return 2;
    }
}

await using (WorkerRun disconnected = await WorkerRun.StartAsync(executable))
{
    await disconnected.WaitForReadyAsync();
    int processId = disconnected.Process.Id;
    disconnected.Disconnect();
    if (!disconnected.Process.WaitForExit(10_000))
    {
        Console.Error.WriteLine($"Worker {processId} did not exit after its parent pipe disconnected.");
        return 3;
    }
    Console.WriteLine("Parent-disconnect cleanup: worker exited");
}

GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
GC.WaitForPendingFinalizers();
parent.Refresh();
Console.WriteLine($"Parent after: private {Mb(parent.PrivateMemorySize64)} MB, handles {parent.HandleCount}");
if (parent.HandleCount > firstHandles + 12)
{
    Console.Error.WriteLine($"Parent handle count grew by {parent.HandleCount - firstHandles}.");
    return 4;
}

Console.WriteLine($"Media worker survived {rounds} start, frame, keyframe and stop cycles.");
return 0;

static long Mb(long bytes) => bytes / (1024 * 1024);

sealed class WorkerRun : IAsyncDisposable
{
    private const byte Ready = 1;
    private const byte Video = 2;
    private const byte Stop = 1;
    private const int HeaderSize = 6;

    private readonly NamedPipeServerStream _pipe;
    private bool _disconnected;

    private WorkerRun(NamedPipeServerStream pipe, Process process)
    {
        _pipe = pipe;
        Process = process;
    }

    internal Process Process { get; }

    internal static async Task<WorkerRun> StartAsync(string executable, bool audio = false)
    {
        string pipeName = "arsenal-media-probe-" + Guid.NewGuid().ToString("N");
        var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        string json = JsonSerializer.Serialize(new
        {
            monitor = 0,
            maxWidth = 1280,
            maxHeight = 720,
            frameRate = 20,
            bitrateKbps = 5000,
            jpegQuality = 60,
            cursor = true,
            audio,
            monoAudio = false,
            codecs = new[] { "hevc", "h264" },
        });
        string configuration = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        start.ArgumentList.Add("--media-worker");
        start.ArgumentList.Add(pipeName);
        start.ArgumentList.Add(configuration);

        Process process = Process.Start(start) ?? throw new InvalidOperationException("Worker did not start.");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await pipe.WaitForConnectionAsync(timeout.Token);
            return new WorkerRun(pipe, process);
        }
        catch
        {
            pipe.Dispose();
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.Dispose();
            throw;
        }
    }

    internal async Task<JsonElement> WaitForReadyAsync()
    {
        while (true)
        {
            (byte message, _, byte[] payload) = await ReadFrameAsync(TimeSpan.FromSeconds(10));
            if (message == Ready) return JsonDocument.Parse(payload).RootElement.Clone();
        }
    }

    internal async Task<int> WaitForVideoAsync(TimeSpan timeout)
    {
        while (true)
        {
            (byte message, _, byte[] payload) = await ReadFrameAsync(timeout);
            if (message == Video) return payload.Length;
        }
    }

    private async Task<(byte Message, byte Flags, byte[] Payload)> ReadFrameAsync(TimeSpan timeout)
    {
        using var stopping = new CancellationTokenSource(timeout);
        byte[] header = new byte[HeaderSize];
        await ReadExactAsync(header, stopping.Token);
        int length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(2));
        if (length < 0 || length > 64 * 1024 * 1024) throw new InvalidDataException("Worker frame length out of range.");
        byte[] payload = new byte[length];
        await ReadExactAsync(payload, stopping.Token);
        return (header[0], header[1], payload);
    }

    private async Task ReadExactAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int got = await _pipe.ReadAsync(buffer[read..], cancellationToken);
            if (got <= 0) throw new EndOfStreamException();
            read += got;
        }
    }

    internal async Task SendAsync(byte command)
    {
        await _pipe.WriteAsync(new[] { command });
        await _pipe.FlushAsync();
    }

    internal async Task StopAsync()
    {
        if (_disconnected) return;
        try { await SendAsync(Stop); } catch { }
    }

    internal void Disconnect()
    {
        if (_disconnected) return;
        _disconnected = true;
        _pipe.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disconnected)
        {
            await StopAsync();
            _pipe.Dispose();
        }
        _disconnected = true;
        if (!Process.HasExited)
        {
            try { Process.Kill(entireProcessTree: true); } catch { }
        }
        Process.Dispose();
    }
}
