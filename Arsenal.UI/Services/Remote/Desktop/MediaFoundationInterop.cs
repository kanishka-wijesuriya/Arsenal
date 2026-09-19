using System.Runtime.InteropServices;

namespace Arsenal.UI.Services.Remote.Desktop;

/// <summary>
/// Just enough Media Foundation to drive a video encoder.
/// </summary>
/// <remarks>
/// Hand-written rather than taken from a package. The surface needed here is small and
/// fixed - enumerate an encoder, describe two media types, push frames through it - and
/// Arsenal keeps a short dependency list on purpose; the alternative was a graphics
/// interop library an order of magnitude larger than the thing being used from it.
///
/// <para><b>How to read the interfaces below.</b> A COM interface is a table of function
/// pointers in declaration order, so every method has to occupy its slot whether this
/// code calls it or not. Methods that are never called are declared as
/// <c>Reserved</c> with opaque arguments: they hold the slot, they are impossible to
/// call by accident, and they cannot be wrong in a way that matters. Getting a slot
/// count wrong is the one mistake here that is not a compile error and not an
/// exception, it is a call landing on the wrong function.</para>
///
/// <para>Every method is <c>[PreserveSig]</c> and returns its HRESULT, because several
/// of them return failure as part of normal operation: an encoder with nothing ready
/// yet answers <c>MF_E_TRANSFORM_NEED_MORE_INPUT</c> on most frames, and letting the
/// marshaller turn that into an exception would cost one throw per frame.</para>
///
/// <para><b>Every array parameter is marked <c>LPArray</c>, and has to be.</b> In a
/// COM interface, unlike a P/Invoke, the default marshalling for an array is SAFEARRAY:
/// the callee is handed a descriptor where it expected a pointer to bytes. Media
/// Foundation then writes the parameter sets straight over the runtime's own heap
/// structures and the process dies later, somewhere else, with a heap corruption that
/// names nothing to do with this file. That is what happened; it is written down here
/// because nothing about the declaration looks wrong.</para>
/// </remarks>
internal static class MF
{
    // ---- Media type and sample attributes ----------------------------------------

    internal static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    internal static readonly Guid MFVideoFormat_NV12 = new("3231564E-0000-0010-8000-00AA00389B71");
    internal static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
    internal static readonly Guid MFVideoFormat_HEVC = new("43564548-0000-0010-8000-00AA00389B71");

    internal static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    internal static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    internal static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    internal static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    internal static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    internal static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    internal static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    internal static readonly Guid MF_MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    internal static readonly Guid MF_MT_ALL_SAMPLES_INDEPENDENT = new("c9173739-5e56-461c-b713-46fb995cb95f");
    internal static readonly Guid MF_MT_YUV_MATRIX = new("3e23d450-2c75-4d25-a00e-b91670d12327");
    internal static readonly Guid MF_MT_VIDEO_NOMINAL_RANGE = new("c21b8ee5-b956-4071-8daf-325edf5cab11");

    /// <summary>Set on an input sample to make the encoder emit an IDR for it.</summary>
    /// <remarks>
    /// The alternative is ICodecAPI and a VARIANT, which is another interface and a
    /// hand-built 24-byte union for a value the sample can simply carry.
    /// </remarks>
    internal static readonly Guid MFSampleExtension_CleanPoint = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");

    internal static readonly Guid MFT_CATEGORY_VIDEO_ENCODER = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
    internal static readonly Guid MFT_FRIENDLY_NAME_Attribute = new("314ffbae-5b41-4c95-9c19-4e7d586face3");
    internal static readonly Guid MF_TRANSFORM_ASYNC = new("f81a699a-649a-497d-8c73-29f8fed6ad7a");
    internal static readonly Guid MF_TRANSFORM_ASYNC_UNLOCK = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");

    internal const uint MFVideoInterlace_Progressive = 2;
    internal const uint MFVideoTransferMatrix_BT709 = 1;
    internal const uint MFNominalRange_16_235 = 2;
    internal const uint eAVEncH264VProfile_High = 100;
    internal const uint eAVEncH265VProfile_Main_420_8 = 1;

    internal const uint MFT_ENUM_FLAG_SYNCMFT = 0x00000001;
    internal const uint MFT_ENUM_FLAG_ASYNCMFT = 0x00000002;
    internal const uint MFT_ENUM_FLAG_HARDWARE = 0x00000004;
    internal const uint MFT_ENUM_FLAG_SORTANDFILTER = 0x00000040;

