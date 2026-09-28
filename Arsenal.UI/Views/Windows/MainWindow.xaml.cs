using Arsenal.Application.Models;
using Arsenal.AutoUpdate;
using Arsenal.Helpers;
using Arsenal.UI.ViewModels;
using Arsenal.UI.Views.Overlays;
using Arsenal.UI.Views.Pages;
using Microsoft.Extensions.DependencyInjection;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using InputModifierKeys = System.Windows.Input.ModifierKeys;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Arsenal.UI.Views.Windows
{
    public partial class MainWindow : FluentWindow
    {
        private readonly MainViewModel _viewModel;
        private readonly MainWindowState _windowState;
        private readonly DispatcherTimer _placementSaveTimer;

        private bool _allowClose;
        private int _visibilityLogToken;

        public MainWindow(MainViewModel viewModel, MainWindowState windowState)
        {
            InitializeComponent();
            _viewModel = viewModel;
            _windowState = windowState;
            DataContext = _viewModel;

            // The theme is applied before any window exists, so this window takes the
            // current solid surface colours as soon as it is constructed.
            App.ApplyWindowSurface(this);

            _placementSaveTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(400)
            };
            _placementSaveTimer.Tick += (_, _) =>
            {
                _placementSaveTimer.Stop();
                SaveWindowPlacementNow();
            };

            Loaded += (_, _) =>
            {
                RestoreWindowPlacement();
                TrackNavigationPaneWidth();
                ApplyResponsivePane();

                // A window left open is a working set held for as long as it is open.
                // Started here rather than at launch because with nothing on screen the
                // quiet release already does more than this would.
                Services.BackgroundMemoryRelease.WatchOpenWindows();
            };
            LocationChanged += (_, _) => ScheduleWindowPlacementSave();
            SizeChanged += (_, e) =>
            {
                ScheduleWindowPlacementSave();
                if (e.WidthChanged) ApplyResponsivePane();
            };
            StateChanged += (_, _) => ScheduleWindowPlacementSave();

            // Home is the view-model default. A Main Window recreated after a long tray
            // idle instead returns to the page that was active before its native window
            // was released.
            NavigateToTag(string.IsNullOrEmpty(_viewModel.ActivePageTag) ? "Home" : _viewModel.ActivePageTag);

            // This is a tray application: the window spends most of its life hidden,
            // and a hidden window still holds every element, brush and cached render
            // resource from visited pages. Keep the visible-session cache for instant
            // navigation, then drop it when the full window returns to the tray.
            IsVisibleChanged += (_, e) =>
            {
                if ((bool)e.NewValue)
                {
                    Services.BackgroundMemoryRelease.NotifyActivity();
                    RestorePageContent();
                }
                else ReleasePageContent();

                // The window being up is the state anybody looking at Task Manager is
                // looking at, so it is the one worth a line. Debounced so rapid open
                // and close toggles do not spawn repeated virtual memory sweeps.
                if (MemoryHelper.DetailedReportingEnabled)
                {
                    int logToken = Interlocked.Increment(ref _visibilityLogToken);
                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                        if (Volatile.Read(ref _visibilityLogToken) != logToken) return;
                        Arsenal.Helpers.MemoryHelper.LogReport((bool)e.NewValue ? "window shown" : "window hidden");
                    });
                }
            };

            // Keybindings (Ctrl+K for Command Palette)
            KeyDown += (s, e) =>
            {
                if (e.Key == Key.K && Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control))
                {
                    ShowCommandPalette();
                    e.Handled = true;
                }
            };

            // The custom title bar can consume button/key input before bubbling reaches
            // the window. Listen to handled preview events as a reliable fallback.
            AddHandler(Keyboard.PreviewKeyDownEvent, new System.Windows.Input.KeyEventHandler((s, e) =>
            {
                if (e.Key == Key.K && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                {
                    ShowCommandPalette();
                    e.Handled = true;
                }
            }), true);
            AddHandler(Keyboard.PreviewKeyDownEvent, new System.Windows.Input.KeyEventHandler(OnPreviewKeyDownForOverlays), true);

            // The two side buttons on a mouse. Registered the same way and for the same
            // reason as the keys above: a preview handler on the window, taking handled
            // events too, because the title bar and the pages both sit on controls that
            // consume mouse input before it reaches here.
            AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnPreviewMouseNavigate), true);
            AddHandler(Keyboard.PreviewKeyDownEvent, new System.Windows.Input.KeyEventHandler(OnPreviewKeyDownForNavigation), true);

            SearchOverlay.Closed += ReleaseSearchPanel;
            SetupOverlay.Closed += ReleaseSetupPanel;
            UpdateOverlay.Closed += ReleaseUpdatePanel;

            // Coming back to the window should not look like the user had been tabbing
            // through it. Deferred to Input priority because focus restoration is still in
            // flight while Activated is being raised, and reading the focused element any
            // earlier names the element that is about to lose it.
            Activated += (_, _) => Dispatcher.BeginInvoke(
                new Action(() => _focusRing.SuppressForActivation(Keyboard.FocusedElement)),
                DispatcherPriority.Input);
            PreviewKeyDown += RestoreFocusRingOnNavigationKey;

            // The bar is the only thing that names the open subpage now, so it has to
            // hear about one opening from anywhere on any page. Dropped again on close
            // because the event is static and would otherwise hold this window for the
            // life of the process.
            Controls.SettingsGroup.OpenGroupChanged += OnOpenGroupChanged;
            Closed += (_, _) => Controls.SettingsGroup.OpenGroupChanged -= OnOpenGroupChanged;
        }

        private readonly Controls.FocusRingSuppressor _focusRing = new();

        private void RestoreFocusRingOnNavigationKey(object sender, KeyEventArgs e)
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (Controls.FocusRingSuppressor.IsNavigationKey(key)) _focusRing.RestoreForNavigation();
        }

        private void NavigationView_SelectionChanged(NavigationView sender, RoutedEventArgs args)
        {
            if (sender.SelectedItem is NavigationViewItem selectedItem)
            {
                string tag = selectedItem.TargetPageTag ?? "Home";
                NavigateToTag(tag);
            }
        }

        private void NavigationItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is NavigationViewItem item)
                NavigateToTag(item.TargetPageTag ?? "Home");
        }

        private void RootNavigationView_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var item = FindNavigationItem(e.OriginalSource as DependencyObject);
            if (item != null) NavigateToTag(item.TargetPageTag ?? "Home");
        }

        private void RootNavigationView_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key is not (Key.Enter or Key.Space)) return;
            var item = FindNavigationItem(Keyboard.FocusedElement as DependencyObject);
            if (item == null) return;
            NavigateToTag(item.TargetPageTag ?? "Home");
            e.Handled = true;
        }

        private static NavigationViewItem? FindNavigationItem(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is NavigationViewItem item) return item;
                source = VisualTreeHelper.GetParent(source);
            }
            return null;
        }

        private FrameworkElement? _navigationPane;
        private FrameworkElement? _navigationContent;

        /// <summary>
        /// Finds the pane parts and keeps the window grounds tied to them.
        /// </summary>
        /// <remarks>
        /// The pane's width animates when it opens and closes - 220, 205, 175, 137, 109,
        /// 73, 46 over the transition - so a width derived from IsPaneOpen would snap the
        /// divider to its destination while the pane was still travelling. It follows the
        /// live layout instead.
        ///
        /// Width alone is not the seam, though. PaneGrid carries a 4px left margin, so at
        /// 220 wide its right edge is at 224 and a divider placed at 220 draws *inside*
        /// the pane, through the navigation items. The content presenter then starts at
        /// 229, leaving a 5px gap that would show as a stripe of bare window between the
        /// two grounds. So the seam is taken from where the
        /// content actually begins, and the sidebar ground runs all the way to it.
        /// </remarks>
        private void TrackNavigationPaneWidth()
        {
            _navigationPane = FindDescendant<FrameworkElement>(RootNavigationView, "PaneGrid");
            _navigationContent = FindDescendant<FrameworkElement>(
                RootNavigationView, "PART_NavigationViewContentPresenter");

            if (_navigationPane is null)
            {
                Logger.WriteLine("Navigation pane part 'PaneGrid' not found; sidebar ground will not track the pane.");
                return;
            }

            // Fires on every frame of the open/close animation, because the pane is being
            // resized by it.
            _navigationPane.SizeChanged += (_, _) => UpdateWindowSurfaces();
            UpdateWindowSurfaces();
        }

        /// <summary>Height of the title bar strip, matching the window's first row.</summary>
        private const double TitleBarHeight = 48d;

        /// <summary>How far the content ground's top-left corner rounds once tucked under
        /// the title bar.</summary>
        private const double ContentCornerRadius = 8d;

        /// <summary>
        /// Lays the content ground out against the pane, and tucks it under the title bar
        /// as the pane collapses.
        /// </summary>
        /// <remarks>
        /// Fully open, the content ground is a plain rectangle filling the right of the
        /// window from the very top. As the pane closes it slides down below the title bar
        /// and rounds its top-left corner, which leaves the navigation ground running
        /// across the whole strip - so the toggle, history and search icons sit on the
        /// navigation colour instead of hanging over the content.
        ///
        /// The morph is driven from the pane's own animated width rather than from a
        /// separate clock, so it is exactly as fluid as the pane and can never fall out of
        /// step with it. The pane bottoms out 8px under CompactPaneLength because its grid
        /// carries a 4px margin on each side; if that template detail ever changes the
        /// morph simply finishes slightly early or late.
        /// </remarks>
        private void UpdateWindowSurfaces()
        {
            if (_navigationPane is null || !_navigationPane.IsVisible) return;

            try
            {
                double seam = _navigationContent is { IsVisible: true }
                    ? _navigationContent.TransformToAncestor(RootNavigationView).Transform(default).X
                    : _navigationPane.TransformToAncestor(RootNavigationView)
                        .Transform(new System.Windows.Point(_navigationPane.ActualWidth, 0)).X;

                if (seam <= 0 || double.IsNaN(seam) || double.IsInfinity(seam)) return;

                // Before the early-out below, which only guards the grounds: the path in
                // the bar is laid out against this same seam and has to keep up with it.
                PlaceBreadcrumb(seam);

                double open = RootNavigationView.OpenPaneLength;
                double compact = Math.Max(0, RootNavigationView.CompactPaneLength - 8);
                double travel = Math.Max(1, open - compact);
                double collapsed = 1 - Math.Clamp((_navigationPane.ActualWidth - compact) / travel, 0, 1);

                var margin = new Thickness(seam, collapsed * TitleBarHeight, 0, 0);
                var corner = new CornerRadius(collapsed * ContentCornerRadius, 0, 0, 0);

                // The left divider is always there. The top one is the underside of the
                // title bar, which only exists once the content has tucked beneath it, so
                // it arrives with the collapse and carries on round the corner into the
                // left edge. Fully open there is no title bar to underline and its
                // thickness is zero.
                var border = new Thickness(1, collapsed, 0, 0);

                // This runs inside a layout pass, so writing values back unchanged would
                // invalidate layout again for nothing.
                if (Math.Abs(ContentSurface.Margin.Left - margin.Left) < 0.5 &&
                    Math.Abs(ContentSurface.Margin.Top - margin.Top) < 0.5)
                    return;

                ContentSurface.Margin = margin;
                ContentSurface.CornerRadius = corner;
                ContentSurface.BorderThickness = border;
            }
            catch (InvalidOperationException)
            {
                // The pane is not connected to the same visual root yet; the next resize
                // brings us back here with a usable tree.
            }
        }

        private static T? FindDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is T typed && typed.Name == name) return typed;
                if (FindDescendant<T>(child, name) is { } found) return found;
            }
            return null;
        }

        private void SearchButton_Click(object sender, RoutedEventArgs e) => ShowCommandPalette();

        private void PaneToggleButton_Click(object sender, RoutedEventArgs e)
        {
            RootNavigationView.IsPaneOpen = !RootNavigationView.IsPaneOpen;

            // The last thing the user asked for by hand is what the pane goes back to
            // when the window is widened again.
            _responsivePane.UserSetPaneOpen(RootNavigationView.IsPaneOpen);
        }

        private readonly Controls.ResponsivePaneState _responsivePane = new();

        /// <summary>
        /// Folds the navigation column away as the window approaches its minimum width,
        /// and brings it back when there is room again.
        /// </summary>
        /// <remarks>
        /// Nothing is animated here. <c>IsPaneOpen</c> is what the NavigationView
        /// animates on its own, and the window grounds already follow the pane's live
        /// width frame by frame through <see cref="UpdateWindowSurfaces"/> - so setting
        /// the flag produces exactly the motion the toggle button produces, which is
        /// what makes an automatic collapse indistinguishable from a manual one.
        /// </remarks>
        private void ApplyResponsivePane()
        {
            if (!IsLoaded) return;

            bool? open = _responsivePane.Evaluate(ActualWidth, MinWidth, RootNavigationView.IsPaneOpen);
            if (open is not null) RootNavigationView.IsPaneOpen = open.Value;
        }

        /// <summary>
        /// Pages are swapped into the content overlay by hand rather than driven through
        /// a frame, so the pane's own selection tracking never engages and nothing shows
        /// where you are. Marking the matching item active drives the accent indicator.
        /// </summary>
        private void MarkActiveNavigationItem(string tag)
        {
            foreach (NavigationViewItem item in RootNavigationView.MenuItems.OfType<NavigationViewItem>()
                        .Concat(RootNavigationView.FooterMenuItems.OfType<NavigationViewItem>()))
            {
                item.IsActive = string.Equals(item.TargetPageTag, tag, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Re-applies the backdrop once the window has a handle.
        /// </summary>
        /// <remarks>
        /// The composition target does not exist in the constructor, and its clear colour
        /// is what a resize uncovers along the edge being dragged. This is the first
        /// moment it can be set.
        /// </remarks>
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            App.ApplyWindowSurface(this);
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            _placementSaveTimer.Stop();
            SaveWindowPlacementNow();
            if (!_allowClose && AppConfig.IsNotFalse("minimize_to_tray"))
            {
                e.Cancel = true;
                Hide();
            }
            else
            {
                base.OnClosing(e);
                if (!_allowClose)
                    Dispatcher.BeginInvoke(new Action(() => System.Windows.Application.Current.Shutdown()));
            }
        }

        public void AllowClose() => _allowClose = true;

        private void RestoreWindowPlacement()
        {
            Width = Math.Max(MinWidth, AppConfig.Get("window_width", 1180));
            Height = Math.Max(MinHeight, AppConfig.Get("window_height", 780));
            int left = AppConfig.Get("window_left", int.MinValue);
            int top = AppConfig.Get("window_top", int.MinValue);
            if (left != int.MinValue && top != int.MinValue)
            {
                Left = left;
                Top = top;
            }
            if (AppConfig.Is("window_maximized")) WindowState = WindowState.Maximized;
        }

        private void ScheduleWindowPlacementSave()
        {
            if (!IsLoaded) return;
            _placementSaveTimer.Stop();
            _placementSaveTimer.Start();
        }

        private void SaveWindowPlacementNow()
        {
            if (!IsLoaded) return;
            SetConfigIfChanged("window_maximized", WindowState == WindowState.Maximized ? 1 : 0);
            if (WindowState != WindowState.Normal) return;
            SetConfigIfChanged("window_width", (int)ActualWidth);
            SetConfigIfChanged("window_height", (int)ActualHeight);
            SetConfigIfChanged("window_left", (int)Left);
            SetConfigIfChanged("window_top", (int)Top);
        }

        private static void SetConfigIfChanged(string key, int value)
        {
            if (AppConfig.Get(key, int.MinValue) != value) AppConfig.Set(key, value);
        }
    }
}
