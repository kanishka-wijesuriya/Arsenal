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

    internal static int Run(string pipeName, string encodedConfiguration)
    {
        try
        {
            MediaWorkerConfiguration configuration = MediaWorkerProtocol.DecodeConfiguration(encodedConfiguration);
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(10_000);
            using var stopping = new CancellationTokenSource();
            var writer = new WorkerWriter(pipe, stopping.Token);
            return RunCapture(pipe, writer, configuration, stopping);
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

        using var source = new GdiScreenSource(monitor, configuration.MaxWidth, configuration.MaxHeight, configuration.Cursor);
        IVideoEncoder encoder = MediaFoundationVideoEncoder.TryCreate(
            configuration.Codecs,
            source.Width,
            source.Height,
            configuration.FrameRate,
            configuration.BitrateKbps)
            ?? (IVideoEncoder)new JpegTileEncoder(source.Width, source.Height, configuration.JpegQuality);

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
            writer.WriteJson(MediaWorkerProtocol.Message.Ready, Ready(encoder, source, configuration, audioFormat));
            int keyFrameWanted = 0;
            Task commands = Task.Run(() => ReadCommands(pipe, () => Interlocked.Exchange(ref keyFrameWanted, 1), stopping));

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
            encoder.Dispose();
        }
    }

    private static void ReadCommands(Stream pipe, Action requestKeyFrame, CancellationTokenSource stopping)
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                int value = pipe.ReadByte();
                if (value < 0 || value == (byte)MediaWorkerProtocol.Command.Stop) break;
                if (value == (byte)MediaWorkerProtocol.Command.KeyFrame) requestKeyFrame();
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
