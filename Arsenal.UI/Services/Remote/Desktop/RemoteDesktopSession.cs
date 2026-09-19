using Arsenal.Helpers;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows.Threading;

namespace Arsenal.UI.Services.Remote.Desktop;

/// <summary>Who is on the other end, for the consent prompt and the status line.</summary>
internal sealed record RemotePeer(string DeviceId, string DeviceName, string Address);

/// <summary>
/// One live remote control session.
/// </summary>
/// <remarks>
/// Owns the socket for as long as somebody is watching, and everything hanging off it:
/// the capture thread, the encoder, audio, clipboard, files and the privacy guard. When
/// it ends, for any reason including the network simply going away, it puts all of them
/// back - held keys released, local input unblocked, covers removed - because a session
/// that leaves the desktop in its own state is worse than one that never started.
///
/// <para>Three loops, deliberately separate:</para>
/// <list type="bullet">
/// <item><b>Read</b> takes frames off the socket. Input is applied inline because a
/// pointer move that waits for a queue is a pointer move somebody can feel.</item>
/// <item><b>Capture</b> runs on a thread of its own, paced to the requested frame rate.
/// It is a thread rather than a timer because it holds a GDI device context and does
/// blocking work every tick, and a thread pool thread doing that starves everything
/// else on the pool.</item>
/// <item><b>Write</b> drains one queue to the socket. One writer, because two tasks
/// interleaving frames on the same stream produces a stream neither end can parse.</item>
/// </list>
/// </remarks>
internal sealed class RemoteDesktopSession : IRemoteFrameWriter, IDisposable
{
    /// <summary>
    /// How many frames may be waiting for the socket.
    /// </summary>
    /// <remarks>
    /// Small on purpose. A deep queue on a slow network does not make the session
    /// smoother, it makes it late: every frame in the queue is already stale by the time
    /// it is sent, and the phone shows a screen from a second ago. Video is dropped when
    /// this fills rather than queued, which is the right answer for a picture that will
    /// be replaced in 16 milliseconds and the wrong one for a file chunk, so file chunks
    /// wait instead.
    /// </remarks>
    private const int OutboundCapacity = 8;

    private static readonly TimeSpan StatsInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IdleKeyFrameInterval = TimeSpan.FromSeconds(10);

    /// <summary>How often to look again while the secure desktop is up.</summary>
    /// <remarks>
    /// Nothing can be captured until it goes away, so the frame rate is irrelevant;
    /// what matters is noticing promptly once somebody has signed back in.
    /// </remarks>
    private const int SecureDesktopPollMs = 400;