    internal const int MFT_MESSAGE_COMMAND_FLUSH = 0x00000000;
    internal const int MFT_MESSAGE_COMMAND_DRAIN = 0x00000001;
    internal const int MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000;
    internal const int MFT_MESSAGE_NOTIFY_END_STREAMING = 0x10000001;
    internal const int MFT_MESSAGE_NOTIFY_END_OF_STREAM = 0x10000002;
    internal const int MFT_MESSAGE_NOTIFY_START_OF_STREAM = 0x10000003;

    internal const uint METransformNeedInput = 601;
    internal const uint METransformHaveOutput = 602;

    internal const int S_OK = 0;
    internal const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    internal const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
    internal const int MF_E_INVALIDMEDIATYPE = unchecked((int)0xC00D36B4);
    internal const int MF_E_NO_MORE_TYPES = unchecked((int)0xC00D36B9);

    /// <summary>MF_SDK_VERSION 2, MF_API_VERSION 0x70.</summary>
    internal const uint MF_VERSION = 0x00020070;

    internal const uint MFSTARTUP_LITE = 1;

    [DllImport("mfplat.dll", ExactSpelling = true)]
    internal static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    internal static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    internal static extern int MFCreateMediaType(out IMFMediaType type);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    internal static extern int MFCreateSample(out IMFSample sample);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    internal static extern int MFCreateMemoryBuffer(int maxLength, out IMFMediaBuffer buffer);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    internal static extern int MFTEnumEx(
        Guid category,
        uint flags,
        [In] MFT_REGISTER_TYPE_INFO? inputType,
        [In] MFT_REGISTER_TYPE_INFO? outputType,
        out IntPtr activateArray,
        out uint activateCount);

    [DllImport("ole32.dll", ExactSpelling = true)]
    internal static extern void CoTaskMemFree(IntPtr memory);

