using Arsenal.Helpers;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace Arsenal.UI.Services.Remote.Desktop;

internal sealed class MediaWorkerClient : IDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly Process _process;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Action<MediaWorkerProtocol.Message, byte, byte[], int> _onPayload;
    private readonly Action<MediaWorkerReady> _onReady;
    private readonly Action<MediaWorkerStats> _onStats;
    private readonly Action<bool> _onSurface;
    private readonly Action<MediaWorkerClient> _onDisconnected;
    private readonly Task _reader;
    private int _disposed;

    private MediaWorkerClient(
        NamedPipeServerStream pipe,
        Process process,
        MediaWorkerReady ready,
        Action<MediaWorkerProtocol.Message, byte, byte[], int> onPayload,
        Action<MediaWorkerReady> onReady,
        Action<MediaWorkerStats> onStats,
        Action<bool> onSurface,
        Action<MediaWorkerClient> onDisconnected)
    {
        _pipe = pipe;
        _process = process;
        Ready = ready;
        _onPayload = onPayload;
        _onReady = onReady;
        _onStats = onStats;
        _onSurface = onSurface;
        _onDisconnected = onDisconnected;
        _reader = Task.Run(ReadLoopAsync);
    }

    internal MediaWorkerReady Ready { get; private set; }

    internal static async Task<MediaWorkerClient> StartAsync(
        MediaWorkerConfiguration configuration,
        Action<MediaWorkerProtocol.Message, byte, byte[], int> onPayload,
        Action<MediaWorkerReady> onReady,
        Action<MediaWorkerStats> onStats,
        Action<bool> onSurface,
        Action<MediaWorkerClient> onDisconnected,
        CancellationToken cancellationToken)
    {
        string pipeName = "arsenal-media-" + Guid.NewGuid().ToString("N");
        var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        Process? process = null;
        try
        {
            string executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("The Arsenal executable path is unavailable.");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            start.ArgumentList.Add("--media-worker");
            start.ArgumentList.Add(pipeName);
            start.ArgumentList.Add(MediaWorkerProtocol.EncodeConfiguration(configuration));
            process = Process.Start(start) ?? throw new InvalidOperationException("The media worker did not start.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await pipe.WaitForConnectionAsync(timeout.Token);
            MediaWorkerReady ready = await ReadReadyAsync(pipe, timeout.Token);
            return new MediaWorkerClient(pipe, process, ready, onPayload, onReady, onStats, onSurface, onDisconnected);
        }
        catch
        {
            pipe.Dispose();
            if (process is { HasExited: false })
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            }
            process?.Dispose();
            throw;
        }
    }

    internal void RequestKeyFrame() => SendCommand(MediaWorkerProtocol.Command.KeyFrame);

    private void SendCommand(MediaWorkerProtocol.Command command, bool allowDisposing = false)
    {
        try
        {
            if ((!allowDisposing && Volatile.Read(ref _disposed) != 0) || !_pipe.IsConnected) return;
            _pipe.WriteByte((byte)command);
            _pipe.Flush();
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task ReadLoopAsync()
    {
        byte[] header = new byte[MediaWorkerProtocol.HeaderSize];
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                await ReadExactAsync(_pipe, header, _stopping.Token);
                var (message, flags, length) = MediaWorkerProtocol.ReadHeader(header);
                if (length < 0 || length > MediaWorkerProtocol.MaxPayloadBytes)
                    throw new InvalidDataException("Media worker frame length out of range.");

                byte[] payload = length == 0 ? Array.Empty<byte>() : ArrayPool<byte>.Shared.Rent(length);
                bool handedOff = false;
                try
                {
                    if (length > 0) await ReadExactAsync(_pipe, payload.AsMemory(0, length), _stopping.Token);
                    if (message is MediaWorkerProtocol.Message.Video or MediaWorkerProtocol.Message.Audio)
                    {
                        _onPayload(message, flags, payload, length);
                        handedOff = true;
                    }
                    else
                    {
                        Dispatch(message, payload.AsSpan(0, length));
                    }
                }
                finally
                {
                    if (length > 0 && !handedOff) ArrayPool<byte>.Shared.Return(payload);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (EndOfStreamException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            Logger.WriteLine("Media worker reader: " + ex.Message);
        }
        finally
        {
            if (!_stopping.IsCancellationRequested && Volatile.Read(ref _disposed) == 0)
                _ = Task.Run(() => _onDisconnected(this));
        }
    }

    private void Dispatch(MediaWorkerProtocol.Message message, ReadOnlySpan<byte> payload)
    {
        switch (message)
        {
            case MediaWorkerProtocol.Message.Ready:
                Ready = JsonSerializer.Deserialize<MediaWorkerReady>(payload, RemoteDesktopProtocol.Json)
                    ?? throw new InvalidDataException("The media worker returned an empty format.");
                _onReady(Ready);
                break;
            case MediaWorkerProtocol.Message.Stats:
                if (JsonSerializer.Deserialize<MediaWorkerStats>(payload, RemoteDesktopProtocol.Json) is { } stats) _onStats(stats);
                break;
            case MediaWorkerProtocol.Message.Surface:
                if (JsonSerializer.Deserialize<MediaWorkerSurface>(payload, RemoteDesktopProtocol.Json) is { } surface) _onSurface(surface.Readable);
                break;
            case MediaWorkerProtocol.Message.Error:
                if (JsonSerializer.Deserialize<MediaWorkerError>(payload, RemoteDesktopProtocol.Json) is { } error)
                    Logger.WriteLine("Media worker: " + error.Message);
                break;
        }
    }

    private static async Task<MediaWorkerReady> ReadReadyAsync(Stream pipe, CancellationToken cancellationToken)
    {
        byte[] header = new byte[MediaWorkerProtocol.HeaderSize];
        await ReadExactAsync(pipe, header, cancellationToken);
        var (message, _, length) = MediaWorkerProtocol.ReadHeader(header);
        if (message != MediaWorkerProtocol.Message.Ready || length <= 0 || length > 64 * 1024)
            throw new InvalidDataException("The media worker did not return its format.");

        byte[] payload = new byte[length];
        await ReadExactAsync(pipe, payload, cancellationToken);
        return JsonSerializer.Deserialize<MediaWorkerReady>(payload, RemoteDesktopProtocol.Json)
            ?? throw new InvalidDataException("The media worker returned an empty format.");
    }

    private static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int got = await stream.ReadAsync(buffer[read..], cancellationToken);
            if (got <= 0) throw new EndOfStreamException();
            read += got;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        SendCommand(MediaWorkerProtocol.Command.Stop, allowDisposing: true);
        _stopping.Cancel();
        try { _pipe.Dispose(); } catch { }
        try { _reader.Wait(TimeSpan.FromSeconds(1)); } catch { }
        try
        {
            if (!_process.WaitForExit(3_000))
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(3_000);
            }
        }
        catch { }
        _process.Dispose();
        _stopping.Dispose();
    }
}
