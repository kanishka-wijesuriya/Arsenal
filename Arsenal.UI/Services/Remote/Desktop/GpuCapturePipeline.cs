using Arsenal.Helpers;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Arsenal.UI.Services.Remote.Desktop;

/// <summary>
/// Capture, scale and encode, with the frame never leaving the graphics card.
/// </summary>
/// <remarks>
/// The three stages are one object because they are not separable: the texture the
/// duplication produces can only be read by the device it came from, so the scaler and
/// the encoder have to be built on that same device. Splitting them behind the old
/// <c>IScreenSource</c> and <c>IVideoEncoder</c> pair would mean handing a texture across
/// a seam that was designed for a byte array, which is how the copies this exists to
/// remove would creep back in.
///
/// <para>What the old path did per frame: read the screen out of video memory, resample
/// it on the processor, walk it again to convert to NV12, walk it a third time to compare
/// it with the last one, then copy it back to the card for the encoder. Measured at 46 to
/// 86 milliseconds on a 3440x1440 desktop, which is a ceiling of twelve to twenty-one
/// frames a second before the encoder has done anything.</para>
///
/// <para>What happens here: the duplication hands over a texture, the video processor
/// scales and converts it in place, and the encoder reads it where it lies. Whether the
/// desktop changed at all is answered by <c>AccumulatedFrames</c> before any of that, at
/// no cost, which is what <c>FrameDigest</c> used to spend a full pass over the frame
/// working out.</para>
/// </remarks>
internal sealed class GpuCapturePipeline : IDisposable
{
    private readonly DesktopDuplicationSource _source;
    private readonly GpuFrameScaler _scaler;
    private readonly MediaFoundationVideoEncoder _encoder;
    private readonly MF.IMFDXGIDeviceManager _manager;
    private readonly Stopwatch _clock = new();
    private long _intervalUs;
    private long _nextDueUs;
    private bool _disposed;

    private GpuCapturePipeline(
        DesktopDuplicationSource source,
        GpuFrameScaler scaler,
        MediaFoundationVideoEncoder encoder,
        MF.IMFDXGIDeviceManager manager,
        int frameRate)
    {
        _source = source;
        _scaler = scaler;
        _encoder = encoder;
        _manager = manager;

        _intervalUs = 1_000_000L / Math.Max(1, frameRate);
    }

    internal string Codec => _encoder.Codec;
    internal string EncoderName => _encoder.EncoderName;

    /// <summary>The size frames are sent at, which is what the phone is told.</summary>
    internal int Width => _scaler.Width;
    internal int Height => _scaler.Height;

    internal bool HasStalled => _encoder.HasStalled;

    internal void RequestKeyFrame() => _encoder.RequestKeyFrame();

    /// <summary>
    /// Changes what the link is being asked to carry, without rebuilding anything.
    /// </summary>
    /// <remarks>
    /// Both levers are cheap here, which is the point of adapting them rather than the
    /// resolution: the bitrate is a property on the running encoder, and the frame rate
    /// is just how often this pipeline decides a frame is due. Neither disturbs the
    /// decoder on the phone.
    /// </remarks>
    internal void SetTarget(int bitrateKbps, int frameRate)
    {
        if (_disposed) return;
        if (bitrateKbps > 0) _encoder.TrySetBitrate(bitrateKbps);
        if (frameRate > 0) Volatile.Write(ref _intervalUs, 1_000_000L / frameRate);
    }

