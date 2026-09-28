using Arsenal.UI.ViewModels;
using System.Windows;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Arsenal.UI.Views.Windows
{
    public partial class QuickPanelWindow
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PhysicalToLogicalPointForPerMonitorDPI(IntPtr hwnd, ref PointI point);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        private const uint MonitorDefaultToNearest = 2;
        private const int MonitorDpiTypeEffective = 0;

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(PointI point, uint flags);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(
            IntPtr monitor,
            int dpiType,
            out uint dpiX,
            out uint dpiY);

        private const uint AbmGetTaskbarPos = 0x00000005;
        private const uint AbeLeft = 0;
        private const uint AbeTop = 1;
        private const uint AbeRight = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct AppBarData
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public NativeRect rc;
            public int lParam;
        }

        [DllImport("shell32.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern IntPtr SHAppBarMessage(uint message, ref AppBarData data);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [Flags]
        private enum SetWindowPositionFlags : uint
        {
            NoSize = 0x0001,
            NoZOrder = 0x0004,
            NoActivate = 0x0010
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect bounds);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(
            IntPtr hwnd,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            SetWindowPositionFlags flags);

        private delegate void WinEventProc(
            IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
        private const int ObjIdWindow = 0;

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(
            uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
            WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

        private const int WhMouseLowLevel = 14;
        private const int WmLButtonDown = 0x0201;
        private const int WmLButtonUp = 0x0202;
        private const int WmRButtonDown = 0x0204;
        private const int WmRButtonUp = 0x0205;
        private const int WmMButtonDown = 0x0207;
        private const int WmMButtonUp = 0x0208;
        private const int WmNcLButtonDown = 0x00A1;
        private const int WmNcLButtonUp = 0x00A2;

        [StructLayout(LayoutKind.Sequential)]
        private struct PointI
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseLowLevelHookStruct
        {
            public PointI Point;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(
            int idHook, LowLevelMouseProc callback, IntPtr module, uint threadId);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? name);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SetActiveWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint attachTo, uint attachFrom, bool attach);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
    }
}
