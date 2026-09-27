using Arsenal.Helpers;
using System.Runtime.InteropServices;

namespace Arsenal.UI.Services.Remote.Desktop;

/// <summary>
/// Scales the captured desktop and converts it to NV12, on the graphics card.
/// </summary>
/// <remarks>
/// This replaces two things that used to happen on the processor for every frame: the
/// HALFTONE <c>StretchBlt</c> in the BitBlt path, which measured slower than copying the
/// whole screen unscaled, and <see cref="Arsenal.Application.Models.Nv12Converter"/>,
/// which walked the entire frame afterwards. Both are one operation here, on the hardware
/// that already holds the pixels.
///
/// <para>The stock Video Processor transform rather than a shader of our own. It is the
/// same component the rest of Windows uses for this, it is backed by the driver's fixed
/// function video hardware where there is any, and writing a compute shader would mean
/// shipping compiled bytecode for a job the driver already does well.</para>
///
/// <para>What comes out is a sample, not a texture, and it is passed to the encoder
/// exactly as it arrives. Unwrapping it to get at the texture inside would mean another
/// interface and would buy nothing: the encoder wants a sample.</para>
/// </remarks>
internal sealed class GpuFrameScaler : IDisposable
{
    private const uint InputStream = 0;
    private const uint OutputStream = 0;

    private readonly MF.IMFTransform _transform;
    private readonly bool _providesSamples;

    /// <summary>A nominal duration, so the processor has one. The encoder re-times its own input.</summary>
    private const long _frameDuration = 10_000_000 / 60;
    private bool _disposed;

    private GpuFrameScaler(MF.IMFTransform transform, bool providesSamples, int width, int height)
    {
        _transform = transform;
        _providesSamples = providesSamples;
        Width = width;
        Height = height;
    }

    /// <summary>The size frames leave at, which is what the encoder is built for.</summary>
    internal int Width { get; }
    internal int Height { get; }

