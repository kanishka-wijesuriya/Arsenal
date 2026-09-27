using Arsenal.Helpers;
using System.Runtime.InteropServices;
using D3D = Arsenal.UI.Services.Remote.Desktop.Direct3DInterop;

namespace Arsenal.UI.Services.Remote.Desktop;

/// <summary>
/// One acquired desktop frame, as a texture that is still on the graphics card.
/// </summary>
/// <remarks>
/// The pointer is owned by the duplication and is only valid until the frame is
/// released, which is why this is a struct handed back by reference rather than
/// something that could outlive the acquire that produced it.
/// </remarks>
internal readonly struct DuplicatedFrame
{
    /// <summary>The desktop image, as an <c>ID3D11Texture2D</c>.</summary>
    internal IntPtr Texture { get; init; }

    /// <summary>How many desktop updates were folded into this one. Zero means none.</summary>
    internal uint AccumulatedFrames { get; init; }

    internal long TimestampUs { get; init; }

    internal bool HasImage => Texture != IntPtr.Zero;
}

/// <summary>
/// Reads the desktop with DXGI Desktop Duplication.
/// </summary>
/// <remarks>
/// This is the path every serious remote desktop and game streaming client uses, and it
/// differs from the BitBlt one in the only way that matters: the frame is never copied
/// into system memory. What comes back is a texture already on the graphics card, which
/// the scaler and the encoder both read from where it lies. The measured cost of the old
/// path on a 3440x1440 desktop was 46 to 86 milliseconds a frame, all of it the copy out
/// of video memory and the resample on the processor; neither of those happens here.
///
/// <para>It also brings its own clock. <c>AcquireNextFrame</c> returns when the desktop
/// has changed and blocks until then, so the capture loop runs at the rate the screen is
/// actually moving instead of waking on a timer and comparing pixels to find out that
/// nothing did. An idle desktop costs one timeout per interval rather than a full frame's
/// work, and <c>AccumulatedFrames</c> says whether anything but the pointer moved without
/// reading a single pixel.</para>
///
/// <para>What it cannot do is capture more than one output at a time, so the combined
/// "All displays" entry still belongs to the BitBlt path. It also loses access across a
/// desktop switch, a mode change and a driver restart, all of which are normal and all of
/// which are recovered by duplicating the output again rather than by ending the
/// session.</para>
/// </remarks>
internal sealed class DesktopDuplicationSource : IDisposable
{
    private readonly RemoteMonitor _monitor;
    private readonly object _gate = new();

    private IntPtr _device;
    private IntPtr _context;
    private D3D.ID3D11Device? _deviceObject;
    private D3D.ID3D11DeviceContext? _contextObject;
    private IntPtr _stage;
    private D3D.ID3D10Multithread? _multithread;
    private D3D.IDXGIOutputDuplication? _duplication;
    private IntPtr _acquired;
    private bool _holdingFrame;
    private bool _disposed;

    /// <summary>Set when the duplication needs rebuilding before the next acquire.</summary>
    private bool _lost;

    private DesktopDuplicationSource(RemoteMonitor monitor, IntPtr device, IntPtr context, int width, int height)
    {
        _monitor = monitor;
        _device = device;
        _context = context;
        Width = width;
        Height = height;

        // Multithread protection before anything else touches the device. The encoder's
        // callbacks arrive on a Media Foundation work queue thread while this class is
        // acquiring on its own, and that is two threads on one device.
        if (QueryInterface(device, D3D.IID_ID3D10Multithread) is D3D.ID3D10Multithread guard)
        {
            guard.SetMultithreadProtected(true);
            _multithread = guard;
        }

        _deviceObject = (D3D.ID3D11Device)Marshal.GetObjectForIUnknown(device);
        _contextObject = (D3D.ID3D11DeviceContext)Marshal.GetObjectForIUnknown(context);
    }

    /// <summary>The duplicated output's size, in real pixels.</summary>
    internal int Width { get; }
    internal int Height { get; }

    /// <summary>The device every texture from here belongs to.</summary>
    internal IntPtr Device => _device;