    private readonly Stream _stream;
    private readonly RemotePeer _peer;
    private readonly Dispatcher _dispatcher;
    private readonly RemoteDesktopServer _server;
    private readonly CancellationTokenSource _stopping;
    private readonly Channel<Outgoing> _outbound = System.Threading.Channels.Channel.CreateBounded<Outgoing>(
        new BoundedChannelOptions(OutboundCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

    private readonly object _videoGate = new();
    private IScreenSource? _source;
    private IVideoEncoder? _encoder;
    private RemoteInputInjector? _input;
    private RemoteAudioCapture? _audio;
    private RemoteClipboardBridge? _clipboard;
    private RemoteFileService? _files;
    private PrivacyGuard? _privacy;
    private Thread? _captureThread;

    private RemoteMonitor _monitor;
    private RemoteQualityPreset _quality = RemoteQualityPreset.Balanced;
    private volatile bool _streaming;
    private volatile bool _viewOnly;
    private volatile bool _cursorWanted = true;
    private volatile bool _audioRequested;
    private volatile bool _everStreamed;
    private bool _surfaceReadable = true;

    /// <summary>The codecs the phone says it can decode, best first.</summary>
    /// <remarks>
    /// Empty until the phone asks to start, and the tile encoder needs no entry: it is
    /// what happens when none of these can be produced.
    /// </remarks>
    private IReadOnlyList<string> _codecs = Array.Empty<string>();
    private long _bytesSent;
    private int _framesSent;
    private int _lastEncodeMs;
    private int _lastCaptureMs;
    private bool _disposed;

    internal RemoteDesktopSession(Stream stream, RemotePeer peer, Dispatcher dispatcher, RemoteDesktopServer server, CancellationToken serverStopping)
    {
        _stream = stream;
        _peer = peer;
        _dispatcher = dispatcher;
        _server = server;
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(serverStopping);
        _monitor = RemoteMonitors.Enumerate()[0];
    }

    internal RemotePeer Peer => _peer;
    internal bool Streaming => _streaming;
    internal bool ViewOnly => _viewOnly;
    internal string MonitorName => _monitor.Name;
    internal DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;

    private readonly record struct Outgoing(RemoteDesktopProtocol.Channel Channel, byte Flags, ushort Stream, byte[] Buffer, int Length, bool Pooled);

    // ---- Lifecycle ---------------------------------------------------------------

    internal async Task RunAsync()
    {
        Task writer = Task.Run(WriteLoopAsync);
        try
        {
            await SendControlAsync(new RemoteWelcome(
                "welcome",
                1,
                AppConfig.GetModelDisplayName(),
                RemoteMonitors.Enumerate().Select(monitor => monitor.ToInfo()).ToArray(),
                new RemoteSessionCapabilities(
                    // What this PC will attempt, not what it will end up using. Which
                    // encoder actually starts depends on the phone's own list and on
                    // whether the graphics driver hands one over, so the answer arrives
                    // with "started" rather than here.
                    HardwareVideo: true,
                    Codecs: new[] { "hevc", "h264", "jpeg-tiles" },
                    Audio: RemoteDesktopSettings.AllowAudio,
                    Clipboard: RemoteDesktopSettings.AllowClipboard,
                    FileTransfer: RemoteDesktopSettings.AllowFiles,
                    PrivacyScreen: true,
                    BlockLocalInput: true,
                    Elevated: ProcessHelper.IsUserAdministrator())));

            await ReadLoopAsync();
        }
        catch (OperationCanceledException) { }
        catch (IOException) { /* the phone went away, which is how most sessions end */ }
        catch (Exception ex)
        {
            Logger.WriteLine("Remote session: " + ex.Message);
        }
        finally
        {
            _stopping.Cancel();
            _outbound.Writer.TryComplete();
            try { await writer; } catch { }
            Dispose();
        }
    }

    private async Task ReadLoopAsync()
    {
        byte[] header = new byte[RemoteDesktopProtocol.HeaderSize];
        while (!_stopping.IsCancellationRequested)
        {
            await ReadExactAsync(header, RemoteDesktopProtocol.HeaderSize);
            var (channel, flags, stream, length) = RemoteDesktopProtocol.ReadHeader(header);
            if (length < 0 || length > RemoteDesktopProtocol.MaxFrameBytes)
            {
                throw new InvalidDataException("Remote session frame length out of range.");
            }

            byte[] payload = length == 0 ? Array.Empty<byte>() : ArrayPool<byte>.Shared.Rent(length);
            try
            {
                if (length > 0) await ReadExactAsync(payload, length);

                switch (channel)
                {
                    case RemoteDesktopProtocol.Channel.Control:
                        await HandleControlAsync(payload.AsMemory(0, length));
                        break;
                    case RemoteDesktopProtocol.Channel.Input:
                        _input?.Handle(payload.AsSpan(0, length));
                        break;
                    case RemoteDesktopProtocol.Channel.Clipboard when RemoteDesktopSettings.AllowClipboard:
                        _clipboard?.Receive(Encoding.UTF8.GetString(payload, 0, length));
                        break;
                    case RemoteDesktopProtocol.Channel.Files:
                        if (_files is not null) await _files.HandleAsync(flags, stream, payload, length);
                        break;
                }
            }
            finally
            {
                if (length > 0) ArrayPool<byte>.Shared.Return(payload);
            }
        }
    }

    private async Task ReadExactAsync(byte[] buffer, int count)
    {
        int read = 0;
        while (read < count)
        {
            int got = await _stream.ReadAsync(buffer.AsMemory(read, count - read), _stopping.Token);
            if (got <= 0) throw new EndOfStreamException();
            read += got;
        }
    }

    // ---- Control -----------------------------------------------------------------

    private async Task HandleControlAsync(ReadOnlyMemory<byte> payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement root = document.RootElement;
        string type = root.TryGetProperty("type", out JsonElement value) ? value.GetString() ?? string.Empty : string.Empty;

        switch (type)
        {
            case "start": await StartStreamAsync(root); break;
            case "stop": StopStream(); break;
            case "quality": await ChangeQualityAsync(root); break;
            case "monitor": await ChangeMonitorAsync(root); break;
            case "keyframe": _encoder?.RequestKeyFrame(); break;
            case "cursor": _cursorWanted = Bool(root, "enabled"); await RestartCaptureAsync(); break;
            case "privacy": SetPrivacy(root); break;
            case "lock": PrivacyGuard.LockWorkstation(); break;
            case "clipboard": _clipboard?.Receive(Text(root, "text")); break;
            case "ping": await SendControlAsync(new { type = "pong", id = Text(root, "id") }); break;
            default: await SendControlAsync(new RemoteError("error", "Unsupported request: " + type)); break;
        }
    }

    /// <summary>
    /// Asks whoever is at the machine, then opens the stream.
    /// </summary>
    /// <remarks>
    /// The prompt happens here rather than at the socket because a phone is allowed to
    /// connect, read the monitor list and disconnect again without anybody being
    /// disturbed. What needs consent is the screen, and this is where the screen starts.
    /// </remarks>
    private async Task StartStreamAsync(JsonElement root)
    {
        if (_streaming) StopStream();

        RemoteConsentDecision decision = await _server.RequestConsentAsync(_peer, _stopping.Token);
        if (!decision.Allowed)
        {
            await SendControlAsync(new RemoteConsentMessage("consent", "denied", 0, decision.Reason));
            return;
        }
        await SendControlAsync(new RemoteConsentMessage("consent", "granted", 0, null));

        _viewOnly = decision.ViewOnly || !RemoteDesktopSettings.AllowInput;
        _cursorWanted = !root.TryGetProperty("cursor", out JsonElement cursor) || cursor.ValueKind != JsonValueKind.False;
        _audioRequested = RemoteDesktopSettings.AllowAudio
            && root.TryGetProperty("audio", out JsonElement audio) && audio.ValueKind == JsonValueKind.True;
        _quality = ResolveQuality(root);
        _codecs = ReadCodecs(root);

        var monitors = RemoteMonitors.Enumerate();
        int index = Math.Clamp(Int(root, "monitor"), 0, monitors.Count - 1);
        _monitor = monitors[index];

        await BeginCaptureAsync();
    }

    private RemoteQualityPreset ResolveQuality(JsonElement root)
    {
        RemoteQualityPreset preset = RemoteQualityPreset.ById(Text(root, "preset"));

        // Explicit numbers win over the preset when the phone sends them, so the quality
        // sheet can offer its own slider without needing a preset for every combination.
        int width = Int(root, "maxWidth");
        int height = Int(root, "maxHeight");
        int rate = Int(root, "frameRate");
        return preset with
        {
            MaxWidth = width > 0 ? Math.Clamp(width, 320, 7680) : preset.MaxWidth,
            MaxHeight = height > 0 ? Math.Clamp(height, 240, 4320) : preset.MaxHeight,
            FrameRate = rate > 0 ? Math.Clamp(rate, 1, 60) : preset.FrameRate,
        };
    }

    private async Task BeginCaptureAsync()
    {
        IScreenSource source;
        IVideoEncoder encoder;
        try
        {
            source = new GdiScreenSource(_monitor, _quality.MaxWidth, _quality.MaxHeight, _cursorWanted);

            // A real video codec where this machine has one and the phone can decode
            // it, tiles where it cannot. The order comes from the phone: it is the only
            // side that knows which decoders exist on it, and offering HEVC to a
            // handset without one opens the session onto a black rectangle.
            encoder = MediaFoundationVideoEncoder.TryCreate(_codecs, source.Width, source.Height, _quality.FrameRate, _quality.BitrateKbps)
                ?? (IVideoEncoder)new JpegTileEncoder(source.Width, source.Height, _quality.JpegQuality);
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Remote capture start: " + ex.Message);
            await SendControlAsync(new RemoteError("error", "This PC could not start capturing its screen.", "capture"));
            return;
        }

        lock (_videoGate)
        {
            _source = source;
            _encoder = encoder;
        }

        _input = new RemoteInputInjector(_monitor, !_viewOnly);
        _privacy ??= new PrivacyGuard(_dispatcher);
        _files ??= new RemoteFileService(this, RemoteDesktopSettings.AllowFiles, _stopping.Token);

        if (RemoteDesktopSettings.AllowClipboard && _clipboard is null)
        {
            _clipboard = new RemoteClipboardBridge(_dispatcher, text => _ = SendClipboardAsync(text));
            _clipboard.Start();
        }

        RemoteAudioFormat? audioFormat = null;
        if (RemoteDesktopSettings.AllowAudio && _audioRequested && _audio is null)
        {
            var audio = new RemoteAudioCapture(_quality.Id == RemoteQualityPreset.Economy.Id, OnAudioSamples);
            if (audio.Start())
            {
                _audio = audio;
                audioFormat = audio.Format;
            }
            else audio.Dispose();
        }

        await SendControlAsync(new RemoteStarted("started", encoder.Codec, source.Width, source.Height, _quality.FrameRate, _monitor.Index, _viewOnly, audioFormat));

        _streaming = true;
        _everStreamed = true;
        _server.NotifySessionsChanged();

        // Keeps the machine awake while somebody is driving it. Without this a session
        // over a long download watches the display time out and then the machine sleep.
        RemoteNative.SetThreadExecutionState(RemoteNative.ES_CONTINUOUS | RemoteNative.ES_SYSTEM_REQUIRED | RemoteNative.ES_DISPLAY_REQUIRED);

        _captureThread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = "Arsenal remote capture",
            Priority = ThreadPriority.AboveNormal,
        };
        _captureThread.Start();
    }

    private async Task RestartCaptureAsync()
    {
        if (!_streaming) return;
        StopStream();
        await BeginCaptureAsync();
    }

    private async Task ChangeQualityAsync(JsonElement root)
    {
        _quality = ResolveQuality(root);
        if (root.TryGetProperty("audio", out JsonElement audio)) _audioRequested = audio.ValueKind != JsonValueKind.False;
        await RestartCaptureAsync();
    }

    private async Task ChangeMonitorAsync(JsonElement root)
    {
        var monitors = RemoteMonitors.Enumerate();
        _monitor = monitors[Math.Clamp(Int(root, "monitor"), 0, monitors.Count - 1)];
        await RestartCaptureAsync();
    }

    private void SetPrivacy(JsonElement root)
    {
        _privacy ??= new PrivacyGuard(_dispatcher);
        if (root.TryGetProperty("screen", out JsonElement screen)) _privacy.SetScreenCovered(screen.ValueKind == JsonValueKind.True);
        if (root.TryGetProperty("input", out JsonElement input)) _privacy.SetInputBlocked(input.ValueKind == JsonValueKind.True);
        _server.NotifySessionsChanged();
    }

    private void StopStream()
    {
        _streaming = false;
        Thread? thread = _captureThread;
        _captureThread = null;
        if (thread is not null && thread.IsAlive && !thread.Equals(Thread.CurrentThread)) thread.Join(TimeSpan.FromSeconds(2));

        lock (_videoGate)
        {
            _encoder?.Dispose();
            _encoder = null;
            _source?.Dispose();
            _source = null;
        }

        _input?.ReleaseHeldKeys();
        _input = null;
        _audio?.Dispose();
        _audio = null;
        RemoteNative.SetThreadExecutionState(RemoteNative.ES_CONTINUOUS);
        _server.NotifySessionsChanged();
    }

    // ---- Capture -----------------------------------------------------------------

    private void CaptureLoop()
    {
        var frame = new CapturedFrame();
        var encoded = new EncodedVideoFrame();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        long nextStatsMs = (long)StatsInterval.TotalMilliseconds;
        long lastKeyFrameMs = 0;

        try
        {
            while (_streaming && !_stopping.IsCancellationRequested)
            {
                long tickStart = stopwatch.ElapsedMilliseconds;
                int targetMs = Math.Max(8, 1000 / Math.Max(1, _quality.FrameRate));

                IScreenSource? source;
                IVideoEncoder? encoder;
                lock (_videoGate)
                {
                    source = _source;
                    encoder = _encoder;
                }
                if (source is null || encoder is null) break;

                // The sign-in screen, a UAC prompt and Ctrl+Alt+Del all run on a desktop
                // this process cannot read. Capture does not fail there, it returns
                // black, so without asking first the phone shows a frozen picture and no
                // reason for it.
                bool readable = RemoteNative.IsInputDesktopReadable();
                if (readable != _surfaceReadable)
                {
                    _surfaceReadable = readable;
                    _ = SendControlAsync(new RemoteSurfaceState(
                        "surface",
                        readable ? "live" : "secure",
                        readable ? null : "This PC is showing the Windows sign-in screen. Arsenal runs as you rather than as a service, so it cannot see that screen or type into it. Sign in at the laptop and the session picks up again."));
                    if (readable) encoder.RequestKeyFrame();
                }
                if (!readable)
                {
                    Thread.Sleep(SecureDesktopPollMs);
                    continue;
                }

                long captureStart = stopwatch.ElapsedMilliseconds;
                bool captured = source.TryCapture(frame);
                _lastCaptureMs = (int)(stopwatch.ElapsedMilliseconds - captureStart);

                if (captured)
                {
                    // A phone that joined a still desktop, or one that dropped a frame it
                    // could not decode, would otherwise wait for something to move before
                    // it saw anything at all.
                    if (stopwatch.ElapsedMilliseconds - lastKeyFrameMs > IdleKeyFrameInterval.TotalMilliseconds)
                    {
                        encoder.RequestKeyFrame();
                        lastKeyFrameMs = stopwatch.ElapsedMilliseconds;
                    }

                    long encodeStart = stopwatch.ElapsedMilliseconds;
                    if (encoder.TryEncode(frame, encoded))
                    {
                        _lastEncodeMs = (int)(stopwatch.ElapsedMilliseconds - encodeStart);
                        if ((encoded.Flags & RemoteDesktopProtocol.VideoFlags.KeyFrame) != 0) lastKeyFrameMs = stopwatch.ElapsedMilliseconds;
                        QueueVideo(encoded);
                    }
                }

                if (stopwatch.ElapsedMilliseconds >= nextStatsMs)
                {
                    nextStatsMs = stopwatch.ElapsedMilliseconds + (long)StatsInterval.TotalMilliseconds;
                    PublishStats();
                }

                int elapsed = (int)(stopwatch.ElapsedMilliseconds - tickStart);
                if (elapsed < targetMs) Thread.Sleep(targetMs - elapsed);
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Remote capture loop: " + ex.Message);
        }
    }

    /// <summary>
    /// Hands one coded picture to the writer, or drops it.
    /// </summary>
    /// <remarks>
    /// Dropped rather than queued when the socket is behind, and the encoder is told to
    /// send a keyframe next time so the phone is not left applying tiles to a surface it
    /// no longer has all of. This is the whole of the congestion control: on a LAN there
    /// is nothing subtler worth building, and on anything slower dropping the stale
    /// picture is what a person actually wants.
    /// </remarks>
    private void QueueVideo(EncodedVideoFrame encoded)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(encoded.Length);
        encoded.Data.AsSpan(0, encoded.Length).CopyTo(buffer);

        if (_outbound.Writer.TryWrite(new Outgoing(RemoteDesktopProtocol.Channel.Video, (byte)encoded.Flags, 0, buffer, encoded.Length, true)))
        {
            Interlocked.Increment(ref _framesSent);
            return;
        }

        ArrayPool<byte>.Shared.Return(buffer);
        lock (_videoGate) _encoder?.RequestKeyFrame();
    }

    private void OnAudioSamples(byte[] samples, int length, long timestampUs)
    {
        if (length <= 0 || !_streaming) return;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(length + 8);
        BinaryPrimitives.WriteInt64BigEndian(buffer, timestampUs);
        samples.AsSpan(0, length).CopyTo(buffer.AsSpan(8));

        // Audio is dropped on a full queue for the same reason video is: late samples
        // are worse than missing ones, and a queue of them never catches up.
        if (!_outbound.Writer.TryWrite(new Outgoing(RemoteDesktopProtocol.Channel.Audio, 0, 0, buffer, length + 8, true)))
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void PublishStats()
    {
        int frames = Interlocked.Exchange(ref _framesSent, 0);
        long bytes = Interlocked.Exchange(ref _bytesSent, 0);
        _ = SendControlAsync(new RemoteStats(
            "stats",
            (int)Math.Round(frames / StatsInterval.TotalSeconds),
            (long)Math.Round(bytes / StatsInterval.TotalSeconds),
            _lastEncodeMs,
            _lastCaptureMs,
            _outbound.Reader.Count,
            EncoderLabel()));
    }

    /// <summary>What the statistics line names as the encoder.</summary>
    /// <remarks>
    /// The hardware encoders report the name Windows registered them under, so the
    /// phone can say "NVIDIA H.264 Encoder" rather than just "h264" and anybody looking
    /// at a slow session can see at a glance whether it fell back to the CPU.
    /// </remarks>
    private string EncoderLabel()
    {
        lock (_videoGate)
        {
            return _encoder switch
            {
                MediaFoundationVideoEncoder hardware => hardware.EncoderName,
                null => "none",
                var other => other.Codec,
            };
        }
    }

    // ---- Writing -----------------------------------------------------------------

    public async ValueTask SendAsync(RemoteDesktopProtocol.Channel channel, byte flags, ushort stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, payload.Length));
        payload.Span.CopyTo(buffer);
        try
        {
            await _outbound.Writer.WriteAsync(new Outgoing(channel, flags, stream, buffer, payload.Length, true), cancellationToken);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    private ValueTask SendControlAsync<T>(T message) =>
        SendAsync(RemoteDesktopProtocol.Channel.Control, 0, 0, RemoteDesktopProtocol.Encode(message), _stopping.Token);

    private async Task SendClipboardAsync(string text)
    {
        try { await SendAsync(RemoteDesktopProtocol.Channel.Clipboard, 0, 0, Encoding.UTF8.GetBytes(text), _stopping.Token); }
        catch (Exception ex) { Logger.WriteLine("Remote clipboard send: " + ex.Message); }
    }

    private async Task WriteLoopAsync()
    {
        byte[] header = new byte[RemoteDesktopProtocol.HeaderSize];
        try
        {
            while (await _outbound.Reader.WaitToReadAsync(_stopping.Token))
            {
                while (_outbound.Reader.TryRead(out Outgoing frame))
                {
                    try
                    {
                        RemoteDesktopProtocol.WriteHeader(header, frame.Channel, frame.Flags, frame.Stream, frame.Length);
                        await _stream.WriteAsync(header.AsMemory(0, RemoteDesktopProtocol.HeaderSize), _stopping.Token);
                        if (frame.Length > 0) await _stream.WriteAsync(frame.Buffer.AsMemory(0, frame.Length), _stopping.Token);
                        Interlocked.Add(ref _bytesSent, frame.Length + RemoteDesktopProtocol.HeaderSize);
                    }
                    finally
                    {
                        if (frame.Pooled) ArrayPool<byte>.Shared.Return(frame.Buffer);
                    }
                }
                await _stream.FlushAsync(_stopping.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (Exception ex)
        {
            Logger.WriteLine("Remote write: " + ex.Message);
        }
        finally
        {
            // Anything still queued when the socket died still holds a pooled buffer.
            while (_outbound.Reader.TryRead(out Outgoing leftover))
            {
                if (leftover.Pooled) ArrayPool<byte>.Shared.Return(leftover.Buffer);
            }
            _stopping.Cancel();
        }
    }

    // ---- Helpers -----------------------------------------------------------------

    /// <summary>
    /// The decoders the phone offered, in its order of preference.
    /// </summary>
    /// <remarks>
    /// An older phone sends nothing, and gets tiles, which is what it knows how to
    /// draw. Names it does not recognise are left in the list rather than filtered
    /// here: the encoder factory is the thing that knows which ones mean something.
    /// </remarks>
    private static IReadOnlyList<string> ReadCodecs(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("codecs", out JsonElement codecs)
            || codecs.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var names = new List<string>(codecs.GetArrayLength());
        foreach (JsonElement entry in codecs.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String && entry.GetString() is { Length: > 0 } name) names.Add(name);
        }
        return names;
    }

    private static string Text(JsonElement root, string? name) =>
        name is not null && root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int Int(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int parsed) ? parsed : 0;

    private static bool Bool(JsonElement root, string? name) =>
        name is not null && root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Ends the session from outside, without tearing it down here.
    /// </summary>
    /// <remarks>
    /// Cancelling and closing the socket is enough: the read loop unblocks, RunAsync's
    /// finally disposes everything in the order it was built, and there is one teardown
    /// path rather than two racing each other over the same encoder and thread.
    /// </remarks>
    internal void Stop()
    {
        _stopping.Cancel();
        try { _stream.Close(); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        bool streamed = _everStreamed;
        StopStream();
        _clipboard?.Dispose();
        _clipboard = null;
        _files?.Dispose();
        _files = null;
        _privacy?.Dispose();
        _privacy = null;

        _stopping.Cancel();
        _server.Remove(this);

        // Off unless somebody deliberately turned it on, and only for a session that
        // actually took the screen.
        //
        // The phone asks before it closes instead, which is the better place for the
        // question: the person ending the session is the one who knows whether they are
        // coming back. Locking here as well meant a session ended, the machine locked,
        // and the phone was then looking at the sign-in screen - which this app cannot
        // capture, so the session appeared to have crashed on the way out.
        if (streamed && !_viewOnly && RemoteDesktopSettings.LockOnDisconnect) PrivacyGuard.LockWorkstation();
    }
}
