using Arsenal.Helpers;
using System.Runtime.InteropServices;

namespace Arsenal.UI.Services.Remote.Desktop;

/// <summary>A captured surface, in 32-bit BGRA, top row first.</summary>
/// <remarks>
/// The buffer belongs to the source and is reused between frames. A consumer that needs
/// to keep it past the next <see cref="IScreenSource.TryCapture"/> copies it.
/// </remarks>
internal sealed class CapturedFrame
{
    internal byte[] Pixels = Array.Empty<byte>();
    internal int Width;
    internal int Height;
    internal int Stride;
    internal long TimestampUs;
    /// <summary>Where the pointer is, in captured surface pixels, or -1 when off it.</summary>
    internal int CursorX = -1;
    internal int CursorY = -1;
}

internal interface IScreenSource : IDisposable
{
    int Width { get; }
    int Height { get; }
    string Name { get; }

    /// <summary>
    /// Fills <paramref name="frame"/> with the current surface.
    /// </summary>
    /// <returns>False when the surface could not be read this tick, which is normal
    /// across a resolution change, a session switch or a secure desktop.</returns>
    bool TryCapture(CapturedFrame frame);
}

/// <summary>One physical display, as the capture path sees it.</summary>
internal sealed record RemoteMonitor(int Index, string Name, int Left, int Top, int Width, int Height, bool Primary, int RefreshHz)
{
    internal RemoteMonitorInfo ToInfo() => new(Index, Name, Width, Height, Left, Top, Primary, RefreshHz);
}

internal static class RemoteMonitors
{
    /// <summary>
    /// Every attached display, plus a virtual entry covering all of them.
    /// </summary>
    /// <remarks>
    /// Physical pixels throughout. Arsenal is per-monitor DPI aware, so a coordinate read
    /// through the Win32 monitor APIs is already the coordinate the capture and the input
    /// injection both use; anything read through a scaled WPF or WinForms surface would
    /// not be, and a 150% display would place every click a third of the way off.
    /// </remarks>
    internal static IReadOnlyList<RemoteMonitor> Enumerate()
    {
        var monitors = new List<RemoteMonitor>();
        try
        {
            var collected = new List<(RemoteNative.RECT Bounds, string Device, bool Primary)>();
            RemoteNative.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref RemoteNative.RECT _, IntPtr _) =>
            {
                var info = new RemoteNative.MONITORINFOEX { cbSize = Marshal.SizeOf<RemoteNative.MONITORINFOEX>() };
                if (RemoteNative.GetMonitorInfo(monitor, ref info))
                {
                    collected.Add((info.rcMonitor, info.szDevice, (info.dwFlags & RemoteNative.MONITORINFOF_PRIMARY) != 0));
                }
                return true;
            }, IntPtr.Zero);

            // Primary first, then left to right: the order somebody would point at them.
            foreach (var entry in collected.OrderByDescending(entry => entry.Primary).ThenBy(entry => entry.Bounds.Left))
            {
                monitors.Add(new RemoteMonitor(
                    monitors.Count, FriendlyName(entry.Device, entry.Primary),
                    entry.Bounds.Left, entry.Bounds.Top,
                    entry.Bounds.Right - entry.Bounds.Left, entry.Bounds.Bottom - entry.Bounds.Top,
                    entry.Primary, RefreshRate(entry.Device)));
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Remote monitors: " + ex.Message);
        }

        if (monitors.Count == 0)
        {
            // No monitor answered. The virtual desktop metrics still describe something
            // capturable, and a session with one wrong-sized screen beats no session.
            monitors.Add(new RemoteMonitor(0, "Display",
                RemoteNative.GetSystemMetrics(RemoteNative.SM_XVIRTUALSCREEN),
                RemoteNative.GetSystemMetrics(RemoteNative.SM_YVIRTUALSCREEN),
                Math.Max(640, RemoteNative.GetSystemMetrics(RemoteNative.SM_CXVIRTUALSCREEN)),
                Math.Max(480, RemoteNative.GetSystemMetrics(RemoteNative.SM_CYVIRTUALSCREEN)),
                true, 60));
        }
        else if (monitors.Count > 1)
        {
            int left = monitors.Min(monitor => monitor.Left);
            int top = monitors.Min(monitor => monitor.Top);
            int right = monitors.Max(monitor => monitor.Left + monitor.Width);
            int bottom = monitors.Max(monitor => monitor.Top + monitor.Height);
            monitors.Add(new RemoteMonitor(monitors.Count, "All displays", left, top, right - left, bottom - top, false,
                monitors.Max(monitor => monitor.RefreshHz)));
        }

