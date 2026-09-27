using Arsenal.UI.Services.Remote.Desktop;
using System.Diagnostics;
using System.Runtime.InteropServices;

// Drives DXGI Desktop Duplication against the real display, and then wraps what it
// hands back the way the encoder will.
//
// Everything in Direct3DInterop is hand-written vtable order. A slot that is off by one
// compiles, throws nothing, and calls a different function; the failure surfaces later
// as a wrong answer or as a process that dies in a library with no managed stack. So
// this calls each declaration that matters and checks the answer is sane:
//
//   - the output is found and its size matches what the monitor enumeration said
//   - AcquireNextFrame returns a texture, and a timeout when nothing moves
//   - the texture wraps as a Media Foundation buffer, which proves both the IID and
//     that what came back really is an ID3D11Texture2D and not some other pointer
//   - the whole loop runs at a rate worth having
//
// Exits non-zero if the duplication could not be opened at all.

RemoteNative.SetProcessDpiAwarenessContext(RemoteNative.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

int seconds = args.Length > 0 && int.TryParse(args[0], out int parsed) ? parsed : 10;

var monitors = RemoteMonitors.Enumerate();
Console.WriteLine($"Monitors: {monitors.Count}");
foreach (var entry in monitors)
{
    Console.WriteLine($"  [{entry.Index}] {entry.Name}  {entry.Width}x{entry.Height} at ({entry.Left},{entry.Top})  {entry.RefreshHz}Hz");
}

DumpAdapters();
DumpContextIids();

var monitor = monitors[0];
Console.WriteLine($"\nDuplicating [{monitor.Index}] {monitor.Name} for {seconds}s\n");

var source = DesktopDuplicationSource.TryCreate(monitor);
if (source is null)
{
    Console.WriteLine("FAILED: no duplication. Nothing below was tested.");
    return 1;
}

Console.WriteLine($"Opened at {source.Width}x{source.Height}");
if (source.Width != monitor.Width || source.Height != monitor.Height)
{
    Console.WriteLine($"WARNING: duplication size disagrees with the monitor ({monitor.Width}x{monitor.Height}).");
}

if (MF.MFStartup(MF.MF_VERSION, MF.MFSTARTUP_LITE) != 0)
{
    Console.WriteLine("FAILED: Media Foundation would not start.");
    return 1;
}

int acquires = 0, images = 0, timeouts = 0, wrapped = 0, wrapFailures = 0, changed = 0, pointerOnly = 0;
long slowestUs = 0, totalUs = 0;
var stopwatch = Stopwatch.StartNew();
var perAcquire = new Stopwatch();

while (stopwatch.Elapsed.TotalSeconds < seconds)
{
    perAcquire.Restart();
    bool alive = source.TryAcquire(16, out DuplicatedFrame frame);
    perAcquire.Stop();

    if (!alive)
    {
        Console.WriteLine("FAILED: the duplication died and could not be rebuilt.");
        return 1;
    }

    acquires++;
    long us = perAcquire.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
    totalUs += us;
    if (us > slowestUs) slowestUs = us;

    if (!frame.HasImage)
    {
        timeouts++;
        continue;
    }

    images++;
    if (frame.AccumulatedFrames > 0) changed++; else pointerOnly++;

    // The real test of the IID and of what AcquireNextFrame handed back. Media
    // Foundation will reject a pointer that is not the surface type it was told.
    int hr = MF.MFCreateDXGISurfaceBuffer(
        in Direct3DInterop.IID_ID3D11Texture2D, frame.Texture, 0, false,
        out MF.IMFMediaBuffer buffer);

    if (hr == 0)
    {
        wrapped++;
        Marshal.FinalReleaseComObject(buffer);
    }
    else
    {
        wrapFailures++;
        if (wrapFailures == 1) Console.WriteLine($"  surface buffer refused the texture: 0x{hr:X8}");
    }

    source.ReleaseFrame();
}

stopwatch.Stop();
double elapsed = stopwatch.Elapsed.TotalSeconds;

Console.WriteLine($"\n  acquires      {acquires}  ({acquires / elapsed:F1}/s)");
Console.WriteLine($"  with a frame  {images}  ({images / elapsed:F1}/s)");
Console.WriteLine($"  desktop moved {changed}  ({changed / elapsed:F1}/s)   <- what would actually be encoded");
Console.WriteLine($"  pointer only  {pointerOnly}  (AccumulatedFrames 0; nothing to encode)");
Console.WriteLine($"  timeouts      {timeouts}   (the desktop was not changing)");
Console.WriteLine($"  wrapped       {wrapped} ok, {wrapFailures} refused");
Console.WriteLine($"  acquire cost  {totalUs / Math.Max(1, acquires) / 1000.0:F2} ms average, {slowestUs / 1000.0:F2} ms worst");

MF.MFShutdown();

if (images > 0 && wrapFailures == 0) Console.WriteLine("\n  capture PASS");
else if (images == 0) Console.WriteLine("\n  capture INCONCLUSIVE: nothing on screen changed.");
else Console.WriteLine("\n  capture FAIL: textures were refused by Media Foundation.");

// DXGI allows one duplication of an output per process, so the capture test has to let
// go before the pipeline can open its own. Leaving it alive made all three presets fail
// with E_INVALIDARG, which looked exactly like a broken pipeline and was not.
source.Dispose();

// ---- The whole path, which is the only test that means anything ------------------

Console.WriteLine($"\nFull pipeline (capture -> scale -> encode), {seconds}s each\n");

int failures = 0;
foreach ((string label, int capWidth, int capHeight, int fps, int kbps) in new[]
{
    ("Balanced 1920x1080 30fps", 1920, 1080, 30, 10000),
    ("Smooth   1600x900  60fps", 1600, 900, 60, 8000),
    ("Sharp    3840x2160 30fps", 3840, 2160, 30, 24000),
})
{
    using var pipeline = GpuCapturePipeline.TryCreate(monitor, capWidth, capHeight, fps, kbps, ["hevc", "h264"]);
    if (pipeline is null)
    {
        Console.WriteLine($"  {label}: no gpu pipeline (fell back).");
        failures++;
        continue;
    }

    var encoded = new EncodedVideoFrame();
    int coded = 0, skipped = 0;
    long bytes = 0, encodeTotal = 0, captureTotal = 0, worstMs = 0;
    var run = Stopwatch.StartNew();
    var frameClock = new Stopwatch();

    while (run.Elapsed.TotalSeconds < seconds)
    {
        frameClock.Restart();
        bool produced = pipeline.TryProduce(16, encoded, out int capMs, out int encMs, out bool alive);
        frameClock.Stop();
        if (!alive) break;

        captureTotal += capMs;
        encodeTotal += encMs;
        if (frameClock.ElapsedMilliseconds > worstMs) worstMs = frameClock.ElapsedMilliseconds;

        if (produced) { coded++; bytes += encoded.Length; }
        else skipped++;
    }

    run.Stop();
    double span = run.Elapsed.TotalSeconds;
    Console.WriteLine($"  {label}");
    Console.WriteLine($"     {pipeline.Width}x{pipeline.Height} {pipeline.Codec} via {pipeline.EncoderName}");
    Console.WriteLine($"     coded {coded} ({coded / span:F1} fps), skipped {skipped}, " +
        $"{bytes * 8 / 1000 / span:F0} kbps");
    Console.WriteLine($"     capture {captureTotal / (double)Math.Max(1, coded + skipped):F2} ms, " +
        $"encode {encodeTotal / (double)Math.Max(1, coded):F2} ms, worst frame {worstMs} ms");

    if (coded == 0) { Console.WriteLine("     FAIL: nothing was coded."); failures++; }
}

Console.WriteLine(failures == 0 ? "\nPASS" : $"\n{failures} preset(s) did not run on the gpu path.");
return 0;

// Which interface identity the immediate context actually answers to. Written because
// a guessed IID reads exactly like a broken interface: the cast throws E_NOINTERFACE and
// says nothing about which one would have worked.
static void DumpContextIids()
{
    int[] levels = [Direct3DInterop.D3D_FEATURE_LEVEL_11_1, Direct3DInterop.D3D_FEATURE_LEVEL_11_0];
    int hr = Direct3DInterop.D3D11CreateDevice(
        IntPtr.Zero, Direct3DInterop.D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero,
        Direct3DInterop.D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, (uint)levels.Length,
        Direct3DInterop.D3D11_SDK_VERSION, out IntPtr device, out _, out IntPtr context);

    Console.WriteLine($"\nImmediate context IIDs (device 0x{hr:X8}):");
    if (hr != 0) return;

    foreach ((string label, string iid) in new[]
    {
        ("ID3D11DeviceChild ", "1841e5c8-16b0-489b-bcc8-44cfb0d5deae"),
        ("ID3D11DeviceContext ", "c0bfa2d0-e785-4bf3-9d0e-a1b19d84f4cb"),
        ("ID3D11DeviceContext1", "bb2c6faa-b5fb-4082-8e6b-388b8cfa90e1"),
        ("ID3D11DeviceContext2", "420d5b32-b90c-4da4-bef0-359f6a24a83a"),
        ("ID3D11DeviceContext3", "b4e3c01d-e79e-4637-91b2-510e9f4c9b8f"),
        ("ID3D11DeviceContext4", "917600da-f58c-4c33-98d8-3e15b390fa24"),
    })
    {
        Guid id = new(iid);
        int probe = Marshal.QueryInterface(context, in id, out IntPtr got);
        Console.WriteLine($"  {label} {iid}  0x{probe:X8}{(probe == 0 && got == context ? "  (same pointer)" : "")}");
        if (probe == 0) Marshal.Release(got);
    }

    Marshal.Release(context);
    Marshal.Release(device);
}

// Prints what DXGI says about the machine. Sane names and rectangles here are also the
// evidence that GetDesc is on the vtable slot this code thinks it is: a wrong slot
// reads some other function's output as a structure and prints nonsense.
static void DumpAdapters()
{
    Guid factoryId = new("770aae78-f26f-4dba-a829-253c83d1b387");
    int hr = Direct3DInterop.CreateDXGIFactory1(in factoryId, out IntPtr factoryPointer);
    Console.WriteLine($"\nDXGI factory: 0x{hr:X8}");
    if (hr != 0) return;

    var factory = (Direct3DInterop.IDXGIFactory1)Marshal.GetObjectForIUnknown(factoryPointer);

    for (uint a = 0; ; a++)
    {
        int enumerated = factory.EnumAdapters1(a, out IntPtr adapterPointer);
        if (enumerated != 0)
        {
            Console.WriteLine($"  EnumAdapters1({a}) -> 0x{enumerated:X8}");
            break;
        }

        int qi = Marshal.QueryInterface(adapterPointer, in Direct3DInterop.IID_IDXGIAdapter1, out IntPtr basePointer);
        if (qi != 0) Console.WriteLine($"  adapter {a}: QI IDXGIAdapter1 0x{qi:X8}");
        if (qi != 0) { Marshal.Release(adapterPointer); continue; }

        var adapter = (Direct3DInterop.IDXGIAdapter1)Marshal.GetObjectForIUnknown(basePointer);
        int described = adapter.GetDesc(out Direct3DInterop.DXGI_ADAPTER_DESC desc);
        Console.WriteLine($"    desc 0x{described:X8}  \"{desc.Description}\"  vendor 0x{desc.VendorId:X4}");

        for (uint o = 0; ; o++)
        {
            int outputHr = adapter.EnumOutputs(o, out IntPtr outputPointer);
            if (outputHr != 0)
            {
                Console.WriteLine($"    EnumOutputs({o}) -> 0x{outputHr:X8}");
                break;
            }

            int outputQi = Marshal.QueryInterface(outputPointer, in Direct3DInterop.IID_IDXGIOutput1, out IntPtr output1);
            if (outputQi == 0)
            {
                var described1 = (Direct3DInterop.IDXGIOutput1)Marshal.GetObjectForIUnknown(output1);
                int descHr = described1.GetDesc(out Direct3DInterop.DXGI_OUTPUT_DESC outputDesc);
                var b = outputDesc.DesktopCoordinates;
                Console.WriteLine($"    output {o}: 0x{descHr:X8} \"{outputDesc.DeviceName}\" " +
                    $"({b.Left},{b.Top})-({b.Right},{b.Bottom}) attached={outputDesc.AttachedToDesktop} rotation={outputDesc.Rotation}");
                Marshal.Release(output1);
            }
            else Console.WriteLine($"    output {o}: QI IDXGIOutput1 0x{outputQi:X8}");

            Marshal.Release(outputPointer);
        }

        Marshal.Release(basePointer);
        Marshal.Release(adapterPointer);
    }

    Marshal.Release(factoryPointer);
}
