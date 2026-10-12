using System.Runtime.InteropServices;

namespace Arsenal.UI.Controls
{
    /// <summary>
    /// The parts of DirectManipulation that <see cref="TouchpadScroll"/> uses.
    /// </summary>
    /// <remarks>
    /// Every method of each interface is declared, in header order, even the ones nothing
    /// calls: a COM call goes through the slot at that position in the vtable, so leaving
    /// one out sends every call after it to the wrong method. Interface pointers that are
    /// only ever passed through are taken as <see cref="IntPtr"/> so that a callback
    /// arriving every frame does not wrap a fresh runtime callable wrapper each time.
    /// </remarks>
    internal static class DirectManipulationInterop
    {
        internal const int ConfigurationInteraction = 0x0001;
        internal const int ConfigurationTranslationY = 0x0004;
        internal const int ConfigurationTranslationInertia = 0x0020;

        internal const int ViewportOptionsManualUpdate = 0x0002;

        internal const int StatusRunning = 3;
        internal const int StatusInertia = 4;
        internal const int StatusReady = 5;

        internal const int PointerTypeTouchpad = 5;

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public NativeRect(int left, int top, int right, int bottom)
            {
                Left = left;
                Top = top;
                Right = right;
                Bottom = bottom;
            }
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetPointerType(uint pointerId, out int pointerType);

        [ComImport, Guid("54E211B6-3650-4F75-8334-FA359598E1C5")]
        internal class DirectManipulationManager
        {
        }

        [ComImport, Guid("FBF5D3B4-70C7-4163-9322-5A6F660D6FBC"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IDirectManipulationManager
        {
            void Activate(IntPtr window);
            void Deactivate(IntPtr window);
            void RegisterHitTestTarget(IntPtr window, IntPtr hitTestWindow, int type);
            void ProcessInput(IntPtr message, out int handled);
            void GetUpdateManager(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object updateManager);
            void CreateViewport(IntPtr frameInfo, IntPtr window, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object viewport);
            void CreateContent(IntPtr frameInfo, ref Guid clsid, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object content);
        }

        [ComImport, Guid("B0AE62FD-BE34-46E7-9CAA-D361FACBB9CC"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IDirectManipulationUpdateManager
        {
            void RegisterWaitHandleCallback(IntPtr handle, IntPtr eventHandler, out uint cookie);
            void UnregisterWaitHandleCallback(uint cookie);
            void Update(IntPtr frameInfo);
        }

        [ComImport, Guid("28B85A3D-60A0-48BD-9BA1-5CE8D9EA3A6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IDirectManipulationViewport
        {
            void Enable();
            void Disable();
            void SetContact(uint pointerId);
            void ReleaseContact(uint pointerId);
            void ReleaseAllContacts();
            void GetStatus(out int status);
            void GetTag(ref Guid riid, IntPtr tagObject, IntPtr id);
            void SetTag(IntPtr tagObject, uint id);
            void GetViewportRect(out NativeRect viewport);
            void SetViewportRect(ref NativeRect viewport);
            void ZoomToRect(float left, float top, float right, float bottom, int animate);
            void SetViewportTransform([MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] float[] matrix, uint pointCount);
            void SyncDisplayTransform([MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] float[] matrix, uint pointCount);
            void GetPrimaryContent(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object content);
            void AddContent(IntPtr content);
            void RemoveContent(IntPtr content);
            void SetViewportOptions(int options);
            void AddConfiguration(int configuration);
            void RemoveConfiguration(int configuration);
            void ActivateConfiguration(int configuration);
            void SetManualGesture(int configuration);
            void SetChaining(int enabledTypes);
            void AddEventHandler(IntPtr window, IDirectManipulationViewportEventHandler eventHandler, out uint cookie);
            void RemoveEventHandler(uint cookie);
            void SetInputMode(int mode);
            void SetUpdateMode(int mode);
            void Stop();
            void Abandon();
        }

        [ComImport, Guid("952121DA-D69F-45F9-B0F9-F23944321A6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IDirectManipulationViewportEventHandler
        {
            void OnViewportStatusChanged(IntPtr viewport, int current, int previous);
            void OnViewportUpdated(IntPtr viewport);
            void OnContentUpdated(IntPtr viewport, IntPtr content);
        }

        [ComImport, Guid("B89962CB-3D89-442B-BB58-5098FA0F9F16"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IDirectManipulationContent
        {
            void GetContentRect(out NativeRect contentSize);
            void SetContentRect(ref NativeRect contentSize);
            void GetViewport(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object viewport);
            void GetTag(ref Guid riid, IntPtr tagObject, IntPtr id);
            void SetTag(IntPtr tagObject, uint id);
            void GetOutputTransform([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] float[] matrix, uint pointCount);
            void GetContentTransform([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] float[] matrix, uint pointCount);
            void SyncContentTransform([MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] float[] matrix, uint pointCount);
        }
    }
}
