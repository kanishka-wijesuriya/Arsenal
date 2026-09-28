using Arsenal.UI.ViewModels;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using TranslateTransform = System.Windows.Media.TranslateTransform;
using System.Windows.Threading;

namespace Arsenal.UI.Views.Windows
{
    public partial class QuickPanelWindow : Window
    {
        private bool _targetVisible;
        private readonly DispatcherTimer _tilePageWheelIdleTimer;
        private readonly PerformanceViewModel _performanceViewModel;
        private PerformanceTuningWindow? _performanceTuningWindow;
        public bool IsOpenOrOpening => IsVisible && _targetVisible;

        public QuickPanelWindow(QuickPanelViewModel viewModel, PerformanceViewModel performanceViewModel)
        {
            InitializeComponent();

            // Read before the window has been shown, so this is the width XAML asked for
            // rather than one a monitor change has already drifted. See LockNativeViewport.
            _nativeViewportWidth = Width;

            DataContext = viewModel;
            _performanceViewModel = performanceViewModel;

            _tilePageWheelIdleTimer = new DispatcherTimer
            {
                // Precision-touchpad inertia normally emits packets much closer
                // together than this. Once the stream stops, re-arm promptly so the
                // first packet of the next physical gesture is never discarded.
                Interval = TimeSpan.FromMilliseconds(80)
            };
            _tilePageWheelIdleTimer.Tick += TilePageWheelIdleTimer_Tick;
            Closed += (_, _) => _tilePageWheelIdleTimer.Stop();

            // A monitor can disappear while the panel is open. Per-monitor DPI then
            // moves the layered HWND to the remaining screen and may resize its native
            // viewport before WPF has remeasured the card. Re-fit after that transaction
            // so a bottom-aligned card cannot lose its header above the work area.
            DpiChanged += (_, _) => QueueViewportRefit();

            if (viewModel is not null)
            {
                viewModel.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(QuickPanelViewModel.IsDetailOpen))
                        TransitionToView(viewModel.IsDetailOpen);
                };

                // A page can be partially filled, so the card's height is not a constant.
                // Deferred to Background so the containers for the new tiles exist by
                // the time it is measured.
                viewModel.Tiles.CollectionChanged += (_, _) =>
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        FitTilePageViewport();
                        RemeasureMainView();
                    }), DispatcherPriority.Background);

                viewModel.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(QuickPanelViewModel.TilePage)) SlideToTilePage();
                };
            }

            HookTileDragging();

            // Tunnelling, and handled here rather than on any child: Escape closes the
            // panel outright from wherever focus happens to be - a slider mid-drag, a
            // button, a detail page - instead of a focused control claiming the key or
            // the detail page taking it as "go back". Closing the panel is what Escape
            // means here, whatever is on screen.
            PreviewKeyDown += (_, e) =>
            {
                Key key = e.Key == Key.System ? e.SystemKey : e.Key;
                if (key != Key.Escape) return;
                e.Handled = true;
                HideAnimated();
            };

            bool isQuickTest = Environment.GetCommandLineArgs().Any(
                arg => arg.Equals("--quick-test", StringComparison.OrdinalIgnoreCase));
            if (isQuickTest)
            {
                ShowInTaskbar = true;
                Topmost = false;
            }

            // Independent of the dismissal watches below, because it has to happen for a
            // quick-test run too - and because what it releases is not about focus.
            IsVisibleChanged += (_, e) => OnPanelVisibilityChanged((bool)e.NewValue);

            if (!isQuickTest)
            {
                Deactivated += (s, e) => DismissForActivationLoss();

                // Both watches only have to run while the panel is up, and this catches
                // every route out - the close animation, a direct Hide() from the tray
                // menu, or the window being torn down.
                IsVisibleChanged += (s, e) =>
                {
                    if ((bool)e.NewValue)
                    {
                        HookForegroundChanges();
                        HookOutsideClicks();
                    }
                    else
                    {
                        UnhookForegroundChanges();
                        UnhookOutsideClicks();
                    }
                };
            }
        }

        private void Dismiss()
        {
            if (!IsVisible || !_targetVisible) return;

            // Focus settles a beat after Show(), and a spurious deactivation in that
            // window used to start a close animation on top of the open one - the two
            // fighting over the same transform is what made the panel tremble.
            if (DateTime.UtcNow - _shownAt < ActivationSettleTime) return;

            _deactivateDismissedAt = DateTime.UtcNow;
            HideAnimated();
        }

        private void OpenFullApp_Click(object sender, RoutedEventArgs e)
        {
            if (System.Windows.Application.Current is App app)
            {
                // Let the layered flyout finish presenting its exit frames before the
                // full window takes foreground. Activating the full app immediately
                // covers/deactivates the panel and makes its normal close look instant.
                HideAnimated(app.ShowMainWindow);
                return;
            }

            HideAnimated();
        }

        private void OpenPerformanceTuning_Click(object sender, RoutedEventArgs e)
        {
            if (_performanceTuningWindow is null)
            {
                _performanceTuningWindow = new PerformanceTuningWindow(_performanceViewModel);
                _performanceTuningWindow.Closed += (_, _) => _performanceTuningWindow = null;
            }

            _performanceTuningWindow.Show();
            _performanceTuningWindow.Activate();
            HideAnimated();
        }

        private void ClosePanel_Click(object sender, RoutedEventArgs e) => HideAnimated();

        private void ChargeLimitSlider_Commit(object sender, System.Windows.Input.MouseButtonEventArgs e) => CommitChargeLimit();
        private void ChargeLimitSlider_KeyUp(object sender, System.Windows.Input.KeyEventArgs e) => CommitChargeLimit();
        private void KeyboardBrightnessSlider_Commit(object sender, System.Windows.Input.MouseButtonEventArgs e) => CommitKeyboardBrightness();
        private void KeyboardBrightnessSlider_KeyUp(object sender, System.Windows.Input.KeyEventArgs e) => CommitKeyboardBrightness();

        private void CommitChargeLimit()
        {
            if (DataContext is QuickPanelViewModel vm)
                vm.UpdateChargeLimit(vm.ChargeLimit);
        }

        private void CommitKeyboardBrightness()
        {
            if (DataContext is QuickPanelViewModel vm)
                vm.SetKeyboardBrightness(vm.KeyboardBrightness);
        }
    }
}
