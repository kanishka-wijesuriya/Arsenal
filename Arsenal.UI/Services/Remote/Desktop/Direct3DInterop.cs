using System.Runtime.InteropServices;

namespace Arsenal.UI.Services.Remote.Desktop;

/// <summary>
/// Direct3D 11 and DXGI, for the capture path that never leaves the graphics card.
/// </summary>
/// <remarks>
/// Separate from <see cref="MF"/> because it is a different stack
/// with different rules, but the conventions are the same ones and for the same reason.
/// Every method of every interface is declared in vtable order, and the slots this code
/// does not call are held by <c>Reserved</c> members with opaque arguments: they keep the
/// count right, they are impossible to call by accident, and a missing one would move
/// every method below it onto the wrong function with no compiler error and no exception
/// - just wrong behaviour, or a process that dies later somewhere else.
///
/// <para>Two interfaces are deliberately absent. <c>ID3D11DeviceContext</c> and
/// <c>ID3D11Texture2D</c> are never declared, because nothing here calls a method on
/// either: the texture is carried as a raw pointer from the duplication straight into
/// <c>MFCreateDXGISurfaceBuffer</c>, which wants an <c>IUnknown*</c> and an IID, and the
/// device context is only ever queried for something else. Not declaring an interface is
/// the one certain way not to get its vtable wrong.</para>
/// </remarks>
internal static class Direct3DInterop
{
    // ---- Identifiers ---------------------------------------------------------------

    internal static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    internal static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    internal static readonly Guid IID_IDXGIOutput1 = new("00cddea8-939b-4b83-a340-a685226666cc");
    /// <remarks>
    /// The adapter is taken as <c>IDXGIAdapter1</c> and never as the base interface.
    /// Asked for the base by IID, every adapter on this machine answered E_NOINTERFACE,
    /// which is not what the headers suggest and not worth arguing with: the derived one
    /// is what <c>EnumAdapters1</c> returns, it answers, and it carries every method this
    /// code needs.
    /// </remarks>
    internal static readonly Guid IID_IDXGIAdapter1 = new("29038f61-3839-4626-91fd-086879011a05");
    internal static readonly Guid IID_ID3D10Multithread = new("9b7e4e00-342c-4106-a19f-4f2704f689f0");
    internal static readonly Guid IID_ID3D11Device = new("db6f6ddb-ac77-4e88-8253-819df9bbf140");

    // ---- Creation ------------------------------------------------------------------

    internal const int D3D_DRIVER_TYPE_UNKNOWN = 0;
    internal const int D3D_DRIVER_TYPE_HARDWARE = 1;

    internal const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    internal const uint D3D11_CREATE_DEVICE_VIDEO_SUPPORT = 0x800;

    internal const int D3D_FEATURE_LEVEL_11_1 = 0xb100;
    internal const int D3D_FEATURE_LEVEL_11_0 = 0xb000;
    internal const int D3D_FEATURE_LEVEL_10_1 = 0xa100;
    internal const int D3D_FEATURE_LEVEL_10_0 = 0xa000;

    internal const uint D3D11_SDK_VERSION = 7;

    /// <remarks>
    /// The adapter is passed as a pointer rather than an interface so the caller can hand
    /// over the one the monitor is actually on. Driver type must be
    /// <see cref="D3D_DRIVER_TYPE_UNKNOWN"/> whenever an adapter is given; passing
    /// hardware with a non-null adapter fails with E_INVALIDARG, which is the kind of
    /// rule that reads like a formality until it costs an afternoon.
    /// </remarks>
    [DllImport("d3d11.dll", ExactSpelling = true)]
    internal static extern int D3D11CreateDevice(
        IntPtr adapter,
        int driverType,
        IntPtr software,
        uint flags,
        [In, MarshalAs(UnmanagedType.LPArray)] int[]? featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out IntPtr device,
        out int featureLevel,
        out IntPtr immediateContext);

    [DllImport("dxgi.dll", ExactSpelling = true)]
    internal static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