    /// <summary>
    /// Opens a duplication of the given monitor, or returns null when this machine or
    /// this monitor cannot provide one.
    /// </summary>
    /// <remarks>
    /// Null is an ordinary answer, not a failure: a combined virtual monitor, a session
    /// with no rights to the desktop, and a display driver that refuses duplication all
    /// land here, and all of them are handled by using the BitBlt path instead.
    /// </remarks>
    internal static DesktopDuplicationSource? TryCreate(RemoteMonitor monitor)
    {
        IntPtr adapter = IntPtr.Zero;
        IntPtr output = IntPtr.Zero;
        IntPtr device = IntPtr.Zero;
        IntPtr context = IntPtr.Zero;

        try
        {
            if (!TryFindOutput(monitor, out adapter, out output, out int width, out int height))
            {
                Logger.WriteLine($"Desktop duplication: no single output matches {monitor.Name}; using the desktop copy path.");
                return null;
            }

            // Driver type must be UNKNOWN when an adapter is named, and the adapter has
            // to be the one the monitor hangs off: a texture from one device cannot be
            // read by another, and on this laptop the panel and an external screen can
            // easily be on different ones.
            int[] levels =
            [
                D3D.D3D_FEATURE_LEVEL_11_1, D3D.D3D_FEATURE_LEVEL_11_0,
                D3D.D3D_FEATURE_LEVEL_10_1, D3D.D3D_FEATURE_LEVEL_10_0,
            ];

            int hr = D3D.D3D11CreateDevice(
                adapter,
                D3D.D3D_DRIVER_TYPE_UNKNOWN,
                IntPtr.Zero,
                D3D.D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D.D3D11_CREATE_DEVICE_VIDEO_SUPPORT,
                levels,
                (uint)levels.Length,
                D3D.D3D11_SDK_VERSION,
                out device,
                out _,
                out context);

            if (hr != D3D.S_OK || device == IntPtr.Zero)
            {
                Logger.WriteLine($"Desktop duplication: no Direct3D device (0x{hr:X8}); using the desktop copy path.");
                return null;
            }

            var source = new DesktopDuplicationSource(monitor, device, context, width, height);
            device = IntPtr.Zero;
            context = IntPtr.Zero;

            if (!source.TryDuplicate(output))
            {
                source.Dispose();
                return null;
            }

            Logger.WriteLine($"Desktop duplication: {monitor.Name} at {width}x{height}, frames stay on the graphics card.");
            return source;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Desktop duplication start: " + ex.Message);
            return null;
        }
        finally
        {
            Release(ref adapter);
            Release(ref output);
            Release(ref device);
            Release(ref context);
        }
    }

