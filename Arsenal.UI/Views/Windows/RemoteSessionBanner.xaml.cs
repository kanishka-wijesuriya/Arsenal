using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;

namespace Arsenal.UI.Views.Windows;

/// <summary>
/// Says, on the laptop itself, that somebody else is driving it.
/// </summary>
/// <remarks>
/// Not a toast. A toast fades, and the question this answers - "is this machine being
/// controlled right now" - is asked by whoever walks up to it, at whatever moment they
/// walk up to it. It also respects the person's chosen notification corner, which is
/// the wrong place for something that has to be noticed rather than read.
///
/// <para>The window never takes focus and never appears in Alt+Tab or the taskbar, so a
/// session that runs for an hour does not interrupt anything. It is not click-through:
/// the one control on it ends the session, which is the thing somebody standing at a
/// machine being driven from elsewhere most wants within reach.</para>
/// </remarks>
public partial class RemoteSessionBanner : Window
{
    private readonly Action _onEnd;

    public RemoteSessionBanner(Action onEnd)
    {
        _onEnd = onEnd;
        InitializeComponent();

        Loaded += (_, _) =>
        {
            ApplyNoActivate();
            PositionBottomCentre();
            Pulse();
        };
    }

    /// <summary>Updates the two lines without recreating the window.</summary>
    public void Describe(string headline, string detail)
    {
        HeadlineText.Text = headline;
        DetailText.Text = detail;

        // The card grows and shrinks with the text, so it has to be re-centred; a
        // session that gains "view only" would otherwise drift left of centre.
        Dispatcher.BeginInvoke(PositionBottomCentre, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void End_Click(object sender, RoutedEventArgs e) => _onEnd();

    /// <summary>
    /// Sits above the taskbar on whichever screen is primary.
    /// </summary>
    /// <remarks>
    /// Placed through the handle in device pixels rather than by setting Left and Top.
    /// Arsenal is per-monitor DPI aware, so WPF's own units belong to whichever monitor
    /// the window was created on, and on a 150% panel that put the card a third of the
    /// way off the bottom of the screen.
    /// </remarks>
    private void PositionBottomCentre()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        if (screen is null) return;

        var area = screen.WorkingArea;
        if (!GetWindowRect(handle, out RECT bounds)) return;

        int width = bounds.Right - bounds.Left;
        int height = bounds.Bottom - bounds.Top;
        int left = area.Left + ((area.Width - width) / 2);
        int top = area.Bottom - height;

        SetWindowPos(handle, HWND_TOPMOST, left, top, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>
    /// Keeps the banner from ever stealing the caret.
    /// </summary>
    /// <remarks>
    /// ShowActivated="False" covers the first show only. WS_EX_NOACTIVATE covers every
    /// later one, including the reposition after the text changes, which otherwise
    /// pulled focus out of whatever the remote session was typing into.
    /// </remarks>
    private void ApplyNoActivate()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        int style = GetWindowLong(handle, GWL_EXSTYLE);
        SetWindowLong(handle, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    private void Pulse()
    {
        var fade = new DoubleAnimation(1.0, 0.35, TimeSpan.FromSeconds(1.1))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        LiveDot.BeginAnimation(OpacityProperty, fade);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int HWND_TOPMOST = -1;
    private const int SWP_NOSIZE = 0x0001;
    private const int SWP_NOACTIVATE = 0x0010;
    private const int SWP_SHOWWINDOW = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out RECT bounds);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, int insertAfter, int x, int y, int width, int height, int flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);
}
