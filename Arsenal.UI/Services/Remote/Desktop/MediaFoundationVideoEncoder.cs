using Arsenal.Application.Models;
using Arsenal.Helpers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Arsenal.UI.Services.Remote.Desktop;

/// <summary>
/// Codes the screen as H.264 or HEVC, on the graphics card where there is one.
/// </summary>
/// <remarks>
/// The tile encoder sends pictures; this sends the difference between pictures, which
/// is a different order of magnitude. A window dragged across a 1440p desktop is a
/// megabyte a frame as JPEG rectangles and about thirty kilobytes as H.264, because the
/// codec can say "these pixels moved here" where JPEG can only say "here they are
/// again". That is the whole reason a remote desktop feels like a video call rather
/// than a slideshow.
///
/// <para><b>Which encoder.</b> Media Foundation is asked for every registered video
/// encoder that produces the wanted subtype, hardware first. On this class of machine
/// that is NVENC or Quick Sync, both of which code a 1440p frame in one or two
/// milliseconds and cost the CPU nothing. Where there is no hardware encoder the
/// Microsoft software one is next in the list and still far better than tiles. Where
/// there is neither, or where configuring one fails, the session falls back to the tile
/// encoder rather than failing.</para>
///
/// <para><b>Asynchronous transforms.</b> Every hardware encoder is an async MFT, which
/// does not encode when asked but raises an event when it wants a frame and another
/// when it has one ready. Reading those with GetEvent looks right and returns nothing:
/// the drivers only start queueing once a callback is registered, so
/// <see cref="MediaFoundationEventPump"/> registers one and turns the callbacks into
/// semaphores the capture thread waits on. A synchronous transform has no event queue
/// and skips all of it.</para>
///
/// <para><b>Latency.</b> Encoders default to buffering several frames to code them
/// better. For a screen somebody is typing at, a frame that arrives correct and 100 ms
/// late is worse than one that arrives now, so low latency is requested and B-frames
/// are left off.</para>
/// </remarks>
internal sealed class MediaFoundationVideoEncoder : IVideoEncoder
{
    /// <summary>
    /// How long to wait on the encoder before giving up on a frame.
    /// </summary>
    /// <remarks>
    /// A hardware encoder answers in single milliseconds. Anything approaching this is
    /// a driver that has stopped, and the right response is to skip the frame rather
    /// than wedge the capture thread for the length of the session.
    /// </remarks>
    private static readonly TimeSpan EventTimeout = TimeSpan.FromMilliseconds(400);

    /// <summary>How long to wait for a coded picture before giving up on this frame.</summary>
    /// <remarks>
    /// Shorter than the input timeout on purpose. An encoder holding a frame back is
    /// normal and the next call collects two; waiting 400 ms each time an encoder
    /// chose to buffer would cap the session at two frames a second.
    /// </remarks>
    private static readonly TimeSpan FirstOutputTimeout = TimeSpan.FromMilliseconds(40);

    private const uint InputStream = 0;
    private const uint OutputStream = 0;

    /// <summary>MF counts in 100-nanosecond units.</summary>
    private const long TicksPerSecond = 10_000_000;

    private static readonly object StartupGate = new();
    private static bool _platformStarted;
    private static bool _platformUnavailable;

    /// <summary>How many encoders are alive and using the platform.</summary>
    /// <remarks>
    /// Only encoders handed to a session count. The candidates built and thrown away
    /// during enumeration never do, so a rejected one cannot take the count below what
    /// is actually running.
    /// </remarks>
    private static int _liveEncoders;

    /// <summary>Encoder discovery/configuration operations currently using the platform.</summary>
    private static int _platformOperations;

    /// <summary>Whether this encoder is one of the counted ones.</summary>
    private bool _counted;

    private readonly MF.IMFTransform _transform;
    private readonly MediaFoundationEventPump? _pump;
    private readonly bool _async;
    private readonly int _outputBufferSize;
    private readonly long _frameDuration;
    private byte[] _nv12;
    private byte[] _output = new byte[256 * 1024];
    private byte[]? _codecConfiguration;
    private bool _configurationSent;
    private bool _keyFrameWanted = true;
    private long _frameIndex;
    private ulong _lastDigest;
    private bool _disposed;

    private MediaFoundationVideoEncoder(
        MF.IMFTransform transform,
        bool async,
        string codec,
        string encoderName,
        int width,
        int height,
        int frameRate,
        int outputBufferSize,
        byte[]? configuration)
    {
        _transform = transform;
        _async = async;
        _pump = async && transform is MF.IMFMediaEventGenerator generator ? new MediaFoundationEventPump(generator) : null;
        _outputBufferSize = Math.Max(outputBufferSize, 64 * 1024);
        _frameDuration = TicksPerSecond / Math.Max(1, frameRate);
        _nv12 = new byte[Nv12Converter.BufferSize(width, height)];
        _codecConfiguration = configuration;

        Codec = codec;
        EncoderName = encoderName;
        Width = width;
        Height = height;
    }