    /// <summary>
    /// Finds the adapter and output whose desktop rectangle is the monitor being asked
    /// for.
    /// </summary>
    /// <remarks>
    /// Matched on the desktop rectangle rather than the device name, because the two
    /// enumerations spell a display differently and the rectangle is the thing both
    /// agree on. Both sides are read per-monitor DPI aware, so both are real pixels.
    /// </remarks>
    private static bool TryFindOutput(RemoteMonitor monitor, out IntPtr adapter, out IntPtr output, out int width, out int height)
    {
        adapter = IntPtr.Zero;
        output = IntPtr.Zero;
        width = 0;
        height = 0;

        Guid factoryId = new("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
        int hr = D3D.CreateDXGIFactory1(in factoryId, out IntPtr factoryPointer);
        if (hr != D3D.S_OK || factoryPointer == IntPtr.Zero)
        {
            Logger.WriteLine($"Desktop duplication: no DXGI factory (0x{hr:X8}).");
            return false;
        }

        try
        {
            var factory = (D3D.IDXGIFactory1)Marshal.GetObjectForIUnknown(factoryPointer);

            for (uint adapterIndex = 0; ; adapterIndex++)
            {
                if (factory.EnumAdapters1(adapterIndex, out IntPtr adapterPointer) != D3D.S_OK) break;

                try
                {
                    // EnumAdapters1 hands back an IDXGIAdapter1. Asked for the base
                    // interface explicitly rather than by casting the wrapper, so a
                    // refusal is a result to log and not an exception to catch.
                    if (Marshal.QueryInterface(adapterPointer, in D3D.IID_IDXGIAdapter1, out IntPtr basePointer) != D3D.S_OK)
                        continue;

                    try
                    {
                        var adapterObject = (D3D.IDXGIAdapter1)Marshal.GetObjectForIUnknown(basePointer);

                        for (uint outputIndex = 0; ; outputIndex++)
                        {
                            if (adapterObject.EnumOutputs(outputIndex, out IntPtr outputPointer) != D3D.S_OK) break;

                            try
                            {
                                if (Marshal.QueryInterface(outputPointer, in D3D.IID_IDXGIOutput1, out IntPtr output1) != D3D.S_OK)
                                    continue;

                                bool matched = false;
                                try
                                {
                                    var described = (D3D.IDXGIOutput1)Marshal.GetObjectForIUnknown(output1);
                                    if (described.GetDesc(out D3D.DXGI_OUTPUT_DESC desc) != D3D.S_OK) continue;

                                    D3D.RECT bounds = desc.DesktopCoordinates;
                                    if (!desc.AttachedToDesktop ||
                                        bounds.Left != monitor.Left || bounds.Top != monitor.Top ||
                                        bounds.Right - bounds.Left != monitor.Width ||
                                        bounds.Bottom - bounds.Top != monitor.Height)
                                    {
                                        continue;
                                    }

                                    width = bounds.Right - bounds.Left;
                                    height = bounds.Bottom - bounds.Top;
                                    adapter = basePointer;
                                    output = output1;
                                    matched = true;
                                    return true;
                                }
                                finally
                                {
                                    if (!matched) Release(ref output1);
                                }
                            }
                            finally
                            {
                                Release(ref outputPointer);
                            }
                        }
                    }
                    finally
                    {
                        if (adapter == IntPtr.Zero) Release(ref basePointer);
                    }
                }
                finally
                {
                    Release(ref adapterPointer);
                }
            }

            Logger.WriteLine($"Desktop duplication: no output matches {monitor.Width}x{monitor.Height} at ({monitor.Left},{monitor.Top}).");
            return false;
        }
        finally
        {
            Release(ref factoryPointer);
        }
    }

    private bool TryDuplicate(IntPtr output)
    {
        try
        {
            var output1 = (D3D.IDXGIOutput1)Marshal.GetObjectForIUnknown(output);
            int hr = output1.DuplicateOutput(_device, out IntPtr duplication);
            if (hr != D3D.S_OK || duplication == IntPtr.Zero)
            {
                Logger.WriteLine(hr == D3D.DXGI_ERROR_UNSUPPORTED
                    ? "Desktop duplication: this display driver does not offer it; using the desktop copy path."
                    : $"Desktop duplication: could not duplicate the output (0x{hr:X8}); using the desktop copy path.");
                return false;
            }

            _duplication = (D3D.IDXGIOutputDuplication)Marshal.GetObjectForIUnknown(duplication);
            Release(ref duplication);
            _lost = false;
            return true;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Desktop duplication: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Waits for the desktop to change and hands back the frame.
    /// </summary>
    /// <remarks>
    /// A timeout is success with no image, not an error: it means nothing moved, and the
    /// caller should send nothing rather than re-encode what the phone already has.
    /// </remarks>
    /// <returns>False only when the duplication is gone and could not be rebuilt.</returns>
    internal bool TryAcquire(uint timeoutMs, out DuplicatedFrame frame)
    {
        frame = default;
        lock (_gate)
        {
            if (_disposed) return false;
            if (_lost && !Rebuild()) return false;

            var duplication = _duplication;
            if (duplication is null) return false;

            ReleaseHeldFrame(duplication);

            int hr = duplication.AcquireNextFrame(timeoutMs, out D3D.DXGI_OUTDUPL_FRAME_INFO info, out IntPtr resource);
            if (hr == D3D.DXGI_ERROR_WAIT_TIMEOUT) return true;

            if (hr != D3D.S_OK)
            {
                // Access lost is routine: a full screen game taking the display, the
                // secure desktop appearing, the user switching away, a driver restart.
                // Rebuilding is the answer to all of them.
                if (hr is D3D.DXGI_ERROR_ACCESS_LOST or D3D.DXGI_ERROR_DEVICE_REMOVED or
                    D3D.DXGI_ERROR_ACCESS_DENIED or D3D.E_ACCESSDENIED or D3D.DXGI_ERROR_SESSION_DISCONNECTED)
                {
                    _lost = true;
                    return true;
                }

                Logger.WriteLine($"Desktop duplication acquire: 0x{hr:X8}");
                return false;
            }

            _holdingFrame = true;

            try
            {
                if (Marshal.QueryInterface(resource, in D3D.IID_ID3D11Texture2D, out IntPtr texture) != D3D.S_OK)
                    return true;

                try
                {
                    // Copied into a texture of our own rather than handed over directly.
                    // The duplication's surface is not a legal video processor input -
                    // it comes back DXGI_ERROR_INVALID_CALL - because it is not created
                    // with the bind flags a processor needs to read it. The copy is
                    // card to card, never crosses the bus, and measures in fractions of
                    // a millisecond; it also means the frame can be given back to the
                    // duplication immediately instead of being held for the length of a
                    // scale and an encode.
                    if (!EnsureStage()) return true;
                    _contextObject!.CopyResource(_stage, texture);
                }
                finally
                {
                    Release(ref texture);
                }

                frame = new DuplicatedFrame
                {
                    Texture = _stage,
                    AccumulatedFrames = info.AccumulatedFrames,
                    TimestampUs = RemoteClock.NowMicroseconds(),
                };
                return true;
            }
            finally
            {
                Release(ref resource);
            }
        }
    }

    /// <summary>Gives the frame back, which must happen before the next acquire.</summary>
    internal void ReleaseFrame()
    {
        lock (_gate)
        {
            var duplication = _duplication;
            if (duplication is not null) ReleaseHeldFrame(duplication);
        }
    }

    private void ReleaseHeldFrame(D3D.IDXGIOutputDuplication duplication)
    {
        Release(ref _acquired);
        if (!_holdingFrame) return;
        _holdingFrame = false;
        try { duplication.ReleaseFrame(); } catch { }
    }

    /// <remarks>
    /// Rebuilding costs a re-enumeration because the output may have moved to another
    /// adapter, which is exactly what happens when this laptop switches between its
    /// integrated and discrete graphics with a session open.
    /// </remarks>
    private bool Rebuild()
    {
        ReleaseDuplication();

        if (!TryFindOutput(_monitor, out IntPtr adapter, out IntPtr output, out _, out _))
        {
            Release(ref adapter);
            Release(ref output);
            return false;
        }

        try
        {
            return TryDuplicate(output);
        }
        finally
        {
            Release(ref adapter);
            Release(ref output);
        }
    }

    private void ReleaseDuplication()
    {
        var duplication = _duplication;
        if (duplication is not null)
        {
            ReleaseHeldFrame(duplication);
            _duplication = null;
            try { Marshal.FinalReleaseComObject(duplication); } catch { }
        }
        _holdingFrame = false;
    }

    /// <summary>
    /// Makes the texture every captured frame is copied into, once.
    /// </summary>
    /// <remarks>
    /// One texture reused for the life of the session rather than one per frame. It is
    /// the same size and format as the desktop and is bound as a shader resource and a
    /// render target, which is what makes it acceptable to the video processor.
    /// </remarks>
    private bool EnsureStage()
    {
        if (_stage != IntPtr.Zero) return true;
        if (_deviceObject is null) return false;

        var desc = new D3D.D3D11_TEXTURE2D_DESC
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = D3D.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleCount = 1,
            SampleQuality = 0,
            Usage = D3D.D3D11_USAGE_DEFAULT,
            BindFlags = D3D.D3D11_BIND_SHADER_RESOURCE | D3D.D3D11_BIND_RENDER_TARGET,
            CPUAccessFlags = 0,
            MiscFlags = 0,
        };

        int hr = _deviceObject.CreateTexture2D(in desc, IntPtr.Zero, out IntPtr stage);
        if (hr != D3D.S_OK || stage == IntPtr.Zero)
        {
            Logger.WriteLine($"Desktop duplication: could not make a capture texture (0x{hr:X8}).");
            return false;
        }

        _stage = stage;
        return true;
    }

    private static object? QueryInterface(IntPtr unknown, Guid iid)
    {
        if (Marshal.QueryInterface(unknown, in iid, out IntPtr result) != D3D.S_OK || result == IntPtr.Zero) return null;
        try { return Marshal.GetObjectForIUnknown(result); }
        finally { Release(ref result); }
    }

    private static void Release(ref IntPtr pointer)
    {
        if (pointer == IntPtr.Zero) return;
        Marshal.Release(pointer);
        pointer = IntPtr.Zero;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            ReleaseDuplication();

            if (_multithread is not null)
            {
                try { Marshal.FinalReleaseComObject(_multithread); } catch { }
                _multithread = null;
            }

            Release(ref _stage);
            Release(ref _context);
            Release(ref _device);
        }
    }
}