    // ---- Structures ----------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    internal struct LUID
    {
        internal uint LowPart;
        internal int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        internal int x;
        internal int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DXGI_OUTPUT_DESC
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        internal string DeviceName;
        internal RECT DesktopCoordinates;
        [MarshalAs(UnmanagedType.Bool)] internal bool AttachedToDesktop;
        internal int Rotation;
        internal IntPtr Monitor;
    }

    /// <remarks>
    /// <c>CharSet.Unicode</c> is not decoration. Without it <c>ByValTStr</c> marshals the
    /// name as single bytes, so the 128 wide characters DXGI writes are read as 128
    /// narrow ones: the description comes back as its first letter, every field after it
    /// is read from the wrong offset, and the native write runs 128 bytes past the end of
    /// the managed structure. The probe caught it because "NVIDIA..." printed as "N".
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DXGI_ADAPTER_DESC
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        internal string Description;
        internal uint VendorId;
        internal uint DeviceId;
        internal uint SubSysId;
        internal uint Revision;
        internal nuint DedicatedVideoMemory;
        internal nuint DedicatedSystemMemory;
        internal nuint SharedSystemMemory;
        internal LUID AdapterLuid;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DXGI_OUTDUPL_POINTER_POSITION
    {
        internal POINT Position;
        [MarshalAs(UnmanagedType.Bool)] internal bool Visible;
    }

    /// <remarks>
    /// <c>AccumulatedFrames</c> is how many desktop updates were folded into this one,
    /// and zero means the only thing that changed was the pointer. That is the signal
    /// this code uses to skip an encode, and it costs nothing: the alternative is
    /// comparing the pixels, which is a full pass over the frame on the processor.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DXGI_OUTDUPL_FRAME_INFO
    {
        internal long LastPresentTime;
        internal long LastMouseUpdateTime;
        internal uint AccumulatedFrames;
        [MarshalAs(UnmanagedType.Bool)] internal bool RectsCoalesced;
        [MarshalAs(UnmanagedType.Bool)] internal bool ProtectedContentMaskedOut;
        internal DXGI_OUTDUPL_POINTER_POSITION PointerPosition;
        internal uint TotalMetadataBufferSize;
        internal uint PointerShapeBufferSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DXGI_OUTDUPL_DESC
    {
        internal uint ModeWidth;
        internal uint ModeHeight;
        internal uint ModeRefreshRateNumerator;
        internal uint ModeRefreshRateDenominator;
        internal int Rotation;
        [MarshalAs(UnmanagedType.Bool)] internal bool DesktopImageInSystemMemory;
    }

    internal const int DXGI_FORMAT_B8G8R8A8_UNORM = 87;

    internal const uint D3D11_USAGE_DEFAULT = 0;
    internal const uint D3D11_BIND_SHADER_RESOURCE = 0x8;
    internal const uint D3D11_BIND_RENDER_TARGET = 0x20;

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_TEXTURE2D_DESC
    {
        internal uint Width;
        internal uint Height;
        internal uint MipLevels;
        internal uint ArraySize;
        internal int Format;
        internal uint SampleCount;
        internal uint SampleQuality;
        internal uint Usage;
        internal uint BindFlags;
        internal uint CPUAccessFlags;
        internal uint MiscFlags;
    }

    // ---- Results -------------------------------------------------------------------

    internal const int S_OK = 0;
    internal const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
    internal const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);
    internal const int DXGI_ERROR_ACCESS_DENIED = unchecked((int)0x887A002B);
    internal const int DXGI_ERROR_UNSUPPORTED = unchecked((int)0x887A0004);
    internal const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
    internal const int DXGI_ERROR_SESSION_DISCONNECTED = unchecked((int)0x887A0028);
    internal const int DXGI_ERROR_DEVICE_REMOVED = unchecked((int)0x887A0005);
    internal const int E_ACCESSDENIED = unchecked((int)0x80070005);

    // ---- Interfaces ----------------------------------------------------------------

