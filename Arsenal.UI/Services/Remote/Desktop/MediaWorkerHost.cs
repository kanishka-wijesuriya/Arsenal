using Arsenal.Helpers;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace Arsenal.UI.Services.Remote.Desktop;

internal static class MediaWorkerHost
{
    private static readonly TimeSpan StatsInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IdleKeyFrameInterval = TimeSpan.FromSeconds(10);
    private const int SecureDesktopPollMs = 400;

    /// <summary>
    /// Runs the capture and encode side of a session in its own process.
    /// </summary>
    /// <remarks>
    /// The first thing this does is claim the same DPI awareness the rest of Arsenal
    /// has, and it has to be done here rather than left to the entry point: the worker
    /// branch returns before <c>ApplicationConfiguration.Initialize</c>, which is the
    /// only place the process-wide mode is otherwise applied.
    ///
    /// <para>Without it the worker is DPI unaware, and an unaware process is lied to
    /// about the screen in one direction only. <c>GetMonitorInfo</c> hands back the
    /// scaled size - 1707x1067 on a 2560x1600 panel at 150% - while the screen device
    /// context is still the real thing at full resolution. Capturing the reported size
    /// therefore copies the top left two thirds of the display and calls it the whole
    /// screen, and the phone, told that picture is the entire monitor, places every tap
    /// a third of the way off. Both halves of that came from this one line being
    /// missing.</para>
    /// </remarks>
    internal static int Run(string pipeName, string encodedConfiguration)
    {
        try
        {
            RemoteNative.SetProcessDpiAwarenessContext(RemoteNative.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

            MediaWorkerConfiguration configuration = MediaWorkerProtocol.DecodeConfiguration(encodedConfiguration);
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(10_000);
            using var stopping = new CancellationTokenSource();
            var writer = new WorkerWriter(pipe, stopping.Token);

            // Capture runs on a thread of its own, in the multithreaded apartment.
            //
            // The process entry point is [STAThread], which the user interface needs and
            // this does not: an asynchronous Media Foundation transform delivers
            // METransformNeedInput through COM, and on a single-threaded apartment that
            // delivery waits for a message pump this process does not have. The encoder
            // configures, reports itself ready, and then never asks for a frame - which
            // is indistinguishable from the hardware being broken, and is what the
            // capture loop was seeing within a second of every session starting.
            int result = 1;
            var worker = new Thread(() => result = RunCapture(pipe, writer, configuration, stopping))
            {
                IsBackground = false,
                Name = "arsenal-media-worker",
            };
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();
            worker.Join();
            return result;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Media worker: " + ex.Message);
            return 1;
        }
        finally
        {
            MediaFoundationVideoEncoder.ReleasePlatformIfIdle();
        }
    }

    private static int RunCapture(
        NamedPipeClientStream pipe,
        WorkerWriter writer,
        MediaWorkerConfiguration configuration,
        CancellationTokenSource stopping)
    {
        IReadOnlyList<RemoteMonitor> monitors = RemoteMonitors.Enumerate();
        RemoteMonitor monitor = monitors[Math.Clamp(configuration.Monitor, 0, monitors.Count - 1)];

        // The path that keeps every frame on the graphics card, where this machine and
        // this monitor allow one. It is tried first and it proves itself by coding a real
        // frame before it is accepted, so anything that returns null here - a combined
        // virtual monitor, a driver with no duplication, an encoder that cannot take
        // textures - falls through to the desktop copy path below rather than failing.
        GpuCapturePipeline? gpu = GpuCapturePipeline.TryCreate(
            monitor, configuration.MaxWidth, configuration.MaxHeight,
            configuration.FrameRate, configuration.BitrateKbps, configuration.Codecs);

        GdiScreenSource? source = null;
        IVideoEncoder? encoder = null;

        if (gpu is null)
        {
            source = new GdiScreenSource(monitor, configuration.MaxWidth, configuration.MaxHeight, configuration.Cursor);
            encoder = MediaFoundationVideoEncoder.TryCreate(
                configuration.Codecs,
                source.Width,
                source.Height,
                configuration.FrameRate,
                configuration.BitrateKbps)
                ?? (IVideoEncoder)new JpegTileEncoder(source.Width, source.Height, configuration.JpegQuality);
        }

        RemoteAudioCapture? audio = null;
        RemoteAudioFormat? audioFormat = null;
        if (configuration.Audio)
        {
            audio = new RemoteAudioCapture(configuration.MonoAudio, (samples, length, timestampUs) =>
            {
                if (length <= 0 || stopping.IsCancellationRequested) return;
                byte[] payload = ArrayPool<byte>.Shared.Rent(length + 8);
                try
                {
                    System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(payload, timestampUs);
                    samples.AsSpan(0, length).CopyTo(payload.AsSpan(8));
                    writer.TryWrite(MediaWorkerProtocol.Message.Audio, 0, payload.AsSpan(0, length + 8));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(payload);
                }
            });
            if (audio.Start()) audioFormat = audio.Format;
            else
            {
                audio.Dispose();
                audio = null;
            }
        }

        try
        {
            writer.WriteJson(MediaWorkerProtocol.Message.Ready, gpu is not null
                ? new MediaWorkerReady(gpu.Codec, gpu.EncoderName, gpu.Width, gpu.Height, configuration.FrameRate, audioFormat)
                : Ready(encoder!, source!, configuration, audioFormat));
            int keyFrameWanted = 0;
            // Volatile rather than applied on the command thread: the encoder and the
            // pipeline are owned by the capture loop, and reaching into them from the
            // pipe reader would be a second thread on a transform that is mid frame.
            int targetKbps = 0;
            int targetFps = 0;

            Task commands = Task.Run(() => ReadCommands(
                pipe,
                () => Interlocked.Exchange(ref keyFrameWanted, 1),
                (kbps, fps) =>
                {
                    Interlocked.Exchange(ref targetKbps, kbps);
                    Interlocked.Exchange(ref targetFps, fps);
                },
                stopping));

            var frame = new CapturedFrame();
            var encoded = new EncodedVideoFrame();
            var stopwatch = Stopwatch.StartNew();
            long nextStatsMs = (long)StatsInterval.TotalMilliseconds;
            long lastKeyFrameMs = 0;
            int frames = 0;
            int lastCaptureMs = 0;
            int lastEncodeMs = 0;
            bool surfaceReadable = true;

            while (!stopping.IsCancellationRequested && pipe.IsConnected)
            {
                long tickStart = stopwatch.ElapsedMilliseconds;
                int targetMs = Math.Max(8, 1000 / Math.Max(1, configuration.FrameRate));

                bool readable = RemoteNative.IsInputDesktopReadable();
                bool returned = readable && readable != surfaceReadable;
                if (readable != surfaceReadable)
                {
                    surfaceReadable = readable;
                    writer.WriteJson(MediaWorkerProtocol.Message.Surface, new MediaWorkerSurface(readable));
                }
                if (!readable)
                {
                    Thread.Sleep(SecureDesktopPollMs);
                    continue;
                }

                // The duplication path. It blocks inside AcquireNextFrame until the
                // desktop changes and drops what the frame rate does not need, so there
                // is no sleep at the bottom of this branch: waking on a timer was the
                // thing that capped the old path, and pacing here is the schedule the
                // pipeline keeps.
                // Applied here, on the thread that owns the encoder.
                int wantedKbps = Interlocked.Exchange(ref targetKbps, 0);
                if (wantedKbps > 0)
                {
                    int wantedFps = Volatile.Read(ref targetFps);
                    if (gpu is not null) gpu.SetTarget(wantedKbps, wantedFps);
                    else if (encoder is MediaFoundationVideoEncoder hardware) hardware.TrySetBitrate(wantedKbps);
                    Logger.WriteLine($"Media worker: now sending at {wantedKbps} kbps, {wantedFps} fps.");
                }

                if (gpu is not null)
                {
                    if (Interlocked.Exchange(ref keyFrameWanted, 0) != 0) gpu.RequestKeyFrame();
                    if (returned) gpu.RequestKeyFrame();

                    if (stopwatch.ElapsedMilliseconds - lastKeyFrameMs > IdleKeyFrameInterval.TotalMilliseconds)
                    {
                        gpu.RequestKeyFrame();
                        lastKeyFrameMs = stopwatch.ElapsedMilliseconds;
                    }

                    bool sent = gpu.TryProduce((uint)targetMs, encoded, out lastCaptureMs, out lastEncodeMs, out bool alive);
                    if (sent)
                    {
                        if ((encoded.Flags & RemoteDesktopProtocol.VideoFlags.KeyFrame) != 0)
                            lastKeyFrameMs = stopwatch.ElapsedMilliseconds;

                        writer.Write(MediaWorkerProtocol.Message.Video, (byte)encoded.Flags, encoded.Data.AsSpan(0, encoded.Length));
                        frames++;
                    }

                    // The duplication has gone and could not be rebuilt, or the encoder
                    // stopped answering. Either way the session continues on the path
                    // that always works rather than ending.
                    if (!alive || gpu.HasStalled)
                    {
                        Logger.WriteLine(alive
                            ? "Media worker: the hardware encoder stopped; falling back to the desktop copy path."
                            : "Media worker: desktop duplication was lost; falling back to the desktop copy path.");

                        gpu.Dispose();
                        gpu = null;
                        source = new GdiScreenSource(monitor, configuration.MaxWidth, configuration.MaxHeight, configuration.Cursor);
                        encoder = new JpegTileEncoder(source.Width, source.Height, configuration.JpegQuality);
                        writer.WriteJson(MediaWorkerProtocol.Message.Ready, Ready(encoder, source, configuration, audioFormat));
                    }

                    if (stopwatch.ElapsedMilliseconds >= nextStatsMs)
                    {
                        nextStatsMs = stopwatch.ElapsedMilliseconds + (long)StatsInterval.TotalMilliseconds;
                        writer.WriteJson(MediaWorkerProtocol.Message.Stats, new MediaWorkerStats(
                            (int)Math.Round(frames / StatsInterval.TotalSeconds),
                            lastEncodeMs,
                            lastCaptureMs,
                            gpu?.EncoderName ?? EncoderName(encoder!)));
                        frames = 0;
                    }

                    continue;
                }

                if (encoder is MediaFoundationVideoEncoder { HasStalled: true } stalled)
                {
                    stalled.Dispose();
                    encoder = new JpegTileEncoder(source.Width, source.Height, configuration.JpegQuality);
                    writer.WriteJson(MediaWorkerProtocol.Message.Ready, Ready(encoder, source, configuration, audioFormat));
                    Logger.WriteLine("Media worker fell back to picture tiles after the video encoder stopped responding.");
                }

                if (Interlocked.Exchange(ref keyFrameWanted, 0) != 0) encoder.RequestKeyFrame();
                if (returned) encoder.RequestKeyFrame();
                long captureStart = stopwatch.ElapsedMilliseconds;
                bool captured = source.TryCapture(frame);
                lastCaptureMs = (int)(stopwatch.ElapsedMilliseconds - captureStart);

                if (captured)
                {
                    if (stopwatch.ElapsedMilliseconds - lastKeyFrameMs > IdleKeyFrameInterval.TotalMilliseconds)
                    {
                        encoder.RequestKeyFrame();
                        lastKeyFrameMs = stopwatch.ElapsedMilliseconds;
                    }

                    long encodeStart = stopwatch.ElapsedMilliseconds;
                    if (encoder.TryEncode(frame, encoded))
                    {
                        lastEncodeMs = (int)(stopwatch.ElapsedMilliseconds - encodeStart);
                        if ((encoded.Flags & RemoteDesktopProtocol.VideoFlags.KeyFrame) != 0)
                            lastKeyFrameMs = stopwatch.ElapsedMilliseconds;
                        writer.Write(MediaWorkerProtocol.Message.Video, (byte)encoded.Flags, encoded.Data.AsSpan(0, encoded.Length));
                        frames++;
                    }
                }

                if (stopwatch.ElapsedMilliseconds >= nextStatsMs)
                {
                    nextStatsMs = stopwatch.ElapsedMilliseconds + (long)StatsInterval.TotalMilliseconds;
                    writer.WriteJson(MediaWorkerProtocol.Message.Stats, new MediaWorkerStats(
                        (int)Math.Round(frames / StatsInterval.TotalSeconds),
                        lastEncodeMs,
                        lastCaptureMs,
                        EncoderName(encoder)));
                    frames = 0;
                }

                int elapsed = (int)(stopwatch.ElapsedMilliseconds - tickStart);
                if (elapsed < targetMs) Thread.Sleep(targetMs - elapsed);
            }

            stopping.Cancel();
            try { commands.Wait(TimeSpan.FromSeconds(1)); } catch { }
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            writer.TryWriteJson(MediaWorkerProtocol.Message.Error, new MediaWorkerError(ex.Message));
            Logger.WriteLine("Media worker capture: " + ex.Message);
            return 1;
        }
        finally
        {
            stopping.Cancel();
            audio?.Dispose();
            gpu?.Dispose();
            encoder?.Dispose();
            source?.Dispose();
        }
    }

    private static void ReadCommands(Stream pipe, Action requestKeyFrame, Action<int, int> setTarget, CancellationTokenSource stopping)
    {
        Span<byte> target = stackalloc byte[8];
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                int value = pipe.ReadByte();
                if (value < 0 || value == (byte)MediaWorkerProtocol.Command.Stop) break;
                if (value == (byte)MediaWorkerProtocol.Command.KeyFrame) requestKeyFrame();
                else if (value == (byte)MediaWorkerProtocol.Command.Target)
                {
                    int read = 0;
                    while (read < target.Length)
                    {
                        int got = pipe.Read(target[read..]);
                        if (got <= 0) return;
                        read += got;
                    }

                    setTarget(
                        System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(target),
                        System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(target[4..]));
                }
            }
        }
        catch (IOException) { }
        finally
        {
            stopping.Cancel();
            // This is a disposable media-only process. Native WASAPI or codec teardown
            // can block after the parent has already ended the session, so do not keep
            // the worker alive waiting for a driver callback that may never arrive.
            Environment.Exit(0);
        }
    }

    private static MediaWorkerReady Ready(
        IVideoEncoder encoder,
        IScreenSource source,
        MediaWorkerConfiguration configuration,
        RemoteAudioFormat? audio) =>
        new(encoder.Codec, EncoderName(encoder), source.Width, source.Height, configuration.FrameRate, audio);

    private static string EncoderName(IVideoEncoder encoder) =>
        encoder is MediaFoundationVideoEncoder hardware ? hardware.EncoderName : encoder.Codec;

    private sealed class WorkerWriter(Stream stream, CancellationToken stopping)
    {
        private readonly object _gate = new();
        private readonly byte[] _header = new byte[MediaWorkerProtocol.HeaderSize];

        internal void WriteJson<T>(MediaWorkerProtocol.Message message, T value) =>
            Write(message, 0, JsonSerializer.SerializeToUtf8Bytes(value, RemoteDesktopProtocol.Json));

        internal bool TryWriteJson<T>(MediaWorkerProtocol.Message message, T value)
        {
            try { WriteJson(message, value); return true; }
            catch { return false; }
        }

        internal bool TryWrite(MediaWorkerProtocol.Message message, byte flags, ReadOnlySpan<byte> payload)
        {
            try { Write(message, flags, payload); return true; }
            catch { return false; }
        }

        internal void Write(MediaWorkerProtocol.Message message, byte flags, ReadOnlySpan<byte> payload)
        {
            lock (_gate)
            {
                stopping.ThrowIfCancellationRequested();
                MediaWorkerProtocol.WriteHeader(_header, message, flags, payload.Length);
                stream.Write(_header);
                stream.Write(payload);
            }
        }
    }
}
