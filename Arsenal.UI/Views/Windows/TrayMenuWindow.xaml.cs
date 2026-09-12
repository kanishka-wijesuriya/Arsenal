using Arsenal.UI.ViewModels;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;

namespace Arsenal.UI.Views.Windows;

public partial class TrayMenuWindow : Window
{
    private bool _targetVisible;

    /// <summary>
    /// Where the menu is headed, not where it is. The window stays visible for the whole
    /// exit animation, so a toggle that asked <see cref="Window.IsVisible"/> during the
    /// close could not tell "open" from "closing" and refused to reopen.
    /// </summary>
    public bool IsOpenOrOpening => IsVisible && _targetVisible;

    public TrayMenuWindow(QuickPanelViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        if (Environment.GetCommandLineArgs().Any(arg => arg.Equals("--tray-test", StringComparison.OrdinalIgnoreCase)))
        {
            ShowInTaskbar = true;
            Topmost = false;
        }
        Deactivated += (_, _) => HideAnimated();

        // Several menu items call Hide() directly rather than animating out. Syncing here
        // covers every route out at once, so the flag can never be left asserting that a
        // hidden menu is still open.
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) return;
            _targetVisible = false;
            Services.BackgroundMemoryRelease.Schedule();
        };
    }

    public void ShowAnimated()
    {
        // Deliberately not blocked while a close animation runs. Bailing out then is
        // what made the menu dead to a second click for the whole of its exit; reopening
        // simply takes over the transform from wherever the close had got to.
        if (_targetVisible && IsVisible) return;
        _targetVisible = true;
        _menuFade.Stop();
        _menuSlide.Stop();
        Opacity = 0;
        MenuTransform.Y = 0;
        Show();

        // The menu sizes itself to its rows, and rows the hardware does not support are
        // collapsed, so the height is only known once it has laid out. Showing it fully
        // transparent first lets us measure, then place it against the tray corner.
        UpdateLayout();
        PositionNearTray();

        // Set after placement, because the placement is what decides which side of the
        // pointer the menu ended up on - and the menu has to rise towards the pointer
        // from whichever side that is.
        MenuTransform.Y = _enterY;
        Activate();
        _menuFade.Start(0, 1, 180, Arsenal.UI.Controls.FrameEase.QuinticOut, v => Opacity = v);
        _menuSlide.Start(_enterY, 0, 180, Arsenal.UI.Controls.FrameEase.QuinticOut, y => MenuTransform.Y = y);
    }

    /// <summary>
    /// Where the menu starts, in DIPs. Positive means it comes up from below the
    /// pointer, which is what a taskbar along the bottom calls for; a taskbar along the
    /// top flips the menu underneath the pointer and this flips with it.
    /// </summary>
    private double _enterY = EnterDistance;

    private const double EnterDistance = 10;
    private const double ExitDistance = 28;

    public void HideAnimated()
    {
        if (!IsVisible || !_targetVisible) return;
        _targetVisible = false;

        // Freeze the animated values before clearing, so a close that interrupts the
        // opening animation starts from what is on screen instead of snapping first.
        double currentOpacity = Opacity;
        double currentY = MenuTransform.Y;
        _menuFade.Stop();
        _menuSlide.Stop();
        Opacity = currentOpacity;
        MenuTransform.Y = currentY;

        _menuFade.Start(currentOpacity, 0, 130, Arsenal.UI.Controls.FrameEase.CubicIn, v => Opacity = v);
        _menuSlide.Start(
            currentY,
            Math.Sign(_enterY) * ExitDistance,
            160,
            Arsenal.UI.Controls.FrameEase.CubicIn,
            y => MenuTransform.Y = y,
            completed: () =>
            {
                // Reopened mid-close: ShowAnimated already owns the window and its eases.
                if (_targetVisible) return;
                Hide();
                Opacity = 1;
                MenuTransform.Y = 0;
            });
    }

    private readonly Arsenal.UI.Controls.FrameEase _menuFade = new();
    private readonly Arsenal.UI.Controls.FrameEase _menuSlide = new();

    private void PositionNearTray()
    {
        var point = System.Windows.Forms.Cursor.Position;
        var area = System.Windows.Forms.Screen.FromPoint(point).WorkingArea;
        uint dpi = GetDpiForWindow(new WindowInteropHelper(this).EnsureHandle());
        double scale = dpi > 0 ? dpi / 96d : 1d;
        double height = ActualHeight > 0 ? ActualHeight : Height;
        double width = ActualWidth > 0 ? ActualWidth : Width;
        double areaLeft = area.Left / scale;
        double areaTop = area.Top / scale;
        double areaRight = area.Right / scale;
        double areaBottom = area.Bottom / scale;
        double cursorX = point.X / scale;
        double cursorY = point.Y / scale;

        // Context menus belong to the pointer that opened them. Tray clicks arrive
        // from the taskbar, so anchor the menu's lower edge just above that point and
        // keep its right edge aligned with the tray icon instead of pinning it to the
        // monitor's generic bottom-right corner.
        double anchorY = Math.Min(cursorY, areaBottom);
        double left = cursorX - width + 8;
        double top = anchorY - height - 8;

        // Flip to the other side when the pointer is too close to an edge, then clamp
        // the final bounds so the complete menu remains visible on that monitor.
        if (left < areaLeft + 8) left = cursorX + 8;
        if (left + width > areaRight - 8) left = areaRight - width - 8;

        bool below = top < areaTop + 8;
        if (below) top = anchorY + 8;
        if (top + height > areaBottom - 8) top = areaBottom - height - 8;

        // A taskbar along the top puts the pointer near the top of the work area, which
        // is what flips the menu below it. Opening downwards while sliding upwards reads
        // as the menu being dragged away from the icon that opened it.
        _enterY = below ? -EnterDistance : EnterDistance;

        Left = left;
        Top = top;
    }

    private void QuickPanel_Click(object sender, RoutedEventArgs e) { Hide(); (System.Windows.Application.Current as App)?.ToggleQuickPanel(); }
    private void OpenApp_Click(object sender, RoutedEventArgs e) { Hide(); (System.Windows.Application.Current as App)?.ShowMainWindow(); }
    private void Settings_Click(object sender, RoutedEventArgs e) { Hide(); (System.Windows.Application.Current as App)?.ShowMainWindow(); if (System.Windows.Application.Current.MainWindow is MainWindow main) main.NavigateToTag("Settings"); }
    private void MenuSelection_Click(object sender, RoutedEventArgs e) => HideAnimated();
    private void Overlay_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is QuickPanelViewModel vm) vm.ToggleHardwareOverlay();
        HideAnimated();
    }
    private void Quit_Click(object sender, RoutedEventArgs e) => (System.Windows.Application.Current as App)?.ExitApplication();

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