        return monitors;
    }

    private static string FriendlyName(string device, bool primary)
    {
        // The device path is not a name anybody recognises on a phone.
        string trimmed = device.TrimStart('\\', '.');
        string number = new(trimmed.Where(char.IsDigit).ToArray());
        string label = number.Length > 0 ? "Display " + number : trimmed;
        return primary ? label + " (main)" : label;
    }

    private static int RefreshRate(string device)
    {
        try
        {
            var mode = new RemoteNative.DEVMODE { dmSize = (short)Marshal.SizeOf<RemoteNative.DEVMODE>() };
            return RemoteNative.EnumDisplaySettings(device, RemoteNative.ENUM_CURRENT_SETTINGS, ref mode)
                ? Math.Max(1, mode.dmDisplayFrequency)
                : 60;
        }
        catch { return 60; }
    }
}

/// <summary>
/// Reads the desktop with BitBlt into a device independent bitmap.
/// </summary>
/// <remarks>
/// This is the path that always works. It reads through the desktop window's DC, so it
/// sees whatever is composited there including layered windows, it needs no graphics
/// device of its own, and it survives a GPU mode switch, an external monitor being
/// unplugged and a driver restart without the session dropping.
///
/// <para>What it does not see is a full screen exclusive game, and what it costs is a
/// copy out of video memory every frame.</para>
///
/// <para>Downscaling happens here rather than in the encoder because StretchBlt does it
/// on the way out of the driver, which is both faster than resampling the copy and one
/// less full sized buffer to hold.</para>
/// </remarks>
internal sealed class GdiScreenSource : IScreenSource
{
    private readonly RemoteMonitor _monitor;
    private readonly bool _drawCursor;
    private IntPtr _screenDc;
    private IntPtr _memoryDc;
    private IntPtr _bitmap;
    private IntPtr _previousBitmap;
    private IntPtr _bits;
    private bool _disposed;

    internal GdiScreenSource(RemoteMonitor monitor, int maxWidth, int maxHeight, bool drawCursor)
    {
        _monitor = monitor;
        _drawCursor = drawCursor;

        // Even dimensions throughout. Every hardware video encoder wants them, chroma is
        // subsampled two pixels at a time, and an odd width costs a re-crop later.
        double scale = Math.Min(1d, Math.Min(
            maxWidth <= 0 ? 1d : (double)maxWidth / monitor.Width,
            maxHeight <= 0 ? 1d : (double)maxHeight / monitor.Height));
        Width = Even(Math.Max(2, (int)Math.Round(monitor.Width * scale)));
        Height = Even(Math.Max(2, (int)Math.Round(monitor.Height * scale)));

        _screenDc = RemoteNative.GetDC(IntPtr.Zero);
        _memoryDc = RemoteNative.CreateCompatibleDC(_screenDc);

        var header = new RemoteNative.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<RemoteNative.BITMAPINFOHEADER>(),
            biWidth = Width,
            // Negative: a top-down bitmap, so row zero is the top of the screen and the
            // buffer can be handed to an encoder without being flipped first.
            biHeight = -Height,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,
        };
        _bitmap = RemoteNative.CreateDIBSection(_memoryDc, ref header, 0, out _bits, IntPtr.Zero, 0);
        if (_bitmap == IntPtr.Zero) throw new InvalidOperationException("Could not allocate a capture surface.");
        _previousBitmap = RemoteNative.SelectObject(_memoryDc, _bitmap);