    [StructLayout(LayoutKind.Sequential)]
    internal sealed class MFT_REGISTER_TYPE_INFO
    {
        internal Guid guidMajorType;
        internal Guid guidSubtype;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MFT_OUTPUT_STREAM_INFO
    {
        internal uint dwFlags;
        internal uint cbSize;
        internal uint cbAlignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MFT_INPUT_STREAM_INFO
    {
        internal long hnsMaxLatency;
        internal uint dwFlags;
        internal uint cbSize;
        internal uint cbMaxLookahead;
        internal uint cbAlignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MFT_OUTPUT_DATA_BUFFER
    {
        internal uint dwStreamID;
        /// <summary>
        /// The encoder hands ownership of this back with each call.
        /// </summary>
        /// <remarks>
        /// Raw rather than marshalled: with an interface type here the runtime releases
        /// it on the way out of every call, including the calls that return
        /// NEED_MORE_INPUT and leave it null, and getting that wrong either leaks one
        /// sample per frame or double-releases one.
        /// </remarks>
        internal IntPtr pSample;
        internal uint dwStatus;
        internal IntPtr pEvents;
    }

    /// <summary>Packs a width and height into the 64-bit form MF uses for both.</summary>
    internal static ulong Pack(uint high, uint low) => ((ulong)high << 32) | low;

    // ---- Interfaces --------------------------------------------------------------

    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFAttributes
    {
        [PreserveSig] int Reserved0(IntPtr a, IntPtr b);
        [PreserveSig] int Reserved1(IntPtr a, IntPtr b);
        [PreserveSig] int Reserved2(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int Reserved3(IntPtr a, int b, IntPtr c);
        [PreserveSig] int GetUINT32(ref Guid key, out uint value);
        [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
        [PreserveSig] int Reserved6(IntPtr a, IntPtr b);
        [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength(ref Guid key, out uint length);
        [PreserveSig] int GetString(ref Guid key, [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder value, uint size, IntPtr length);
        [PreserveSig] int Reserved10(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int GetBlobSize(ref Guid key, out uint size);
        [PreserveSig] int GetBlob(ref Guid key, [Out, MarshalAs(UnmanagedType.LPArray)] byte[] buffer, uint bufferSize, IntPtr blobSize);
        [PreserveSig] int Reserved13(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int Reserved14(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int Reserved15(IntPtr a, IntPtr b);
        [PreserveSig] int Reserved16(IntPtr a);
        [PreserveSig] int Reserved17();
        [PreserveSig] int SetUINT32(ref Guid key, uint value);
        [PreserveSig] int SetUINT64(ref Guid key, ulong value);
        [PreserveSig] int Reserved20(IntPtr a, double b);
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] int Reserved22(IntPtr a, IntPtr b);
        [PreserveSig] int Reserved23(IntPtr a, IntPtr b, uint c);
        [PreserveSig] int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object? value);
        [PreserveSig] int Reserved25();
        [PreserveSig] int Reserved26();
        [PreserveSig] int Reserved27(IntPtr a);
        [PreserveSig] int Reserved28(uint a, IntPtr b, IntPtr c);
        [PreserveSig] int Reserved29(IntPtr a);
    }

    [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaType : IMFAttributes
    {
        // IMFAttributes, again, because C# does not inherit a COM vtable.
        [PreserveSig] new int Reserved0(IntPtr a, IntPtr b);
        [PreserveSig] new int Reserved1(IntPtr a, IntPtr b);
        [PreserveSig] new int Reserved2(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] new int Reserved3(IntPtr a, int b, IntPtr c);
        [PreserveSig] new int GetUINT32(ref Guid key, out uint value);
        [PreserveSig] new int GetUINT64(ref Guid key, out ulong value);
        [PreserveSig] new int Reserved6(IntPtr a, IntPtr b);
        [PreserveSig] new int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] new int GetStringLength(ref Guid key, out uint length);
        [PreserveSig] new int GetString(ref Guid key, [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder value, uint size, IntPtr length);
        [PreserveSig] new int Reserved10(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] new int GetBlobSize(ref Guid key, out uint size);
        [PreserveSig] new int GetBlob(ref Guid key, [Out, MarshalAs(UnmanagedType.LPArray)] byte[] buffer, uint bufferSize, IntPtr blobSize);
        [PreserveSig] new int Reserved13(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] new int Reserved14(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] new int Reserved15(IntPtr a, IntPtr b);
        [PreserveSig] new int Reserved16(IntPtr a);
        [PreserveSig] new int Reserved17();
        [PreserveSig] new int SetUINT32(ref Guid key, uint value);
        [PreserveSig] new int SetUINT64(ref Guid key, ulong value);
        [PreserveSig] new int Reserved20(IntPtr a, double b);
        [PreserveSig] new int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] new int Reserved22(IntPtr a, IntPtr b);
        [PreserveSig] new int Reserved23(IntPtr a, IntPtr b, uint c);
        [PreserveSig] new int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object? value);
        [PreserveSig] new int Reserved25();
        [PreserveSig] new int Reserved26();
        [PreserveSig] new int Reserved27(IntPtr a);
        [PreserveSig] new int Reserved28(uint a, IntPtr b, IntPtr c);
        [PreserveSig] new int Reserved29(IntPtr a);

        [PreserveSig] int GetMajorType(out Guid major);
        [PreserveSig] int IsCompressedFormat([MarshalAs(UnmanagedType.Bool)] out bool compressed);
        [PreserveSig] int Reserved32(IntPtr a, IntPtr b);
        [PreserveSig] int Reserved33(Guid a, IntPtr b);
        [PreserveSig] int Reserved34(Guid a, IntPtr b);
    }

    [ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr buffer, out int maxLength, out int currentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out int length);
        [PreserveSig] int SetCurrentLength(int length);
        [PreserveSig] int GetMaxLength(out int length);
    }

    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSample : IMFAttributes
    {
        [PreserveSig] new int Reserved0(IntPtr a, IntPtr b);
        [PreserveSig] new int Reserved1(IntPtr a, IntPtr b);
        [PreserveSig] new int Reserved2(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] new int Reserved3(IntPtr a, int b, IntPtr c);
        [PreserveSig] new int GetUINT32(ref Guid key, out uint value);
        [PreserveSig] new int GetUINT64(ref Guid key, out ulong value);
        [PreserveSig] new int Reserved6(IntPtr a, IntPtr b);
        [PreserveSig] new int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] new int GetStringLength(ref Guid key, out uint length);
        [PreserveSig] new int GetString(ref Guid key, [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder value, uint size, IntPtr length);
        [PreserveSig] new int Reserved10(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] new int GetBlobSize(ref Guid key, out uint size);
        [PreserveSig] new int GetBlob(ref Guid key, [Out, MarshalAs(UnmanagedType.LPArray)] byte[] buffer, uint bufferSize, IntPtr blobSize);
        [PreserveSig] new int Reserved13(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] new int Reserved14(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] new int Reserved15(IntPtr a, IntPtr b);
        [PreserveSig] new int Reserved16(IntPtr a);
        [PreserveSig] new int Reserved17();
        [PreserveSig] new int SetUINT32(ref Guid key, uint value);
        [PreserveSig] new int SetUINT64(ref Guid key, ulong value);
        [PreserveSig] new int Reserved20(IntPtr a, double b);
        [PreserveSig] new int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] new int Reserved22(IntPtr a, IntPtr b);
        [PreserveSig] new int Reserved23(IntPtr a, IntPtr b, uint c);
        [PreserveSig] new int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object? value);
        [PreserveSig] new int Reserved25();
        [PreserveSig] new int Reserved26();
        [PreserveSig] new int Reserved27(IntPtr a);
        [PreserveSig] new int Reserved28(uint a, IntPtr b, IntPtr c);
        [PreserveSig] new int Reserved29(IntPtr a);

        [PreserveSig] int GetSampleFlags(out uint flags);
        [PreserveSig] int SetSampleFlags(uint flags);
        [PreserveSig] int GetSampleTime(out long time);
        [PreserveSig] int SetSampleTime(long time);
        [PreserveSig] int GetSampleDuration(out long duration);
        [PreserveSig] int SetSampleDuration(long duration);
        [PreserveSig] int GetBufferCount(out uint count);
        [PreserveSig] int GetBufferByIndex(uint index, out IMFMediaBuffer buffer);
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
        [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
        [PreserveSig] int RemoveBufferByIndex(uint index);
        [PreserveSig] int RemoveAllBuffers();
        [PreserveSig] int GetTotalLength(out int length);
        [PreserveSig] int CopyToBuffer(IMFMediaBuffer buffer);
    }

    [ComImport, Guid("bf94c121-5b05-4e6f-8000-ba598961414d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFTransform
    {
        [PreserveSig] int GetStreamLimits(out uint inputMin, out uint inputMax, out uint outputMin, out uint outputMax);
        [PreserveSig] int GetStreamCount(out uint inputs, out uint outputs);
        [PreserveSig] int GetStreamIDs(uint inputSize, [Out, MarshalAs(UnmanagedType.LPArray)] uint[]? inputIds, uint outputSize, [Out, MarshalAs(UnmanagedType.LPArray)] uint[]? outputIds);
        [PreserveSig] int GetInputStreamInfo(uint id, out MFT_INPUT_STREAM_INFO info);
        [PreserveSig] int GetOutputStreamInfo(uint id, out MFT_OUTPUT_STREAM_INFO info);
        [PreserveSig] int GetAttributes(out IMFAttributes attributes);
        [PreserveSig] int Reserved6(uint a, IntPtr b);
        [PreserveSig] int Reserved7(uint a, IntPtr b);
        [PreserveSig] int Reserved8(uint a);
        [PreserveSig] int Reserved9(uint a, IntPtr b);
        [PreserveSig] int GetInputAvailableType(uint id, uint index, out IMFMediaType? type);
        [PreserveSig] int GetOutputAvailableType(uint id, uint index, out IMFMediaType? type);
        [PreserveSig] int SetInputType(uint id, IMFMediaType? type, uint flags);
        [PreserveSig] int SetOutputType(uint id, IMFMediaType? type, uint flags);
        [PreserveSig] int GetInputCurrentType(uint id, out IMFMediaType? type);
        [PreserveSig] int GetOutputCurrentType(uint id, out IMFMediaType? type);
        [PreserveSig] int GetInputStatus(uint id, out uint flags);
        [PreserveSig] int GetOutputStatus(out uint flags);
        [PreserveSig] int SetOutputBounds(long lower, long upper);
        [PreserveSig] int ProcessEvent(uint id, IntPtr mediaEvent);
        [PreserveSig] int ProcessMessage(int message, IntPtr parameter);
        [PreserveSig] int ProcessInput(uint id, IMFSample sample, uint flags);
        [PreserveSig] int ProcessOutput(uint flags, uint bufferCount, ref MFT_OUTPUT_DATA_BUFFER buffers, out uint status);
    }

    [ComImport, Guid("7fee9e9a-4a89-47a6-899c-b6a53a70fb67"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFActivate : IMFAttributes
    {
        [PreserveSig] new int Reserved0(IntPtr a, IntPtr b);
        [PreserveSig] new int Reserved1(IntPtr a, IntPtr b);
        [PreserveSig] new int Reserved2(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] new int Reserved3(IntPtr a, int b, IntPtr c);
        [PreserveSig] new int GetUINT32(ref Guid key, out uint value);
        [PreserveSig] new int GetUINT64(ref Guid key, out ulong value);
        [PreserveSig] new int Reserved6(IntPtr a, IntPtr b);
        [PreserveSig] new int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] new int GetStringLength(ref Guid key, out uint length);
        [PreserveSig] new int GetString(ref Guid key, [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder value, uint size, IntPtr length);
        [PreserveSig] new int Reserved10(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] new int GetBlobSize(ref Guid key, out uint size);
        [PreserveSig] new int GetBlob(ref Guid key, [Out, MarshalAs(UnmanagedType.LPArray)] byte[] buffer, uint bufferSize, IntPtr blobSize);
        [PreserveSig] new int Reserved13(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] new int Reserved14(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] new int Reserved15(IntPtr a, IntPtr b);
        [PreserveSig] new int Reserved16(IntPtr a);
        [PreserveSig] new int Reserved17();
        [PreserveSig] new int SetUINT32(ref Guid key, uint value);
        [PreserveSig] new int SetUINT64(ref Guid key, ulong value);
        [PreserveSig] new int Reserved20(IntPtr a, double b);
        [PreserveSig] new int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] new int Reserved22(IntPtr a, IntPtr b);
        [PreserveSig] new int Reserved23(IntPtr a, IntPtr b, uint c);
        [PreserveSig] new int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object? value);
        [PreserveSig] new int Reserved25();
        [PreserveSig] new int Reserved26();
        [PreserveSig] new int Reserved27(IntPtr a);
        [PreserveSig] new int Reserved28(uint a, IntPtr b, IntPtr c);
        [PreserveSig] new int Reserved29(IntPtr a);

        [PreserveSig] int ActivateObject(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int ShutdownObject();
        [PreserveSig] int DetachObject();
    }

    /// <summary>
    /// How an asynchronous transform says it wants a frame, or has one.
    /// </summary>
    /// <remarks>
    /// <see cref="GetEvent"/> is in the interface and looks like the easy way to read
    /// these, and on every hardware encoder tried here it returns
    /// MF_E_NO_EVENTS_AVAILABLE forever: the driver's MFTs only start pumping once a
    /// callback has been registered through <see cref="BeginGetEvent"/>. Polling is
    /// still used for the sync encoders, which have no event queue at all.
    /// </remarks>
    [ComImport, Guid("2cd0bd52-bcd5-4b89-b62c-eadc0c031e7d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaEventGenerator
    {
        [PreserveSig] int GetEvent(uint flags, out IMFMediaEvent? mediaEvent);
        [PreserveSig] int BeginGetEvent(IMFAsyncCallback callback, [MarshalAs(UnmanagedType.IUnknown)] object? state);
        [PreserveSig] int EndGetEvent(IMFAsyncResult result, out IMFMediaEvent? mediaEvent);
        [PreserveSig] int QueueEvent(uint met, ref Guid extendedType, int status, IntPtr value);
    }

    // ---- Codec settings ----------------------------------------------------------

    internal static readonly Guid CODECAPI_AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    internal static readonly Guid CODECAPI_AVEncCommonQuality = new("fcbf57a3-7ea5-4b0c-9644-69b40c39c391");
    internal static readonly Guid CODECAPI_AVEncCommonMaxBitRate = new("9651eae4-39b9-4ebf-85ef-d7f444ec7465");
    internal static readonly Guid CODECAPI_AVEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    internal static readonly Guid CODECAPI_AVEncMPVGOPSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    internal static readonly Guid CODECAPI_AVEncVideoEncodeQP = new("2cb5696b-23fb-4ce1-a0f9-ef5b90fd55ca");
    internal static readonly Guid CODECAPI_AVLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    internal static readonly Guid CODECAPI_AVEncVideoForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");

    internal const uint eAVEncCommonRateControlMode_CBR = 0;
    internal const uint eAVEncCommonRateControlMode_UnconstrainedVBR = 2;
    internal const uint eAVEncCommonRateControlMode_Quality = 3;
    internal const uint eAVEncCommonRateControlMode_LowDelayVBR = 4;

    internal const ushort VT_UI4 = 19;
    internal const ushort VT_BOOL = 11;

    /// <summary>
    /// A VARIANT, only ever holding a 32-bit unsigned value.
    /// </summary>
    /// <remarks>
    /// Twenty four bytes on x64: the tag, six bytes of padding, and a sixteen byte
    /// union. Declared in full rather than as the eight byte form that looks right,
    /// because ICodecAPI reads the whole structure and a short one hands it whatever
    /// was on the stack.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    internal struct VARIANT
    {
        internal ushort vt;
        internal ushort reserved1;
        internal ushort reserved2;
        internal ushort reserved3;
        internal long value;
        internal long valueHigh;

        internal static VARIANT FromUInt32(uint number) => new() { vt = VT_UI4, value = number };
    }

    /// <summary>
    /// The knobs a codec exposes beyond what a media type can describe.
    /// </summary>
    /// <remarks>
    /// Rate control lives here and nowhere else. A media type can name an average
    /// bitrate and every encoder reads that as constant bitrate, which for a desktop
    /// means a still screen is padded to ten megabits a second so the numbers come out
    /// right. What a remote desktop wants is the opposite: spend bits when the screen
    /// moves and almost none when it does not.
    /// </remarks>
    [ComImport, Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICodecAPI
    {
        [PreserveSig] int IsSupported(ref Guid api);
        [PreserveSig] int IsModifiable(ref Guid api);
        [PreserveSig] int Reserved2(ref Guid api, out VARIANT min, out VARIANT max, out VARIANT step);
        [PreserveSig] int Reserved3(ref Guid api, out IntPtr values, out uint count);
        [PreserveSig] int Reserved4(ref Guid api, out VARIANT value);
        [PreserveSig] int GetValue(ref Guid api, out VARIANT value);
        [PreserveSig] int SetValue(ref Guid api, ref VARIANT value);
    }

    [ComImport, Guid("a27003cf-2354-4f2a-8d6a-ab7cff15437e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFAsyncCallback
    {
        [PreserveSig] int GetParameters(out uint flags, out uint queue);
        [PreserveSig] int Invoke(IMFAsyncResult result);
    }

    [ComImport, Guid("ac6b7889-0740-4d51-8619-905994a55cc6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFAsyncResult
    {
        [PreserveSig] int GetState(out IntPtr state);
        [PreserveSig] int GetStatus();
        [PreserveSig] int SetStatus(int status);
        [PreserveSig] int GetObject(out IntPtr obj);
        /// <summary>Returns the pointer itself, not an HRESULT.</summary>
        [PreserveSig] IntPtr GetStateNoAddRef();
    }

    [ComImport, Guid("df598932-f10c-4e39-bba2-c308f101daa3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaEvent : IMFAttributes
    {
        [PreserveSig] new int Reserved0(IntPtr a, IntPtr b);
        [PreserveSig] new int Reserved1(IntPtr a, IntPtr b);
        [PreserveSig] new int Reserved2(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] new int Reserved3(IntPtr a, int b, IntPtr c);
        [PreserveSig] new int GetUINT32(ref Guid key, out uint value);
        [PreserveSig] new int GetUINT64(ref Guid key, out ulong value);
        [PreserveSig] new int Reserved6(IntPtr a, IntPtr b);
        [PreserveSig] new int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] new int GetStringLength(ref Guid key, out uint length);
        [PreserveSig] new int GetString(ref Guid key, [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder value, uint size, IntPtr length);
        [PreserveSig] new int Reserved10(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] new int GetBlobSize(ref Guid key, out uint size);
        [PreserveSig] new int GetBlob(ref Guid key, [Out, MarshalAs(UnmanagedType.LPArray)] byte[] buffer, uint bufferSize, IntPtr blobSize);
        [PreserveSig] new int Reserved13(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] new int Reserved14(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] new int Reserved15(IntPtr a, IntPtr b);
        [PreserveSig] new int Reserved16(IntPtr a);
        [PreserveSig] new int Reserved17();
        [PreserveSig] new int SetUINT32(ref Guid key, uint value);
        [PreserveSig] new int SetUINT64(ref Guid key, ulong value);
        [PreserveSig] new int Reserved20(IntPtr a, double b);
        [PreserveSig] new int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] new int Reserved22(IntPtr a, IntPtr b);
        [PreserveSig] new int Reserved23(IntPtr a, IntPtr b, uint c);
        [PreserveSig] new int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object? value);
        [PreserveSig] new int Reserved25();
        [PreserveSig] new int Reserved26();
        [PreserveSig] new int Reserved27(IntPtr a);
        [PreserveSig] new int Reserved28(uint a, IntPtr b, IntPtr c);
        [PreserveSig] new int Reserved29(IntPtr a);

        [PreserveSig] int GetType(out uint eventType);
        [PreserveSig] int GetExtendedType(out Guid extendedType);
        [PreserveSig] int GetStatus(out int status);
        [PreserveSig] int GetValue(IntPtr value);
    }
}
