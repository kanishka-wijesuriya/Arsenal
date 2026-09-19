using System.Runtime.InteropServices;

namespace Arsenal.UI.Services.Remote.Desktop;

/// <summary>
/// The Win32 surface the remote session needs, in one place.
/// </summary>
/// <remarks>
/// Kept separate from the rest of the application's interop because everything here is
/// only ever called while a session is live, and because the capture and injection paths
/// are the two places where a wrong struct layout produces a silent wrong answer rather
/// than an exception. Every layout below is the documented one for 64-bit Windows.
/// </remarks>
internal static class RemoteNative
{
    // ---- Geometry ----------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        internal int x;
        internal int y;
    }

    // ---- Monitors ----------------------------------------------------------------

    internal const int MONITORINFOF_PRIMARY = 1;
    internal const int ENUM_CURRENT_SETTINGS = -1;

    internal const int SM_CXCURSOR = 13;
    internal const int SM_CYCURSOR = 14;
    internal const int SM_XVIRTUALSCREEN = 76;
    internal const int SM_YVIRTUALSCREEN = 77;
    internal const int SM_CXVIRTUALSCREEN = 78;
    internal const int SM_CYVIRTUALSCREEN = 79;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MONITORINFOEX
    {
        internal int cbSize;
        internal RECT rcMonitor;
        internal RECT rcWork;
        internal int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        internal string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        internal string dmDeviceName;
        internal short dmSpecVersion;
        internal short dmDriverVersion;
        internal short dmSize;
        internal short dmDriverExtra;
        internal int dmFields;
        internal int dmPositionX;
        internal int dmPositionY;
        internal int dmDisplayOrientation;
        internal int dmDisplayFixedOutput;
        internal short dmColor;
        internal short dmDuplex;
        internal short dmYResolution;
        internal short dmTTOption;
        internal short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        internal string dmFormName;
        internal short dmLogPixels;
        internal int dmBitsPerPel;
        internal int dmPelsWidth;
        internal int dmPelsHeight;
        internal int dmDisplayFlags;
        internal int dmDisplayFrequency;
        internal int dmICMMethod;
        internal int dmICMIntent;
        internal int dmMediaType;
        internal int dmDitherType;
        internal int dmReserved1;
        internal int dmReserved2;
        internal int dmPanningWidth;
        internal int dmPanningHeight;
    }

    internal delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    internal static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    internal static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW")]
    internal static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE mode);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int index);

    // ---- Device contexts and bitmaps ---------------------------------------------

    internal const int SRCCOPY = 0x00CC0020;
    internal const int CAPTUREBLT = 0x40000000;
    internal const int HALFTONE = 4;

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        internal int biSize;
        internal int biWidth;
        internal int biHeight;
        internal short biPlanes;
        internal short biBitCount;
        internal int biCompression;
        internal int biSizeImage;
        internal int biXPelsPerMeter;
        internal int biYPelsPerMeter;
        internal int biClrUsed;
        internal int biClrImportant;
    }

    [DllImport("user32.dll")]
    internal static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER header, int usage, out IntPtr bits, IntPtr section, int offset);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    internal static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, int rop);

    [DllImport("gdi32.dll")]
    internal static extern bool StretchBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, int rop);

    [DllImport("gdi32.dll")]
    internal static extern int SetStretchBltMode(IntPtr dc, int mode);

    [DllImport("gdi32.dll")]
    internal static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr previous);

    // ---- Cursor ------------------------------------------------------------------

    internal const int CURSOR_SHOWING = 1;
    internal const int DI_NORMAL = 3;

    [StructLayout(LayoutKind.Sequential)]
    internal struct CURSORINFO
    {
        internal int cbSize;
        internal int flags;
        internal IntPtr hCursor;
        internal POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ICONINFO
    {
        internal bool fIcon;
        internal uint xHotspot;
        internal uint yHotspot;
        internal IntPtr hbmMask;
        internal IntPtr hbmColor;
    }

    [DllImport("user32.dll")]
    internal static extern bool GetCursorInfo(ref CURSORINFO info);

    [DllImport("user32.dll")]
    internal static extern IntPtr CopyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    internal static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);

    [DllImport("user32.dll")]
    internal static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height, int step, IntPtr brush, int flags);

    [DllImport("user32.dll")]
    internal static extern bool DestroyIcon(IntPtr icon);

    // ---- Input injection ---------------------------------------------------------

    internal const int INPUT_MOUSE = 0;
    internal const int INPUT_KEYBOARD = 1;

    internal const uint MOUSEEVENTF_MOVE = 0x0001;
    internal const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    internal const uint MOUSEEVENTF_LEFTUP = 0x0004;
    internal const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    internal const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    internal const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    internal const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    internal const uint MOUSEEVENTF_XDOWN = 0x0080;
    internal const uint MOUSEEVENTF_XUP = 0x0100;
    internal const uint MOUSEEVENTF_WHEEL = 0x0800;
    internal const uint MOUSEEVENTF_HWHEEL = 0x1000;
    internal const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    internal const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    internal const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    internal const uint KEYEVENTF_KEYUP = 0x0002;
    internal const uint KEYEVENTF_UNICODE = 0x0004;
    internal const uint KEYEVENTF_SCANCODE = 0x0008;

    internal const uint XBUTTON1 = 0x0001;
    internal const uint XBUTTON2 = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        internal int dx;
        internal int dy;
        internal uint mouseData;
        internal uint dwFlags;
        internal uint time;
        internal IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        internal ushort wVk;
        internal ushort wScan;
        internal uint dwFlags;
        internal uint time;
        internal IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HARDWAREINPUT
    {
        internal uint uMsg;
        internal ushort wParamL;
        internal ushort wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct INPUTUNION
    {
        [FieldOffset(0)] internal MOUSEINPUT mi;
        [FieldOffset(0)] internal KEYBDINPUT ki;
        [FieldOffset(0)] internal HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        internal int type;
        internal INPUTUNION u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    internal static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    internal static extern short VkKeyScanW(char character);

    // ---- Session guards ----------------------------------------------------------

    /// <summary>
    /// Ignores the physical keyboard and mouse.
    /// </summary>
    /// <remarks>
    /// Windows only honours this from a process running at the same or higher integrity
    /// level as the foreground application, and releases it automatically if the calling
    /// thread stops responding, which is exactly the safety valve this needs.
    /// </remarks>
    [DllImport("user32.dll")]
    internal static extern bool BlockInput(bool block);

    [DllImport("user32.dll")]
    internal static extern bool LockWorkStation();

    [DllImport("kernel32.dll")]
    internal static extern uint SetThreadExecutionState(uint flags);

    internal const uint ES_CONTINUOUS = 0x80000000;
    internal const uint ES_SYSTEM_REQUIRED = 0x00000001;
    internal const uint ES_DISPLAY_REQUIRED = 0x00000002;
}