        // HALFTONE gives StretchBlt a real filter rather than dropping pixels, which is
        // the difference between readable and unreadable text at half scale.
        RemoteNative.SetStretchBltMode(_memoryDc, RemoteNative.HALFTONE);
        RemoteNative.SetBrushOrgEx(_memoryDc, 0, 0, IntPtr.Zero);
    }

    public int Width { get; }
    public int Height { get; }
    public string Name => "Desktop copy";

    public bool TryCapture(CapturedFrame frame)
    {
        if (_disposed) return false;

        bool scaled = Width != _monitor.Width || Height != _monitor.Height;
        bool copied = scaled
            ? RemoteNative.StretchBlt(_memoryDc, 0, 0, Width, Height, _screenDc, _monitor.Left, _monitor.Top, _monitor.Width, _monitor.Height, RemoteNative.SRCCOPY | RemoteNative.CAPTUREBLT)
            : RemoteNative.BitBlt(_memoryDc, 0, 0, Width, Height, _screenDc, _monitor.Left, _monitor.Top, RemoteNative.SRCCOPY | RemoteNative.CAPTUREBLT);
        if (!copied) return false;

        frame.CursorX = -1;
        frame.CursorY = -1;
        if (_drawCursor) DrawCursor(frame, scaled);

        int stride = Width * 4;
        int required = stride * Height;
        if (frame.Pixels.Length < required) frame.Pixels = new byte[required];
        Marshal.Copy(_bits, frame.Pixels, 0, required);

        frame.Width = Width;
        frame.Height = Height;
        frame.Stride = stride;
        frame.TimestampUs = RemoteClock.NowMicroseconds();
        return true;
    }

    /// <summary>
    /// Composites the pointer into the capture.
    /// </summary>
    /// <remarks>
    /// BitBlt never includes it: the cursor is drawn by the compositor over the top of
    /// everything, not into any window's contents. Drawing it here rather than sending
    /// its shape separately keeps the phone simple and keeps the pointer exactly where
    /// the frame says it is, which matters more than a locally drawn pointer moving
    /// sooner while something is being dragged.
    /// </remarks>
    private void DrawCursor(CapturedFrame frame, bool scaled)
    {
        var info = new RemoteNative.CURSORINFO { cbSize = Marshal.SizeOf<RemoteNative.CURSORINFO>() };
        if (!RemoteNative.GetCursorInfo(ref info) || info.flags != RemoteNative.CURSOR_SHOWING || info.hCursor == IntPtr.Zero) return;

        IntPtr cursor = RemoteNative.CopyIcon(info.hCursor);
        if (cursor == IntPtr.Zero) return;
        try
        {
            if (!RemoteNative.GetIconInfo(cursor, out RemoteNative.ICONINFO icon)) return;
            try
            {
                double scaleX = (double)Width / _monitor.Width;
                double scaleY = (double)Height / _monitor.Height;
                int hotX = (int)Math.Round((info.ptScreenPos.x - _monitor.Left) * scaleX);
                int hotY = (int)Math.Round((info.ptScreenPos.y - _monitor.Top) * scaleY);
                if (hotX < 0 || hotY < 0 || hotX >= Width || hotY >= Height) return;

                frame.CursorX = hotX;
                frame.CursorY = hotY;

                int x = (int)Math.Round((info.ptScreenPos.x - _monitor.Left - (int)icon.xHotspot) * scaleX);
                int y = (int)Math.Round((info.ptScreenPos.y - _monitor.Top - (int)icon.yHotspot) * scaleY);

                // DrawIconEx scales only when told both dimensions. At 1:1 pass zero and
                // let it use the cursor's own size, which avoids resampling the common case.
                int drawWidth = scaled ? Math.Max(1, (int)Math.Round(RemoteNative.GetSystemMetrics(RemoteNative.SM_CXCURSOR) * scaleX)) : 0;
                int drawHeight = scaled ? Math.Max(1, (int)Math.Round(RemoteNative.GetSystemMetrics(RemoteNative.SM_CYCURSOR) * scaleY)) : 0;
                RemoteNative.DrawIconEx(_memoryDc, x, y, cursor, drawWidth, drawHeight, 0, IntPtr.Zero, RemoteNative.DI_NORMAL);
            }
            finally
            {
                if (icon.hbmColor != IntPtr.Zero) RemoteNative.DeleteObject(icon.hbmColor);
                if (icon.hbmMask != IntPtr.Zero) RemoteNative.DeleteObject(icon.hbmMask);
            }
        }
        finally
        {
            RemoteNative.DestroyIcon(cursor);
        }
    }

    private static int Even(int value) => value % 2 == 0 ? value : value - 1;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_memoryDc != IntPtr.Zero && _previousBitmap != IntPtr.Zero) RemoteNative.SelectObject(_memoryDc, _previousBitmap);
        if (_bitmap != IntPtr.Zero) RemoteNative.DeleteObject(_bitmap);
        if (_memoryDc != IntPtr.Zero) RemoteNative.DeleteDC(_memoryDc);
        if (_screenDc != IntPtr.Zero) RemoteNative.ReleaseDC(IntPtr.Zero, _screenDc);
        _bitmap = _memoryDc = _screenDc = _previousBitmap = _bits = IntPtr.Zero;
    }
}

/// <summary>
/// One monotonic clock for the whole session.
/// </summary>
/// <remarks>
/// Video and audio timestamps have to come from the same origin or the phone cannot line
/// them up, and neither can be wall clock time: a clock correction mid session would move
/// one stream relative to the other.
/// </remarks>
internal static class RemoteClock
{
    private static readonly long Start = System.Diagnostics.Stopwatch.GetTimestamp();

    internal static long NowMicroseconds() =>
        (System.Diagnostics.Stopwatch.GetTimestamp() - Start) * 1_000_000L / System.Diagnostics.Stopwatch.Frequency;
}