    /// <summary>
    /// Builds the whole path, or returns null so the caller uses the desktop copy one.
    /// </summary>
    /// <remarks>
    /// Every reason to return null is ordinary: a combined virtual monitor, a driver with
    /// no duplication, a machine whose encoders cannot take textures. None of them is an
    /// error worth ending a session over, and all of them are handled by the path that
    /// always works.
    /// </remarks>
    internal static GpuCapturePipeline? TryCreate(
        RemoteMonitor monitor,
        int maxWidth,
        int maxHeight,
        int frameRate,
        int bitrateKbps,
        IReadOnlyList<string> codecs)
    {
        DesktopDuplicationSource? source = null;
        GpuFrameScaler? scaler = null;
        MediaFoundationVideoEncoder? encoder = null;
        MF.IMFDXGIDeviceManager? manager = null;

        try
        {
            source = DesktopDuplicationSource.TryCreate(monitor);
            if (source is null) return null;

            (int targetWidth, int targetHeight) = FitWithin(source.Width, source.Height, maxWidth, maxHeight);

            if (MF.MFCreateDXGIDeviceManager(out uint resetToken, out manager) != MF.S_OK || manager is null)
            {
                Logger.WriteLine("Gpu pipeline: no device manager; using the desktop copy path.");
                return null;
            }

            int reset = manager.ResetDevice(source.Device, resetToken);
            if (reset != MF.S_OK)
            {
                Logger.WriteLine($"Gpu pipeline: the device manager refused the device (0x{reset:X8}).");
                return null;
            }

            scaler = GpuFrameScaler.TryCreate(manager, source.Width, source.Height, targetWidth, targetHeight);
            if (scaler is null) return null;

            encoder = MediaFoundationVideoEncoder.TryCreate(codecs, targetWidth, targetHeight, frameRate, bitrateKbps, manager);
            if (encoder is null)
            {
                Logger.WriteLine("Gpu pipeline: no encoder takes textures; using the desktop copy path.");
                return null;
            }

            var pipeline = new GpuCapturePipeline(source, scaler, encoder, manager, frameRate);

            // The rule this codebase paid for: configuration succeeding proves nothing.
            // An encoder that accepted every media type and the Direct3D device can still
            // never ask for a frame, and the symptom is a session that connects to a
            // black screen with nothing in the log. One real captured frame through the
            // real scaler into the real encoder is the only answer worth having.
            if (!pipeline.ProducesFrames())
            {
                Logger.WriteLine("Gpu pipeline: configured but coded nothing; using the desktop copy path.");
                pipeline.Dispose();
                source = null;
                scaler = null;
                encoder = null;
                manager = null;
                return null;
            }

            source = null;
            scaler = null;
            encoder = null;
            manager = null;

            Logger.WriteLine($"Gpu pipeline: {monitor.Name} {pipeline.Width}x{pipeline.Height} {pipeline.Codec} "
                + $"via {pipeline.EncoderName}, no copies through system memory.");
            return pipeline;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Gpu pipeline: " + ex.Message);
            return null;
        }
        finally
        {
            encoder?.Dispose();
            scaler?.Dispose();
            source?.Dispose();
            if (manager is not null) Marshal.ReleaseComObject(manager);
        }
    }

    /// <summary>
    /// Scales the captured size down to the quality cap, keeping the shape and staying
    /// even on both axes.
    /// </summary>
    /// <remarks>
    /// Even dimensions for the same reason as the old path: chroma is subsampled two
    /// pixels at a time and every hardware encoder wants a pair.
    /// </remarks>
    private static (int Width, int Height) FitWithin(int width, int height, int maxWidth, int maxHeight)
    {
        double scale = Math.Min(1d, Math.Min(
            maxWidth <= 0 ? 1d : (double)maxWidth / width,
            maxHeight <= 0 ? 1d : (double)maxHeight / height));

        int target = Even(Math.Max(2, (int)Math.Round(width * scale)));
        int targetHeight = Even(Math.Max(2, (int)Math.Round(height * scale)));
        return (target, targetHeight);
    }

    private static int Even(int value) => value % 2 == 0 ? value : value - 1;

