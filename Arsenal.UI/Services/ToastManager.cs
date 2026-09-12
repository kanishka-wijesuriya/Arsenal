using Arsenal.Helpers;
using Arsenal.UI.Views.Windows;
using System.Windows;

namespace Arsenal.UI.Services;

public static class ToastManager
{
    private static readonly List<ToastWindow> Active = new();
    private static readonly Dictionary<string, ToastWindow> Live = new(StringComparer.Ordinal);
    private const double Gap = 10;
    private const double Edge = 18;
    private static System.Windows.Forms.Screen? _stackScreen;
    private static Window? _topmostAnchor;
    private static readonly List<ToastWindow> PendingShow = new();
    private static bool _batchingLayout;
    private static bool _layoutPending;

    public static void Show(string message, ToastIcon icon = ToastIcon.Charger, string? detail = null)
    {
        if (!AppConfig.IsNotFalse("toast_enabled") || string.IsNullOrWhiteSpace(message)) return;

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            RunLayoutBatch(() =>
            {
                MakeRoom();
                var toast = new ToastWindow(message, icon, Remove, detail);
                Add(toast);
            });
        });
    }

    public static void ShowLive(string key, string message, ToastIcon icon, string detail, int percentage, int settleMs = 1000)
    {
        if (!AppConfig.IsNotFalse("toast_enabled") || string.IsNullOrWhiteSpace(key)) return;

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            if (Live.TryGetValue(key, out ToastWindow? existing) && !existing.IsClosing)
            {
                double previousHeight = existing.VisibleHeight;
                existing.UpdateLive(message, detail, percentage, settleMs);
                existing.UpdateLayout();
                // Brightness can report dozens of values per second. Its fixed-width
                // live row normally does not alter stack geometry, so avoid needlessly
                // restarting every toast's reflow animation on each slider sample.
                if (Math.Abs(existing.VisibleHeight - previousHeight) >= 0.5) Reposition();
                return;
            }

            RunLayoutBatch(() =>
            {
                MakeRoom();
                var toast = new ToastWindow(message, icon, Remove, detail);
                toast.UpdateLive(message, detail, percentage, settleMs);
                Live[key] = toast;
                Add(toast);
            });
        });
    }

    private static void Add(ToastWindow toast)
    {
        // One persistent topmost owner keeps the stack above normal applications.
        // Individual toasts stay non-topmost, so inserting another layered window does
        // not reshuffle the entire topmost band and briefly recompose existing cards.
        toast.Owner = EnsureTopmostAnchor();
        toast.ClosingStarted += OnClosingStarted;
        toast.PrepareForPlacement();
        Active.Add(toast);
        if (_batchingLayout)
        {
            PendingShow.Add(toast);
            RequestReposition();
        }
        else
        {
            Reposition();
            ShowPrepared([toast]);
        }
    }

    private static void MakeRoom()
    {
        if (Active.Count(toast => !toast.IsClosing) >= 3)
            Active.First(toast => !toast.IsClosing).BeginClose();
    }

    private static void OnClosingStarted(ToastWindow _) => RequestReposition();

    private static void Remove(ToastWindow toast)
    {
        toast.ClosingStarted -= OnClosingStarted;
        Active.Remove(toast);
        foreach (string key in Live.Where(pair => ReferenceEquals(pair.Value, toast)).Select(pair => pair.Key).ToArray())
            Live.Remove(key);
        if (Active.Count == 0)
        {
            _stackScreen = null;
            // Remove() runs just before the toast calls Close(). Defer disposal so the
            // owner cannot close its final child re-entrantly inside that callback.
            System.Windows.Application.Current.Dispatcher.BeginInvoke(CloseTopmostAnchor,
                System.Windows.Threading.DispatcherPriority.Background);
        }
        RequestReposition();
    }

    private static Window EnsureTopmostAnchor()
    {
        if (_topmostAnchor is { IsLoaded: true }) return _topmostAnchor;

        _topmostAnchor = new Window
        {
            Width = 1,
            Height = 1,
            Left = -32000,
            Top = -32000,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            Opacity = 0,
            ShowActivated = false,
            ShowInTaskbar = false,
            Topmost = true
        };
        _topmostAnchor.Show();
        return _topmostAnchor;
    }

    private static void CloseTopmostAnchor()
    {
        if (Active.Count != 0 || _topmostAnchor is null) return;
        _topmostAnchor.Close();
        _topmostAnchor = null;
    }

    private static void RunLayoutBatch(Action action)
    {
        _batchingLayout = true;
        try { action(); }
        finally
        {
            _batchingLayout = false;
            if (_layoutPending)
            {
                _layoutPending = false;
                Reposition();
            }

            ShowPending();
        }
    }

    private static void ShowPending()
    {
        ToastWindow[] pending = PendingShow
            .Where(toast => Active.Contains(toast) && !toast.IsClosing)
            .ToArray();
        PendingShow.Clear();
        ShowPrepared(pending);
    }

    private static void ShowPrepared(IEnumerable<ToastWindow> toasts)
    {
        ToastWindow[] pending = toasts.ToArray();
        if (pending.Length == 0) return;

        // Left and Top were assigned by Reposition before these topmost HWNDs exist.
        // Show them fully transparent, refresh the exact DPI-aware measurements once,
        // and only then reveal their cached cards. Existing toasts are never hidden or
        // re-created during this sequence.
        foreach (ToastWindow toast in pending)
        {
            toast.Show();
            toast.UpdateLayout();
        }

        Reposition();

        foreach (ToastWindow toast in pending)
        {
            if (Active.Contains(toast) && !toast.IsClosing) toast.Reveal();
        }
    }

    private static void RequestReposition()
    {
        if (_batchingLayout) _layoutPending = true;
        else Reposition();
    }

    private static void Reposition()
    {
        List<ToastWindow> visible = Active.Where(toast => !toast.IsClosing).ToList();
        if (visible.Count == 0) return;
        _stackScreen ??= System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
        var areaPx = _stackScreen.WorkingArea;
        double scale = visible[0].VisualTreeHelperDpiScale();
        double areaLeft = areaPx.Left / scale;
        double areaTop = areaPx.Top / scale;
        double areaRight = areaPx.Right / scale;
        double areaBottom = areaPx.Bottom / scale;
        int location = Math.Clamp(AppConfig.Get("toast_position", 0), 0, 3);
        bool bottom = location is 1 or 3;
        bool center = location >= 2;
        double cursorY = bottom ? areaBottom - Edge : areaTop + Edge;

        // Every toast window carries horizontal entrance slack and a larger vertical
        // reflow area. Positioning works in terms of the visible card, so each axis'
        // own slack is subtracted here.
        IEnumerable<ToastWindow> ordered = bottom ? visible.AsEnumerable().Reverse() : visible;
        foreach (var toast in ordered)
        {
            toast.UpdateLayout();
            double cardWidth = toast.VisibleWidth;
            double cardHeight = toast.VisibleHeight;

            double cardLeft = center
                ? areaLeft + ((areaRight - areaLeft - cardWidth) / 2)
                : areaRight - cardWidth - Edge;
            double cardTop = bottom ? cursorY - cardHeight : cursorY;

            toast.SetPosition(cardLeft - ToastWindow.Slack, cardTop - ToastWindow.VerticalSlack);
            cursorY += bottom ? -(cardHeight + Gap) : cardHeight + Gap;
        }
    }

    private static double VisualTreeHelperDpiScale(this Window window)
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
        return dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
    }
}