    /// <summary>
    /// Builds a scaler from <paramref name="sourceWidth"/>x<paramref name="sourceHeight"/>
    /// BGRA to <paramref name="targetWidth"/>x<paramref name="targetHeight"/> NV12.
    /// </summary>
    /// <remarks>
    /// The device manager is handed over before any media type is set. A transform told
    /// about Direct3D afterwards has already decided it is working in system memory, and
    /// it will accept every type it is offered and then ask for buffers this code cannot
    /// give it.
    /// </remarks>
    internal static GpuFrameScaler? TryCreate(
        MF.IMFDXGIDeviceManager manager,
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight)
    {
        MF.IMFTransform? transform = null;
        try
        {
            Guid clsid = MF.CLSID_VideoProcessorMFT;
            Guid iid = typeof(MF.IMFTransform).GUID;
            int hr = MF.CoCreateInstance(in clsid, IntPtr.Zero, MF.CLSCTX_INPROC_SERVER, in iid, out object created);
            if (hr != MF.S_OK || created is not MF.IMFTransform candidate)
            {
                Logger.WriteLine($"Frame scaler: no video processor (0x{hr:X8}).");
                return null;
            }

            transform = candidate;

            IntPtr managerPointer = Marshal.GetIUnknownForObject(manager);
            try
            {
                hr = transform.ProcessMessage(MF.MFT_MESSAGE_SET_D3D_MANAGER, managerPointer);
                if (hr != MF.S_OK)
                {
                    Logger.WriteLine($"Frame scaler: would not take the Direct3D device (0x{hr:X8}).");
                    return null;
                }
            }
            finally
            {
                Marshal.Release(managerPointer);
            }

            // Output before input. The processor picks its conversion from the pair, and
            // setting the input first lets it settle on a passthrough that then refuses
            // the output type it should have been built for.
            if (!TrySetType(transform, output: true, MF.MFVideoFormat_NV12, targetWidth, targetHeight)) return null;
            if (!TrySetType(transform, output: false, MF.MFVideoFormat_ARGB32, sourceWidth, sourceHeight)) return null;

            bool providesSamples = false;
            if (transform.GetOutputStreamInfo(OutputStream, out MF.MFT_OUTPUT_STREAM_INFO info) == MF.S_OK)
                providesSamples = (info.dwFlags & MF.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;

            if (!providesSamples)
            {
                // Without this the processor is in system memory after all, which means
                // the device manager did not take and every frame would be copied back
                // and forth. Better to fall back than to ship a slower path silently.
                Logger.WriteLine("Frame scaler: the video processor is not allocating textures; using the desktop copy path.");
                return null;
            }

            transform.ProcessMessage(MF.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero);
            transform.ProcessMessage(MF.MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero);

            var scaler = new GpuFrameScaler(transform, providesSamples, targetWidth, targetHeight);
            transform = null;
            Logger.WriteLine($"Frame scaler: {sourceWidth}x{sourceHeight} BGRA to {targetWidth}x{targetHeight} NV12 on the graphics card.");
            return scaler;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Frame scaler: " + ex.Message);
            return null;
        }
        finally
        {
            if (transform is not null) Marshal.ReleaseComObject(transform);
        }
    }

    private static bool TrySetType(MF.IMFTransform transform, bool output, Guid subtype, int width, int height)
    {
        if (MF.MFCreateMediaType(out MF.IMFMediaType type) != MF.S_OK) return false;
        try
        {
            Guid major = MF.MF_MT_MAJOR_TYPE;
            Guid majorValue = MF.MFMediaType_Video;
            Guid sub = MF.MF_MT_SUBTYPE;
            Guid frameSize = MF.MF_MT_FRAME_SIZE;
            Guid interlace = MF.MF_MT_INTERLACE_MODE;

            if (type.SetGUID(ref major, ref majorValue) != MF.S_OK) return false;
            if (type.SetGUID(ref sub, ref subtype) != MF.S_OK) return false;

            // Frame size is one 64-bit attribute, width in the high half.
            if (type.SetUINT64(ref frameSize, ((ulong)(uint)width << 32) | (uint)height) != MF.S_OK) return false;
            type.SetUINT32(ref interlace, MF.MFVideoInterlace_Progressive);

            int hr = output
                ? transform.SetOutputType(OutputStream, type, 0)
                : transform.SetInputType(InputStream, type, 0);

            if (hr != MF.S_OK)
            {
                Logger.WriteLine($"Frame scaler: {(output ? "output" : "input")} type {width}x{height} refused (0x{hr:X8}).");
                return false;
            }
            return true;
        }
        finally
        {
            Marshal.ReleaseComObject(type);
        }
    }

    /// <summary>
    /// Converts one captured texture.
    /// </summary>
    /// <remarks>
    /// The texture is wrapped rather than copied, so what the processor reads is the
    /// duplication's own surface. The caller must not release the frame until this has
    /// returned.
    /// </remarks>
    /// <param name="timestampUs">
    /// When the frame was captured. Not optional, and not cosmetic: the video processor
    /// refuses an input sample with no time on it with MF_E_NO_SAMPLE_TIMESTAMP, and
    /// because the refusal happens at <c>ProcessOutput</c> the frame stays queued, so
    /// every later <c>ProcessInput</c> comes back MF_E_NOTACCEPTING. One missing
    /// timestamp reads as a transform that has died.
    /// </param>
    /// <returns>The NV12 sample, or null when the processor had nothing this time.</returns>
    internal MF.IMFSample? TryConvert(IntPtr texture, long timestampUs)
    {
        if (_disposed || texture == IntPtr.Zero) return null;

        try
        {
            int wrap = MF.MFCreateDXGISurfaceBuffer(in Direct3DInterop.IID_ID3D11Texture2D, texture, 0, false,
                out MF.IMFMediaBuffer buffer);
            if (wrap != MF.S_OK)
            {
                Complain("surface buffer", wrap);
                return null;
            }

            try
            {
                if (MF.MFCreateSample(out MF.IMFSample sample) != MF.S_OK) return null;
                try
                {
                    if (sample.AddBuffer(buffer) != MF.S_OK) return null;

                    // Media Foundation counts in 100-nanosecond units.
                    sample.SetSampleTime(timestampUs * 10);
                    sample.SetSampleDuration(_frameDuration);

                    int hr = _transform.ProcessInput(InputStream, sample, 0);
                    if (hr != MF.S_OK)
                    {
                        Complain("ProcessInput", hr);
                        return null;
                    }
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

            var buffers = new MF.MFT_OUTPUT_DATA_BUFFER { dwStreamID = OutputStream, pSample = IntPtr.Zero };
            int output = _transform.ProcessOutput(0, 1, ref buffers, out _);
            if (output != MF.S_OK || buffers.pSample == IntPtr.Zero)
            {
                Complain("ProcessOutput", output);
                return null;
            }

            try
            {
                return (MF.IMFSample)Marshal.GetObjectForIUnknown(buffers.pSample);
            }
            finally
            {
                // The transform handed over its reference; the wrapper above took its
                // own, so this one is ours to drop.
                Marshal.Release(buffers.pSample);
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Frame scaler convert: " + ex.Message);
            return null;
        }
    }

    /// <summary>Says what went wrong once, not once per frame.</summary>
    private readonly HashSet<string> _complained = [];

    private void Complain(string stage, int hr)
    {
        if (_complained.Add(stage)) Logger.WriteLine($"Frame scaler {stage}: 0x{hr:X8}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _transform.ProcessMessage(MF.MFT_MESSAGE_NOTIFY_END_OF_STREAM, IntPtr.Zero);
            _transform.ProcessMessage(MF.MFT_MESSAGE_NOTIFY_END_STREAMING, IntPtr.Zero);
            _transform.ProcessMessage(MF.MFT_MESSAGE_SET_D3D_MANAGER, IntPtr.Zero);
        }
        catch { }
        try { Marshal.ReleaseComObject(_transform); } catch { }
    }
}
