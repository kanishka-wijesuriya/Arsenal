using Arsenal.Helpers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Arsenal.UI.Services.Remote.Desktop;

/// <summary>
/// Hides what a remote session is doing from anyone standing at the laptop.
/// </summary>
/// <remarks>
/// Two separate things, asked for together and often confused:
///
/// <list type="bullet">
/// <item>A black window over every display, so the panel shows nothing while the session
/// works. This is not a driver level blank - there is no supported way to switch a panel
/// off and keep composing to it - but it covers everything above the desktop, stays
/// topmost, and says on it why the screen is black so nobody assumes the machine has
/// crashed.</item>
/// <item>Ignoring the physical keyboard and mouse, so somebody at the machine cannot
/// fight the session for the pointer. Windows releases this on its own if the calling
/// thread stops responding, and Ctrl+Alt+Del always breaks it, which are the two escape
/// hatches that make it safe to offer at all.</item>
/// </list>
///
/// <para>Both are off unless the phone asks for them, and both are released when the
/// session ends however it ends.</para>
/// </remarks>
internal sealed class PrivacyGuard : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly List<Window> _covers = new();
    private bool _inputBlocked;
    private bool _disposed;

    internal PrivacyGuard(Dispatcher dispatcher) => _dispatcher = dispatcher;

    internal bool ScreenCovered { get; private set; }
    internal bool InputBlocked => _inputBlocked;

    internal void SetScreenCovered(bool covered)
    {
        if (_disposed || covered == ScreenCovered) return;
        ScreenCovered = covered;
        _dispatcher.BeginInvoke(() =>
        {
            if (covered) ShowCovers();
            else HideCovers();
        });
    }

    internal bool SetInputBlocked(bool blocked)
    {
        if (_disposed || blocked == _inputBlocked) return _inputBlocked;
        try
        {
            if (RemoteNative.BlockInput(blocked)) _inputBlocked = blocked;
            else Logger.WriteLine("Remote session could not " + (blocked ? "block" : "unblock") + " local input.");
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Remote block input: " + ex.Message);
        }
        return _inputBlocked;
    }

    private void ShowCovers()
    {
        HideCovers();
        foreach (RemoteMonitor monitor in RemoteMonitors.Enumerate())
        {
            // The virtual "all displays" entry is a capture target, not a real panel;
            // covering it would stack a second window over every physical one.
            if (monitor.Name == "All displays") continue;

            try
            {
                var window = new Window
                {
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    AllowsTransparency = false,
                    ShowInTaskbar = false,
                    Topmost = true,
                    Background = System.Windows.Media.Brushes.Black,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Content = new TextBlock
                    {
                        Text = "Screen hidden during a remote session",
                        Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x50, 0x50, 0x50)),
                        FontSize = 15,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                };

                // Device pixels: these windows have to line up with a monitor's physical
                // bounds, and WPF's own units are scaled per monitor. Positioned before
                // the window is shown, then corrected through the handle once it exists
                // so a display at anything but 100% is still covered edge to edge.
                window.Show();
                PositionOverMonitor(window, monitor);
                _covers.Add(window);
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Remote privacy cover: " + ex.Message);
            }
        }
    }

    private static void PositionOverMonitor(Window window, RemoteMonitor monitor)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        SetWindowPos(handle, HWND_TOPMOST, monitor.Left, monitor.Top, monitor.Width, monitor.Height, SWP_SHOWWINDOW | SWP_NOACTIVATE);
    }

    private void HideCovers()
    {
        foreach (Window cover in _covers)
        {
            try { cover.Close(); }
            catch (Exception ex) { Logger.WriteLine("Remote privacy cover close: " + ex.Message); }
        }
        _covers.Clear();
    }

    /// <summary>Locks the workstation, for the end of a session nobody is returning to.</summary>
    internal static void LockWorkstation()
    {
        try { RemoteNative.LockWorkStation(); }
        catch (Exception ex) { Logger.WriteLine("Remote lock: " + ex.Message); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SetInputBlocked(false);
        ScreenCovered = false;
        _dispatcher.BeginInvoke(HideCovers);
    }

    private const int HWND_TOPMOST = -1;
    private const int SWP_NOACTIVATE = 0x0010;
    private const int SWP_SHOWWINDOW = 0x0040;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, int insertAfter, int x, int y, int width, int height, int flags);
}