    public string Codec { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>The name Windows gives this encoder, for the session's statistics.</summary>
    internal string EncoderName { get; }

    public void RequestKeyFrame() => _keyFrameWanted = true;

    /// <summary>
    /// Registers the event callback, then starts the transform streaming.
    /// </summary>
    /// <remarks>
    /// The order matters and is not interchangeable. An async transform raises its
    /// first METransformNeedInput from inside BEGIN_STREAMING, so a callback registered
    /// afterwards misses it, and both sides then wait for the other.
    /// </remarks>
    private bool Begin()
    {
        if (_pump is not null && !_pump.Start()) return false;
        if (_transform.ProcessMessage(MF.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero) != MF.S_OK) return false;
        if (_transform.ProcessMessage(MF.MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero) != MF.S_OK) return false;
        return true;
    }

    // ---- Creation ----------------------------------------------------------------

    /// <summary>
    /// Builds an encoder for the first codec in <paramref name="preferred"/> this
    /// machine can actually produce.
    /// </summary>
    /// <remarks>
    /// The phone sends the list, because it is the side that knows what it can decode;
    /// a desktop that guessed would offer HEVC to a phone whose decoder does not exist
    /// and the session would open to a black rectangle. Returns null when none of them
    /// work, which the caller reads as "use the tile encoder".
    /// </remarks>
    internal static MediaFoundationVideoEncoder? TryCreate(IReadOnlyList<string> preferred, int width, int height, int frameRate, int bitrateKbps)
    {
        if (width <= 0 || height <= 0 || (width & 1) != 0 || (height & 1) != 0) return null;

        Interlocked.Increment(ref _platformOperations);
        try
        {
            foreach (string codec in preferred)
            {
                Guid subtype;
                if (string.Equals(codec, "hevc", StringComparison.OrdinalIgnoreCase)) subtype = MF.MFVideoFormat_HEVC;
                else if (string.Equals(codec, "h264", StringComparison.OrdinalIgnoreCase)) subtype = MF.MFVideoFormat_H264;
                else continue;

                MediaFoundationVideoEncoder? encoder = TryCreate(codec, subtype, width, height, frameRate, bitrateKbps);
                if (encoder is not null) return encoder;
            }
            return null;
        }
        finally
        {
            Interlocked.Decrement(ref _platformOperations);
        }
    }

    private static MediaFoundationVideoEncoder? TryCreate(string codec, Guid subtype, int width, int height, int frameRate, int bitrateKbps)
    {
        if (!Startup()) return null;

        IntPtr activateArray = IntPtr.Zero;
        try
        {
            var output = new MF.MFT_REGISTER_TYPE_INFO { guidMajorType = MF.MFMediaType_Video, guidSubtype = subtype };
            int hr = MF.MFTEnumEx(
                MF.MFT_CATEGORY_VIDEO_ENCODER,
                MF.MFT_ENUM_FLAG_HARDWARE | MF.MFT_ENUM_FLAG_ASYNCMFT | MF.MFT_ENUM_FLAG_SYNCMFT | MF.MFT_ENUM_FLAG_SORTANDFILTER,
                null,
                output,
                out activateArray,
                out uint count);
            if (hr != MF.S_OK || count == 0) return null;

            for (uint i = 0; i < count; i++)
            {
                IntPtr activatePointer = Marshal.ReadIntPtr(activateArray, (int)i * IntPtr.Size);
                if (activatePointer == IntPtr.Zero) continue;

                var activate = (MF.IMFActivate)Marshal.GetObjectForIUnknown(activatePointer);
                try
                {
                    MediaFoundationVideoEncoder? encoder = TryActivate(activate, codec, width, height, frameRate, bitrateKbps);
                    if (encoder is not null) return encoder;
                }
                catch (Exception ex)
                {
                    Logger.WriteLine("Remote encoder candidate: " + ex.Message);
                }
                finally
                {
                    Marshal.ReleaseComObject(activate);
                    Marshal.Release(activatePointer);
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Remote encoder enumeration: " + ex.Message);
            return null;
        }
        finally
        {
            if (activateArray != IntPtr.Zero) MF.CoTaskMemFree(activateArray);
        }
    }

    private static MediaFoundationVideoEncoder? TryActivate(MF.IMFActivate activate, string codec, int width, int height, int frameRate, int bitrateKbps)
    {
        string name = ReadFriendlyName(activate);
        Guid transformId = typeof(MF.IMFTransform).GUID;
        if (activate.ActivateObject(ref transformId, out object instance) != MF.S_OK || instance is not MF.IMFTransform transform)
        {
            return null;
        }

        bool configured = false;
        try
        {
            // An async transform refuses every call until it is told the caller knows
            // it is async. A sync one has no attribute store worth reading here.
            bool async = false;
            if (transform.GetAttributes(out MF.IMFAttributes attributes) == MF.S_OK && attributes is not null)
            {
                try
                {
                    Guid asyncKey = MF.MF_TRANSFORM_ASYNC;
                    async = attributes.GetUINT32(ref asyncKey, out uint isAsync) == MF.S_OK && isAsync != 0;
                    if (async)
                    {
                        Guid unlock = MF.MF_TRANSFORM_ASYNC_UNLOCK;
                        attributes.SetUINT32(ref unlock, 1);
                    }

                    // Ask for the frame in front of us rather than the best frame. An
                    // encoder left to its own devices holds several back to code them
                    // together, which on a screen being typed at is latency for nothing.
                    Guid lowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
                    attributes.SetUINT32(ref lowLatency, 1);
                }
                finally
                {
                    Marshal.ReleaseComObject(attributes);
                }
            }

            if (async && transform is not MF.IMFMediaEventGenerator) return null;

            // Output type first. An encoder cannot describe the input it wants until it
            // knows what it is being asked to produce, and setting input first fails.
            Guid subtype = string.Equals(codec, "hevc", StringComparison.OrdinalIgnoreCase) ? MF.MFVideoFormat_HEVC : MF.MFVideoFormat_H264;
            if (!TrySetOutputType(transform, subtype, codec, width, height, frameRate, bitrateKbps)) return null;
            if (!TrySetInputType(transform, width, height, frameRate)) return null;

            if (transform.GetOutputStreamInfo(OutputStream, out MF.MFT_OUTPUT_STREAM_INFO info) != MF.S_OK) return null;
            byte[]? configuration = ReadSequenceHeader(transform);

            ApplyRateControl(transform, bitrateKbps, frameRate);

            var encoder = new MediaFoundationVideoEncoder(transform, async, codec, name, width, height, frameRate, (int)info.cbSize, configuration);

            // The callback has to be registered before streaming begins, or the first
            // METransformNeedInput is raised into a queue nobody is reading and the
            // encoder waits for a frame this side is waiting to be asked for.
            // Configuration succeeding is not evidence that the encoder works. On this
            // class of machine the Intel H.264 transform accepts every media type,
            // reports itself ready, and then never asks for a frame, so a session built
            // on it opens to a black screen with nothing in the log. One real frame
            // through the whole path is the only answer that means anything.
            if (!encoder.Begin() || !encoder.ProducesFrames(width, height))
            {
                Logger.WriteLine($"Remote encoder: {name} configured but produced nothing, trying the next one");
                encoder.Dispose();
                configured = true; // Dispose released the transform already.
                return null;
            }

            configured = true;

            // This one is going to a session, so it is one of the encoders the platform
            // is being held open for.
            encoder._counted = true;
            Interlocked.Increment(ref _liveEncoders);

            Logger.WriteLine($"Remote encoder: {name} producing {codec} at {width}x{height}, {frameRate} fps, {bitrateKbps} kbps"
                + (async ? " (hardware, asynchronous)" : " (synchronous)"));
            return encoder;
        }
        finally
        {
            if (!configured) Marshal.ReleaseComObject(transform);
        }
    }

    /// <summary>
    /// Asks the codec to spend bits on movement rather than on filling a quota.
    /// </summary>
    /// <remarks>
    /// A media type's average bitrate is read by every encoder as constant bitrate, so
    /// a desktop nobody is touching is padded to the full rate to make the average come
    /// out right. That is the opposite of what a remote session wants: a still screen
    /// should cost almost nothing and a dragged window should be allowed to spend.
    ///
    /// <para>Quality-based rate control first, unconstrained variable bitrate second,
    /// and whatever the encoder was already doing if it supports neither. The bitrate
    /// stays configured as a ceiling either way, so a pathological screen cannot
    /// saturate the network.</para>
    ///
    /// <para>The keyframe interval is long on purpose. A keyframe is by far the most
    /// expensive thing in the stream, the session asks for one whenever the phone
    /// actually needs it, and the capture loop already forces one every ten seconds.</para>
    /// </remarks>
    private static void ApplyRateControl(MF.IMFTransform transform, int bitrateKbps, int frameRate)
    {
        if (transform is not MF.ICodecAPI codec) return;

        void Set(Guid api, uint value)
        {
            try
            {
                Guid key = api;
                if (codec.IsSupported(ref key) != MF.S_OK) return;
                MF.VARIANT variant = MF.VARIANT.FromUInt32(value);
                codec.SetValue(ref key, ref variant);
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Remote encoder setting: " + ex.Message);
            }
        }

        // Low-delay variable bitrate, and specifically not the two modes that read as
        // more obviously right.
        //
        // Measured on Intel's hardware HEVC transform, feeding it sixty frames of a
        // white block sliding across a grey desktop:
        //
        //   left alone (constant bitrate)   2588 KB   the encoder pads a still screen
        //   quality mode, quality 70            2 KB   skipped frames, no keyframe at all
        //   unconstrained VBR                   2 KB   same, with or without a mean rate
        //   low-delay VBR                    2588 KB   correct
        //
        // Two of the three modes that ought to work produce a stream that decodes to
        // nothing while reporting sixty frames a second, which is the worst way for
        // this to fail: the session looks healthy and the screen is blank. Low-delay
        // VBR is also the mode this actually wants, being the one meant for a picture
        // somebody is interacting with rather than one being written to a file.
        uint bitrate = (uint)Math.Clamp(bitrateKbps, 500, 100_000) * 1000;
        Set(MF.CODECAPI_AVEncCommonRateControlMode, MF.eAVEncCommonRateControlMode_LowDelayVBR);
        Set(MF.CODECAPI_AVEncCommonMeanBitRate, bitrate);
        Set(MF.CODECAPI_AVEncCommonMaxBitRate, bitrate);
        Set(MF.CODECAPI_AVEncMPVGOPSize, (uint)Math.Max(frameRate, 1) * 10);
        Set(MF.CODECAPI_AVLowLatencyMode, 1);
    }

    private static bool TrySetOutputType(MF.IMFTransform transform, Guid subtype, string codec, int width, int height, int frameRate, int bitrateKbps)
    {
        if (MF.MFCreateMediaType(out MF.IMFMediaType type) != MF.S_OK) return false;
        try
        {
            Guid major = MF.MF_MT_MAJOR_TYPE, video = MF.MFMediaType_Video;
            Guid subtypeKey = MF.MF_MT_SUBTYPE;
            Guid bitrate = MF.MF_MT_AVG_BITRATE;
            Guid frameSize = MF.MF_MT_FRAME_SIZE;
            Guid rate = MF.MF_MT_FRAME_RATE;
            Guid aspect = MF.MF_MT_PIXEL_ASPECT_RATIO;
            Guid interlace = MF.MF_MT_INTERLACE_MODE;
            Guid profile = MF.MF_MT_MPEG2_PROFILE;
            Guid matrix = MF.MF_MT_YUV_MATRIX;
            Guid range = MF.MF_MT_VIDEO_NOMINAL_RANGE;

            if (type.SetGUID(ref major, ref video) != MF.S_OK) return false;
            if (type.SetGUID(ref subtypeKey, ref subtype) != MF.S_OK) return false;
            if (type.SetUINT32(ref bitrate, (uint)Math.Clamp(bitrateKbps, 500, 100_000) * 1000) != MF.S_OK) return false;
            if (type.SetUINT64(ref frameSize, MF.Pack((uint)width, (uint)height)) != MF.S_OK) return false;
            if (type.SetUINT64(ref rate, MF.Pack((uint)frameRate, 1)) != MF.S_OK) return false;
            if (type.SetUINT64(ref aspect, MF.Pack(1, 1)) != MF.S_OK) return false;
            if (type.SetUINT32(ref interlace, MF.MFVideoInterlace_Progressive) != MF.S_OK) return false;

            // Written into the stream's own VUI so the phone's decoder does not have to
            // guess, which it otherwise does from the picture height and gets wrong for
            // anything under 720p. The conversion feeding this is BT.709 limited range.
            type.SetUINT32(ref matrix, MF.MFVideoTransferMatrix_BT709);
            type.SetUINT32(ref range, MF.MFNominalRange_16_235);

            type.SetUINT32(ref profile, string.Equals(codec, "hevc", StringComparison.OrdinalIgnoreCase)
                ? MF.eAVEncH265VProfile_Main_420_8
                : MF.eAVEncH264VProfile_High);

            return transform.SetOutputType(OutputStream, type, 0) == MF.S_OK;
        }
        finally
        {
            Marshal.ReleaseComObject(type);
        }
    }

    private static bool TrySetInputType(MF.IMFTransform transform, int width, int height, int frameRate)
    {
        if (MF.MFCreateMediaType(out MF.IMFMediaType type) != MF.S_OK) return false;
        try
        {
            Guid major = MF.MF_MT_MAJOR_TYPE, video = MF.MFMediaType_Video;
            Guid subtypeKey = MF.MF_MT_SUBTYPE, nv12 = MF.MFVideoFormat_NV12;
            Guid frameSize = MF.MF_MT_FRAME_SIZE;
            Guid rate = MF.MF_MT_FRAME_RATE;
            Guid aspect = MF.MF_MT_PIXEL_ASPECT_RATIO;
            Guid interlace = MF.MF_MT_INTERLACE_MODE;

            if (type.SetGUID(ref major, ref video) != MF.S_OK) return false;
            if (type.SetGUID(ref subtypeKey, ref nv12) != MF.S_OK) return false;
            if (type.SetUINT64(ref frameSize, MF.Pack((uint)width, (uint)height)) != MF.S_OK) return false;
            if (type.SetUINT64(ref rate, MF.Pack((uint)frameRate, 1)) != MF.S_OK) return false;
            if (type.SetUINT64(ref aspect, MF.Pack(1, 1)) != MF.S_OK) return false;
            if (type.SetUINT32(ref interlace, MF.MFVideoInterlace_Progressive) != MF.S_OK) return false;

            return transform.SetInputType(InputStream, type, 0) == MF.S_OK;
        }
        finally
        {
            Marshal.ReleaseComObject(type);
        }
    }

    /// <summary>
    /// The parameter sets, if the encoder publishes them up front.
    /// </summary>
    /// <remarks>
    /// A decoder needs the sequence and picture parameter sets before it can decode
    /// anything. Most encoders also put them in front of every keyframe, so this is
    /// belt and braces - but a phone that joins and is handed them immediately starts
    /// decoding on the first keyframe rather than the second.
    /// </remarks>
    private static byte[]? ReadSequenceHeader(MF.IMFTransform transform)
    {
        if (transform.GetOutputCurrentType(OutputStream, out MF.IMFMediaType? type) != MF.S_OK || type is null) return null;
        try
        {
            Guid header = new("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3"); // MF_MT_MPEG_SEQUENCE_HEADER
            if (type.GetBlobSize(ref header, out uint size) != MF.S_OK || size == 0 || size > 4096) return null;

            byte[] buffer = new byte[size];
            return type.GetBlob(ref header, buffer, size, IntPtr.Zero) == MF.S_OK ? buffer : null;
        }
        catch { return null; }
        finally
        {
            Marshal.ReleaseComObject(type);
        }
    }

    private static string ReadFriendlyName(MF.IMFActivate activate)
    {
        try
        {
            Guid key = MF.MFT_FRIENDLY_NAME_Attribute;
            if (activate.GetStringLength(ref key, out uint length) != MF.S_OK || length == 0) return "Video encoder";
            var text = new StringBuilder((int)length + 1);
            return activate.GetString(ref key, text, length + 1, IntPtr.Zero) == MF.S_OK ? text.ToString() : "Video encoder";
        }
        catch { return "Video encoder"; }
    }

    /// <summary>Asks the codec for an IDR on the frame about to go in.</summary>
    private void ForceKeyFrame()
    {
        if (_transform is not MF.ICodecAPI codec) return;
        try
        {
            Guid key = MF.CODECAPI_AVEncVideoForceKeyFrame;
            if (codec.IsSupported(ref key) != MF.S_OK) return;
            MF.VARIANT one = MF.VARIANT.FromUInt32(1);
            codec.SetValue(ref key, ref one);
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Remote encoder keyframe: " + ex.Message);
        }
    }

    /// <summary>
    /// Pushes frames through until a coded picture comes back.
    /// </summary>
    /// <remarks>
    /// The acceptance test for a candidate encoder, and the only thing standing between
    /// a session and a black screen. An encoder can configure perfectly and then never
    /// ask for a frame: the NVIDIA transform does exactly that whenever the dedicated
    /// GPU is parked, which on this class of laptop is most of the time.
    ///
    /// <para>Two things it has to get right, both learned by getting them wrong:</para>
    /// <list type="bullet">
    /// <item>A configuration frame is not a picture. It is produced without the encoder
    /// being involved at all, so accepting it tests nothing. This is what let a dead
    /// encoder through once the parameter sets became readable.</item>
    /// <item>Each probe frame has to differ from the one before. They pass through the
    /// same change detection as real frames, so identical frames are dropped before the
    /// encoder sees them and every attempt after the first tests nothing.</item>
    /// </list>
    ///
    /// <para>The frames are discarded, but the keyframe request is reinstated
    /// afterwards so the first real frame of the session is still self contained.</para>
    /// </remarks>
    private bool ProducesFrames(int width, int height)
    {
        var probe = new CapturedFrame
        {
            Width = width,
            Height = height,
            Stride = width * 4,
            Pixels = new byte[width * 4 * height],
        };
        var discarded = new EncodedVideoFrame();

        try
        {
            for (int attempt = 0; attempt < ProbeFrames; attempt++)
            {
                // Somewhere different each time, so the change detector lets it past.
                probe.Pixels[attempt * 4] = (byte)(40 + attempt * 50);
                probe.TimestampUs = attempt * 33_333L;

                if (TryEncode(probe, discarded)
                    && (discarded.Flags & RemoteDesktopProtocol.VideoFlags.Configuration) == 0)
                {
                    return true;
                }

                // An encoder that has ignored two requests for a frame is not going to
                // answer the next two. Giving up here keeps a dead candidate to under a
                // second, which matters when there are several to get through before
                // the session can fall back to tiles.
                if (Volatile.Read(ref _inputTimeouts) >= 2) break;
            }
            return false;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Remote encoder probe: " + ex.Message);
            return false;
        }
        finally
        {
            // The frame counter deliberately keeps running. It is what the sample
            // timestamps are built from, and an encoder handed a timestamp it has
            // already seen treats the frame as a duplicate and codes nothing: the
            // symptom is a session that connects, reports sixty frames a second and
            // sends seventeen bytes a frame of skipped pictures.
            _configurationSent = false;
            RequestKeyFrame();
        }
    }

    // ---- Encoding ----------------------------------------------------------------

    public bool TryEncode(CapturedFrame frame, EncodedVideoFrame encoded)
    {
        if (_disposed || frame.Width != Width || frame.Height != Height) return false;

        // The parameter sets go first and on their own, so the phone can configure its
        // decoder before the first picture rather than after it.
        if (!_configurationSent && _codecConfiguration is { Length: > 0 })
        {
            _configurationSent = true;
            EnsureOutput(_codecConfiguration.Length + 8);
            BinaryPrimitives.WriteInt64BigEndian(_output, frame.TimestampUs);
            _codecConfiguration.CopyTo(_output.AsSpan(8));
            encoded.Data = _output;
            encoded.Length = _codecConfiguration.Length + 8;
            encoded.Flags = RemoteDesktopProtocol.VideoFlags.Configuration;
            encoded.TimestampUs = frame.TimestampUs;
            return true;
        }

        try
        {
            var pixels = frame.Pixels.AsSpan(0, frame.Stride * frame.Height);

            // A frame identical to the last one is dropped here rather than handed to
            // the encoder, which would pad it to the configured bitrate rather than
            // send nothing. See FrameDigest for the measurements. A keyframe request
            // always goes through, because that is a phone asking to be resynchronised
            // and the screen not having changed is exactly when it needs one.
            ulong digest = FrameDigest.Compute(pixels, frame.Stride, Width, Height);
            if (!_keyFrameWanted && digest == _lastDigest) return false;
            _lastDigest = digest;

            Nv12Converter.Convert(pixels, frame.Stride, Width, Height, _nv12);
            if (!Submit(frame.TimestampUs)) return false;
            return Drain(frame.TimestampUs, encoded);
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Remote encode: " + ex.Message);
            return false;
        }
    }

    private bool Submit(long timestampUs)
    {
        if (_async && !WaitForNeedInput()) return false;

        if (MF.MFCreateMemoryBuffer(_nv12.Length, out MF.IMFMediaBuffer buffer) != MF.S_OK) return false;
        try
        {
            if (buffer.Lock(out IntPtr target, out _, out _) != MF.S_OK) return false;
            try { Marshal.Copy(_nv12, 0, target, _nv12.Length); }
            finally { buffer.Unlock(); }
            if (buffer.SetCurrentLength(_nv12.Length) != MF.S_OK) return false;

            if (MF.MFCreateSample(out MF.IMFSample sample) != MF.S_OK) return false;
            try
            {
                if (sample.AddBuffer(buffer) != MF.S_OK) return false;
                sample.SetSampleTime(_frameIndex * _frameDuration);
                sample.SetSampleDuration(_frameDuration);
                _frameIndex++;

                if (_keyFrameWanted)
                {
                    _keyFrameWanted = false;

                    // Both, because neither is reliable alone. The sample attribute is
                    // what the documentation points at and Intel's hardware transform
                    // ignores it outright - measured: a session ran sixty frames with
                    // no keyframe in it, so a phone joining late had nothing to decode
                    // against. The codec property is what that encoder listens to, and
                    // is not supported by every other one.
                    Guid cleanPoint = MF.MFSampleExtension_CleanPoint;
                    sample.SetUINT32(ref cleanPoint, 1);
                    ForceKeyFrame();
                }

                int hr = _transform.ProcessInput(InputStream, sample, 0);
                if (hr != MF.S_OK)
                {
                    // The encoder is full. Dropping this frame is correct: the next one
                    // is 16 milliseconds away and describes the screen better.
                    if (_keyFrameWanted) RequestKeyFrame();
                    return false;
                }
                return true;
            }
            finally
            {
                Marshal.ReleaseComObject(sample);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(buffer);
        }
    }

    /// <summary>
    /// Waits until the transform says it will take a frame.
    /// </summary>
    /// <remarks>
    /// Feeding an async transform without its METransformNeedInput is the documented
    /// way to make a hardware encoder fail for the rest of the session, so this waits
    /// rather than assuming. The wait is bounded: a driver that has stopped answering
    /// should cost one dropped frame, not the session.
    /// </remarks>
    private bool WaitForNeedInput()
    {
        if (_pump is null) return false;
        if (_pump.WaitForInput(EventTimeout))
        {
            Volatile.Write(ref _inputTimeouts, 0);
            return true;
        }

        // Logged once rather than per frame. An encoder in this state never recovers,
        // and at two and a half attempts a second it wrote a thousand identical lines
        // a minute into the log while the session showed nothing.
        int missed = Interlocked.Increment(ref _inputTimeouts);
        if (missed == 1)
        {
            Logger.WriteLine("Remote encoder did not ask for a frame within " + EventTimeout.TotalMilliseconds + " ms.");
        }
        else if (missed == StallThreshold)
        {
            Logger.WriteLine($"Remote encoder has asked for nothing in {StallThreshold} attempts and is being given up on.");
        }
        return false;
    }

    private int _inputTimeouts;

    /// <summary>
    /// True once this encoder has stopped asking for frames and is not coming back.
    /// </summary>
    /// <remarks>
    /// The last line of defence behind the acceptance test. An encoder can also die
    /// part way through a session, when the dedicated GPU it was using is switched off
    /// underneath it, and the session's answer to both is the same: stop waiting for it
    /// and send pictures instead.
    /// </remarks>
    internal bool HasStalled => Volatile.Read(ref _inputTimeouts) >= StallThreshold;

    private const int ProbeFrames = 4;
    private const int StallThreshold = 5;

    /// <summary>Takes one coded picture out, if there is one.</summary>
    private bool Drain(long timestampUs, EncodedVideoFrame encoded)
    {
        if (_async)
        {
            // Not every frame produces one immediately: an encoder is entitled to hold
            // a frame back, and the next call collects two. Waiting the full timeout
            // for each would cap the session at a handful of frames a second, so this
            // takes what is ready and moves on.
            if (_pump is null) return false;
            if (!_pump.TryTakeOutput() && !_pump.WaitForOutput(FirstOutputTimeout)) return false;
        }

        var buffers = new MF.MFT_OUTPUT_DATA_BUFFER { dwStreamID = OutputStream };

        // A sync transform allocates nothing; the caller provides the sample. An async
        // one provides its own and ignores anything handed to it.
        MF.IMFSample? provided = null;
        MF.IMFMediaBuffer? providedBuffer = null;
        if (!_async)
        {
            if (MF.MFCreateMemoryBuffer(_outputBufferSize, out providedBuffer) != MF.S_OK) return false;
            if (MF.MFCreateSample(out provided) != MF.S_OK)
            {
                Marshal.ReleaseComObject(providedBuffer);
                return false;
            }
            provided.AddBuffer(providedBuffer);
            buffers.pSample = Marshal.GetIUnknownForObject(provided);
        }

        try
        {
            int hr = _transform.ProcessOutput(0, 1, ref buffers, out _);
            if (hr == MF.MF_E_TRANSFORM_NEED_MORE_INPUT) return false;
            if (hr == MF.MF_E_TRANSFORM_STREAM_CHANGE)
            {
                // The encoder renegotiated its own output. Nothing here depends on the
                // details, but the next picture has to be a keyframe or the phone is
                // decoding against parameter sets that no longer describe the stream.
                RequestKeyFrame();
                return false;
            }
            if (hr != MF.S_OK) return false;
            if (buffers.pSample == IntPtr.Zero) return false;

            return Read(buffers.pSample, timestampUs, encoded);
        }
        finally
        {
            if (buffers.pSample != IntPtr.Zero) Marshal.Release(buffers.pSample);
            if (provided is not null) Marshal.ReleaseComObject(provided);
            if (providedBuffer is not null) Marshal.ReleaseComObject(providedBuffer);
            if (buffers.pEvents != IntPtr.Zero) Marshal.Release(buffers.pEvents);
        }
    }

    private bool Read(IntPtr samplePointer, long timestampUs, EncodedVideoFrame encoded)
    {
        var sample = (MF.IMFSample)Marshal.GetObjectForIUnknown(samplePointer);
        try
        {
            Guid cleanPoint = MF.MFSampleExtension_CleanPoint;
            bool keyFrame = sample.GetUINT32(ref cleanPoint, out uint clean) == MF.S_OK && clean != 0;

            if (sample.ConvertToContiguousBuffer(out MF.IMFMediaBuffer buffer) != MF.S_OK) return false;
            try
            {
                if (buffer.Lock(out IntPtr source, out _, out int length) != MF.S_OK) return false;
                try
                {
                    if (length <= 0) return false;
                    EnsureOutput(length + 8);
                    BinaryPrimitives.WriteInt64BigEndian(_output, timestampUs);
                    Marshal.Copy(source, _output, 8, length);

                    encoded.Data = _output;
                    encoded.Length = length + 8;
                    encoded.Flags = keyFrame ? RemoteDesktopProtocol.VideoFlags.KeyFrame : RemoteDesktopProtocol.VideoFlags.None;
                    encoded.TimestampUs = timestampUs;
                    return true;
                }
                finally
                {
                    buffer.Unlock();
                }
            }
            finally
            {
                Marshal.ReleaseComObject(buffer);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(sample);
        }
    }

    private void EnsureOutput(int length)
    {
        if (_output.Length < length) _output = new byte[Math.Max(length, _output.Length * 2)];
    }

    // ---- Platform lifetime -------------------------------------------------------

    /// <summary>
    /// Starts the Media Foundation platform on demand.
    /// </summary>
    /// <remarks>
    /// Shutdown is not paired to each candidate or session. The background lifetime
    /// policy waits until no accepted encoder and no discovery/configuration operation
    /// is alive, so quality changes cannot tear the platform down underneath an async
    /// callback or the next candidate in the same enumeration.
    /// </remarks>
    private static bool Startup()
    {
        lock (StartupGate)
        {
            if (_platformStarted) return true;

            // A machine without Media Foundation is not going to grow it; asking again
            // on every session would be a failed COM call per candidate per attempt.
            if (_platformUnavailable) return false;

            if (MF.MFStartup(MF.MF_VERSION, MF.MFSTARTUP_LITE) != MF.S_OK)
            {
                _platformUnavailable = true;
                Logger.WriteLine("Media Foundation did not start; remote sessions will use picture tiles.");
                return false;
            }

            _platformStarted = true;
            return true;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // The pump stops first so the callback does not re-arm itself onto a transform
        // that is about to be released.
        _pump?.Dispose();

        try
        {
            _transform.ProcessMessage(MF.MFT_MESSAGE_NOTIFY_END_OF_STREAM, IntPtr.Zero);
            _transform.ProcessMessage(MF.MFT_MESSAGE_NOTIFY_END_STREAMING, IntPtr.Zero);
        }
        catch (Exception ex) { Logger.WriteLine("Remote encoder stop: " + ex.Message); }

        try { Marshal.ReleaseComObject(_transform); }
        catch (Exception ex) { Logger.WriteLine("Remote encoder release: " + ex.Message); }

        if (_counted)
        {
            _counted = false;
            Interlocked.Decrement(ref _liveEncoders);
        }
    }

    /// <summary>
    /// Closes the Media Foundation platform if nothing is using it.
    /// </summary>
    /// <remarks>
    /// The platform is started on the first session and, left alone, stays up for the
    /// life of the process holding the codec libraries with it - which on this class of
    /// machine is tens of megabytes of vendor media DLLs that a tray application has no
    /// use for between sessions.
    ///
    /// <para>It is closed only from the idle release, which runs five seconds after the
    /// last activity and only when no window is on screen, and then only when no encoder
    /// is alive. That is the distinction that matters: closing it underneath something
    /// still using it corrupts the heap and is what took the process out on a quality
    /// change. Closing it when nothing has been using it for five seconds does not.</para>
    ///
    /// <para>Nothing is remembered about having closed it. The next session starts the
    /// platform again exactly as the first one did.</para>
    /// </remarks>
    internal static void ReleasePlatformIfIdle()
    {
        lock (StartupGate)
        {
            if (!_platformStarted) return;
            if (Volatile.Read(ref _liveEncoders) != 0) return;
            if (Volatile.Read(ref _platformOperations) != 0) return;

            try
            {
                MF.MFShutdown();
                _platformStarted = false;
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Media Foundation release: " + ex.Message);
            }
        }
    }

}