    /// <summary>The base every DXGI object shares. Four slots, all held.</summary>
    [ComImport, Guid("aec22fb8-76f3-4639-9be0-28eb43a67a2e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDXGIObject
    {
        [PreserveSig] int Reserved0(IntPtr a, uint b, IntPtr c);
        [PreserveSig] int Reserved1(IntPtr a, IntPtr b);
        [PreserveSig] int Reserved2(IntPtr a, ref uint b, IntPtr c);
        [PreserveSig] int GetParent(in Guid riid, out IntPtr parent);
    }

    [ComImport, Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDXGIDevice : IDXGIObject
    {
        [PreserveSig] new int Reserved0(IntPtr a, uint b, IntPtr c);
        [PreserveSig] new int Reserved1(IntPtr a, IntPtr b);
        [PreserveSig] new int Reserved2(IntPtr a, ref uint b, IntPtr c);
        [PreserveSig] new int GetParent(in Guid riid, out IntPtr parent);

        [PreserveSig] int GetAdapter(out IntPtr adapter);
        [PreserveSig] int Reserved4(IntPtr a, uint b, uint c, IntPtr d, IntPtr e);
        [PreserveSig] int Reserved5(IntPtr a, uint b, IntPtr c);
        [PreserveSig] int Reserved6(int a);
        [PreserveSig] int Reserved7(IntPtr a);
    }

    /// <remarks>
    /// Declared rather than called through a hand-read vtable slot. The first attempt
    /// here did the latter for <c>EnumAdapters1</c> and got back a pointer that was not
    /// an adapter at all; the count was right on paper and wrong in practice, which is
    /// the entire reason the probe exists. An interface the runtime lays out cannot be
    /// off by one.
    /// </remarks>
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDXGIFactory1 : IDXGIObject
    {
        [PreserveSig] new int Reserved0(IntPtr a, uint b, IntPtr c);
        [PreserveSig] new int Reserved1(IntPtr a, IntPtr b);
        [PreserveSig] new int Reserved2(IntPtr a, ref uint b, IntPtr c);
        [PreserveSig] new int GetParent(in Guid riid, out IntPtr parent);

        [PreserveSig] int EnumAdapters(uint index, out IntPtr adapter);
        [PreserveSig] int Reserved5(IntPtr a, uint b);
        [PreserveSig] int Reserved6(IntPtr a);
        [PreserveSig] int Reserved7(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int Reserved8(IntPtr a, out IntPtr b);
        [PreserveSig] int EnumAdapters1(uint index, out IntPtr adapter);
        [PreserveSig] bool IsCurrent();
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDXGIAdapter1 : IDXGIObject
    {
        [PreserveSig] new int Reserved0(IntPtr a, uint b, IntPtr c);
        [PreserveSig] new int Reserved1(IntPtr a, IntPtr b);
        [PreserveSig] new int Reserved2(IntPtr a, ref uint b, IntPtr c);
        [PreserveSig] new int GetParent(in Guid riid, out IntPtr parent);

        [PreserveSig] int EnumOutputs(uint index, out IntPtr output);
        [PreserveSig] int GetDesc(out DXGI_ADAPTER_DESC desc);
        [PreserveSig] int Reserved6(in Guid a, IntPtr b);
        [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC desc);
    }

    /// <remarks>
    /// Only <see cref="DuplicateOutput"/> is ever called. Everything above it is the
    /// whole of <c>IDXGIOutput</c> plus the three <c>IDXGIOutput1</c> methods that come
    /// before it, held in order.
    /// </remarks>
    [ComImport, Guid("00cddea8-939b-4b83-a340-a685226666cc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDXGIOutput1 : IDXGIObject
    {
        [PreserveSig] new int Reserved0(IntPtr a, uint b, IntPtr c);
        [PreserveSig] new int Reserved1(IntPtr a, IntPtr b);
        [PreserveSig] new int Reserved2(IntPtr a, ref uint b, IntPtr c);
        [PreserveSig] new int GetParent(in Guid riid, out IntPtr parent);

        [PreserveSig] int GetDesc(out DXGI_OUTPUT_DESC desc);
        [PreserveSig] int Reserved5(uint a, uint b, ref uint c, IntPtr d);
        [PreserveSig] int Reserved6(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int Reserved7();
        [PreserveSig] int Reserved8(IntPtr a, int b);
        [PreserveSig] int Reserved9();
        [PreserveSig] int Reserved10(IntPtr a);
        [PreserveSig] int Reserved11(IntPtr a);
        [PreserveSig] int Reserved12(IntPtr a);
        [PreserveSig] int Reserved13(IntPtr a);
        [PreserveSig] int Reserved14(IntPtr a);
        [PreserveSig] int Reserved15(IntPtr a);
        [PreserveSig] int Reserved16(uint a, uint b, ref uint c, IntPtr d);
        [PreserveSig] int Reserved17(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int Reserved18(IntPtr a);
        [PreserveSig] int DuplicateOutput(IntPtr device, out IntPtr duplication);
    }

    /// <remarks>
    /// <see cref="AcquireNextFrame"/> is the clock for the whole capture path. It returns
    /// when the desktop has actually changed and not before, so a loop built on it runs
    /// at the rate the screen is moving rather than at a rate somebody guessed, and an
    /// idle desktop costs a timeout rather than a frame.
    ///
    /// <para>Every successful acquire must be matched by a <see cref="ReleaseFrame"/> or
    /// the next one fails, including the acquires whose frame is thrown away.</para>
    /// </remarks>
    [ComImport, Guid("191cfac3-a341-470d-b26e-a864f428319c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDXGIOutputDuplication : IDXGIObject
    {
        [PreserveSig] new int Reserved0(IntPtr a, uint b, IntPtr c);
        [PreserveSig] new int Reserved1(IntPtr a, IntPtr b);
        [PreserveSig] new int Reserved2(IntPtr a, ref uint b, IntPtr c);
        [PreserveSig] new int GetParent(in Guid riid, out IntPtr parent);

        [PreserveSig] void GetDesc(out DXGI_OUTDUPL_DESC desc);
        [PreserveSig] int AcquireNextFrame(uint timeoutMs, out DXGI_OUTDUPL_FRAME_INFO info, out IntPtr resource);
        [PreserveSig] int GetFrameDirtyRects(uint bufferSize, [Out, MarshalAs(UnmanagedType.LPArray)] RECT[] rects, out uint required);
        [PreserveSig] int Reserved6(uint a, IntPtr b, out uint c);
        [PreserveSig] int Reserved7(uint a, IntPtr b, out uint c, IntPtr d);
        [PreserveSig] int Reserved8(IntPtr a);
        [PreserveSig] int Reserved9();
        [PreserveSig] int ReleaseFrame();
    }

    /// <remarks>
    /// Only <c>CreateTexture2D</c> is called. The two slots before it are held, and
    /// everything after it is simply not declared: an interface may stop short, it just
    /// may not reorder.
    /// </remarks>
    [ComImport, Guid("db6f6ddb-ac77-4e88-8253-819df9bbf140"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ID3D11Device
    {
        [PreserveSig] int Reserved0(IntPtr a, IntPtr b, out IntPtr c);
        [PreserveSig] int Reserved1(IntPtr a, IntPtr b, out IntPtr c);
        [PreserveSig] int CreateTexture2D(in D3D11_TEXTURE2D_DESC desc, IntPtr initialData, out IntPtr texture);
    }

    /// <summary>
    /// The immediate context, declared only as far as the one copy this code performs.
    /// </summary>
    /// <remarks>
    /// Forty-four held slots to reach <c>CopyResource</c>: four for
    /// <c>ID3D11DeviceChild</c>, which this derives from and which comes first, then the
    /// forty of its own that precede the copy. The count was wrong by exactly those four
    /// on the first attempt, which is the arithmetic this pattern always gets wrong.
    ///
    /// <para>They are declared with no arguments on purpose. The runtime lays a COM
    /// interface out by declaration order and nothing else, so an unused slot needs the
    /// right position and not the right signature; giving these plausible-looking
    /// parameters would invite someone to call one, and calling a wrongly typed slot
    /// corrupts the stack. Empty is safer than approximate.</para>
    ///
    /// <para>The position is what the probe checks. A copy that landed on
    /// <c>RSSetScissorRects</c> instead would not throw - it would quietly do nothing and
    /// the encoder would be handed an empty texture.</para>
    /// </remarks>
    /// <remarks>
    /// Identified as <c>ID3D11DeviceContext1</c>, which every context on a supported
    /// version of Windows answers to and which returns the same pointer. The base
    /// interface's own identifier was asked for first and refused, the same way
    /// <c>IDXGIAdapter</c> was; the derived one answers, derives from it, and so begins
    /// with exactly the same slots. Only the identity changed, not the layout below.
    /// </remarks>
    [ComImport, Guid("bb2c6faa-b5fb-4082-8e6b-388b8cfa90e1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ID3D11DeviceContext
    {
        [PreserveSig] void Reserved0();
        [PreserveSig] void Reserved1();
        [PreserveSig] void Reserved2();
        [PreserveSig] void Reserved3();
        [PreserveSig] void Reserved4();
        [PreserveSig] void Reserved5();
        [PreserveSig] void Reserved6();
        [PreserveSig] void Reserved7();
        [PreserveSig] void Reserved8();
        [PreserveSig] void Reserved9();
        [PreserveSig] void Reserved10();
        [PreserveSig] void Reserved11();
        [PreserveSig] void Reserved12();
        [PreserveSig] void Reserved13();
        [PreserveSig] void Reserved14();
        [PreserveSig] void Reserved15();
        [PreserveSig] void Reserved16();
        [PreserveSig] void Reserved17();
        [PreserveSig] void Reserved18();
        [PreserveSig] void Reserved19();
        [PreserveSig] void Reserved20();
        [PreserveSig] void Reserved21();
        [PreserveSig] void Reserved22();
        [PreserveSig] void Reserved23();
        [PreserveSig] void Reserved24();
        [PreserveSig] void Reserved25();
        [PreserveSig] void Reserved26();
        [PreserveSig] void Reserved27();
        [PreserveSig] void Reserved28();
        [PreserveSig] void Reserved29();
        [PreserveSig] void Reserved30();
        [PreserveSig] void Reserved31();
        [PreserveSig] void Reserved32();
        [PreserveSig] void Reserved33();
        [PreserveSig] void Reserved34();
        [PreserveSig] void Reserved35();
        [PreserveSig] void Reserved36();
        [PreserveSig] void Reserved37();
        [PreserveSig] void Reserved38();
        [PreserveSig] void Reserved39();
        [PreserveSig] void Reserved40();
        [PreserveSig] void Reserved41();
        [PreserveSig] void Reserved42();
        [PreserveSig] void Reserved43();
        [PreserveSig] void CopyResource(IntPtr destination, IntPtr source);
    }

    /// <summary>
    /// The lock that lets the capture thread and Media Foundation's own worker threads
    /// touch one device.
    /// </summary>
    /// <remarks>
    /// Not optional. The encoder runs its callbacks on a Media Foundation work queue
    /// thread while this code is acquiring the next frame on its own, and a D3D11 device
    /// used from two threads without this is undefined behaviour of the kind that shows
    /// up as a corrupted picture on someone else's machine.
    /// </remarks>
    [ComImport, Guid("9b7e4e00-342c-4106-a19f-4f2704f689f0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ID3D10Multithread
    {
        [PreserveSig] void Enter();
        [PreserveSig] void Leave();
        [PreserveSig] bool SetMultithreadProtected([MarshalAs(UnmanagedType.Bool)] bool protect);
        [PreserveSig] bool GetMultithreadProtected();
    }
}