    /// <summary>
    /// Waits for the desktop to change, then produces one coded picture.
    /// </summary>
    /// <param name="timeoutMs">
    /// How long to wait for a change. This is the pacing: there is no sleep anywhere in
    /// this path, because the duplication returns exactly when there is something to
    /// send.
    /// </param>
    /// <returns>
    /// False when there is nothing to send this time, which is the normal answer for a
    /// screen nobody is touching.
    /// </returns>
    internal bool TryProduce(uint timeoutMs, EncodedVideoFrame encoded, out int captureMs, out int encodeMs, out bool alive)
    {
        captureMs = 0;
        encodeMs = 0;
        alive = true;

        if (_disposed) { alive = false; return false; }

        _clock.Restart();
        if (!_source.TryAcquire(timeoutMs, out DuplicatedFrame frame))
        {
            alive = false;
            return false;
        }

        if (!frame.HasImage)
        {
            captureMs = (int)_clock.ElapsedMilliseconds;
            return false;
        }

        _acquired++;

        MF.IMFSample? converted = null;
        try
        {
            // The frame rate cap, enforced by throwing frames away rather than by
            // sleeping. The duplication returns whenever the desktop changes, which on a
            // 155Hz panel is a great deal more often than any preset asks for, and an
            // acquire that is dropped here costs a fraction of a millisecond. Sleeping
            // instead would mean waking on a timer again, which is what the old path did
            // and why it could never hit its own target.
            if (frame.TimestampUs < _nextDueUs && !_encoder.KeyFrameWanted)
            {
                captureMs = (int)_clock.ElapsedMilliseconds;
                return false;
            }

            // Nothing but the pointer moved. The old path found this out by hashing the
            // whole frame; here the duplication already said so.
            if (frame.AccumulatedFrames == 0 && !_encoder.KeyFrameWanted)
            {
                _skippedUnchanged++;
                captureMs = (int)_clock.ElapsedMilliseconds;
                return false;
            }

            converted = _scaler.TryConvert(frame.Texture, frame.TimestampUs);
            captureMs = (int)_clock.ElapsedMilliseconds;
            if (converted is null) return false;
            _converted++;
        }
        finally
        {
            // Given back as soon as the scaler has read it. The converted sample is the
            // processor's own texture and does not depend on this one, and holding the
            // frame any longer blocks the next acquire.
            _source.ReleaseFrame();
        }

        try
        {
            _clock.Restart();
            bool produced = _encoder.TryEncodeSample(converted, frame.TimestampUs, encoded);
            encodeMs = (int)_clock.ElapsedMilliseconds;
            if (produced)
            {
                _coded++;

                // Advance the schedule rather than measure from this frame. Counting
                // from when a frame happened to arrive loses the remainder every time
                // and the rate settles below the target: 155Hz arrivals against a 60fps
                // interval measured that way gave 53.7. Carrying the due time forward
                // keeps the average right, and the clamp stops it trying to catch up
                // with a burst after the screen has been still.
                _nextDueUs += _intervalUs;
                if (_nextDueUs < frame.TimestampUs) _nextDueUs = frame.TimestampUs + _intervalUs;
            }
            return produced;
        }
        finally
        {
            Marshal.ReleaseComObject(converted);
        }
    }

    /// <summary>Pushes real frames through until one comes back coded.</summary>
    /// <remarks>
    /// Given a generous number of attempts because the first few acquires after a
    /// duplication opens are often pointer-only, and because an encoder is entitled to
    /// hold the first frame or two back before it emits anything.
    /// </remarks>
    private bool ProducesFrames()
    {
        var encoded = new EncodedVideoFrame();
        RequestKeyFrame();

        for (int attempt = 0; attempt < 60; attempt++)
        {
            if (TryProduce(50, encoded, out _, out _, out bool alive) && encoded.Length > 0) return true;
            if (!alive) break;
        }

        // Where it stopped, because "coded nothing" on its own sends the next person
        // through all three stages looking for which one was silent.
        Logger.WriteLine($"Gpu pipeline probe: {_acquired} acquired, {_converted} scaled, {_coded} coded"
            + $" (skipped {_skippedUnchanged} unchanged).");
        return false;
    }

    // Counters for the validation above only. They cost one increment on a path that is
    // already doing a colour conversion, and they are the difference between a diagnosis
    // and a guess on a machine nobody here can see.
    private int _acquired;
    private int _converted;
    private int _coded;
    private int _skippedUnchanged;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Encoder first: it holds the device manager, and releasing the device out from
        // under a transform that is still streaming is the race this order avoids.
        _encoder.Dispose();
        _scaler.Dispose();
        _source.Dispose();
        try { Marshal.ReleaseComObject(_manager); } catch { }
    }
}
