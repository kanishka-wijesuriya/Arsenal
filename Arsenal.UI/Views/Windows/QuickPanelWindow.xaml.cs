using Arsenal.Application.Models;
using Arsenal.UI.ViewModels;
using System.Windows;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
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

        /// <summary>
        /// Closes the panel because it lost the foreground - clicked away from, tapped
        /// away from, Alt+Tabbed away from, or the Start menu opened over it.
        /// </summary>
        /// <remarks>
        /// The tray-press guard lives here and nowhere else. A tray click deactivates
        /// this window before the notify icon reports the press, and it is the click
        /// itself that must close the panel so the toggle does not reopen it.
        /// </remarks>
        private void DismissForActivationLoss()
        {
            if (System.Windows.Application.Current is App app && app.IsTrayLeftPressActive) return;
            Dismiss();
        }

        /// <summary>
        /// Closes the panel because a click landed somewhere that is provably not on it.
        /// </summary>
        /// <remarks>
        /// Deliberately unguarded. The tray-press guard above once applied to this path
        /// too and swallowed genuine outside clicks, which is what made the panel need
        /// clicking twice. It is not needed here: a click on the tray icon dismisses the
        /// panel through this path, and the tray toggle then claims that dismissal
        /// instead of reopening.
        /// </remarks>
        private void DismissForOutsideClick() => Dismiss();

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

        /// <summary>
        /// Takes the foreground for real, rather than asking for it.
        /// </summary>
        /// <remarks>
        /// Window.Activate() calls SetForegroundWindow, which Windows refuses unless the
        /// caller already owns the foreground - and when the panel opens from the tray,
        /// the shell owns it. A refused activation leaves the panel visible but never
        /// active, with no focus to lose when the user clicks elsewhere, which killed
        /// every activation-based dismissal signal at once.
        ///
        /// Attaching to the foreground thread's input queue puts this window in the same
        /// input context for the duration of the call, which is what makes the handover
        /// succeed. Once the panel genuinely holds the foreground, Deactivated fires for
        /// every way there is to turn away from it - mouse, touch, taskbar, Alt+Tab, the
        /// Start menu - because all of them take the foreground off us.
        /// </remarks>
        private void TakeForeground()
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            IntPtr foreground = GetForegroundWindow();
            if (foreground == hwnd) return;

            uint thisThread = GetCurrentThreadId();
            uint owningThread = foreground == IntPtr.Zero
                ? 0
                : GetWindowThreadProcessId(foreground, out _);

            bool attached = owningThread != 0
                && owningThread != thisThread
                && AttachThreadInput(thisThread, owningThread, true);

            try
            {
                SetForegroundWindow(hwnd);
                SetActiveWindow(hwnd);
                SetFocus(hwnd);
            }
            finally
            {
                if (attached) AttachThreadInput(thisThread, owningThread, false);
            }
        }

        private IntPtr _foregroundHook;
        private WinEventProc? _foregroundHookProc;

        private void HookForegroundChanges()
        {
            if (_foregroundHook != IntPtr.Zero) return;

            // Held in a field for the life of the hook: the callback is invoked from
            // unmanaged code, which does not keep the delegate alive on its own.
            _foregroundHookProc ??= OnForegroundChanged;
            _foregroundHook = SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
                _foregroundHookProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

            // A hook that silently fails to install takes one of the dismissal paths
            // with it and leaves no trace. Say so once, here, rather than leaving it to
            // be inferred from the panel refusing to close.
            if (_foregroundHook == IntPtr.Zero) Logger.WriteLine("Quick panel: foreground hook not installed");
        }

        private void UnhookForegroundChanges()
        {
            if (_foregroundHook == IntPtr.Zero) return;
            UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }

        /// <summary>
        /// Out-of-context hooks are delivered through the hooking thread's message queue,
        /// so this already arrives on the UI thread. Own-process events are filtered by
        /// the hook itself, leaving only a genuine hand-off to another application.
        /// </summary>
        private void OnForegroundChanged(
            IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (idObject != ObjIdWindow) return;
            DismissForActivationLoss();
        }

        private IntPtr _mouseHook;
        private LowLevelMouseProc? _mouseHookProc;

        /// <summary>
        /// Watches for a click anywhere outside the card, whether or not it moves the
        /// foreground.
        /// </summary>
        /// <remarks>
        /// Deactivated and the foreground watch both react to the <em>consequence</em> of
        /// a click, and the taskbar does not produce that consequence: clicking empty
        /// taskbar space, the clock or the notification area deliberately leaves the
        /// foreground where it was, so neither fires and the panel stayed open. Reacting
        /// to the click itself covers those, and the two activation watches stay for what
        /// a mouse hook cannot see - Alt+Tab, and focus moved from the keyboard.
        /// </remarks>
        private void HookOutsideClicks()
        {
            if (_mouseHook != IntPtr.Zero) return;

            _mouseHookProc ??= OnGlobalMouse;
            _mouseHook = SetWindowsHookEx(WhMouseLowLevel, _mouseHookProc, GetModuleHandle(null), 0);
            if (_mouseHook == IntPtr.Zero)
                Logger.WriteLine("Quick panel: outside-click hook not installed, error " + Marshal.GetLastWin32Error());
        }

        private void UnhookOutsideClicks()
        {
            if (_mouseHook == IntPtr.Zero) return;
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        /// <summary>
        /// Set on a press that landed outside the card, cleared by the release that ends
        /// it. Only a press and its own release together dismiss the panel.
        /// </summary>
        private bool _outsidePressPending;

        private IntPtr OnGlobalMouse(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                int message = (int)wParam;

                if (IsButtonDown(message))
                {
                    var hookData = Marshal.PtrToStructure<MouseLowLevelHookStruct>(lParam);
                    _outsidePressPending = !IsPointOnCard(hookData.Point);
                }
                else if (IsButtonUp(message) && _outsidePressPending)
                {
                    _outsidePressPending = false;

                    // Never close from inside the hook. Windows silently unhooks a
                    // low-level procedure that overruns its timeout, and hiding the panel
                    // means starting animations and running a layout pass.
                    Dispatcher.BeginInvoke(new Action(DismissForOutsideClick));
                }
            }

            // Always chain: the click belongs to whatever was clicked. The panel closing
            // is in addition to that, never instead of it.
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        private static bool IsButtonDown(int message)
            => message is WmLButtonDown or WmRButtonDown or WmMButtonDown or WmNcLButtonDown;

        private static bool IsButtonUp(int message)
            => message is WmLButtonUp or WmRButtonUp or WmMButtonUp or WmNcLButtonUp;

        /// <summary>
        /// Hit-tests against the visible card rather than the window. The window carries
        /// transparent slack for the shadow and the entrance animation, and on a detail
        /// page the card is bottom-aligned inside bounds that stay the height of the tile
        /// page - so a large part of the window is empty desktop as far as the user is
        /// concerned, and clicking it should dismiss.
        /// </summary>
        private bool IsPointOnCard(PointI point)
        {
            if (!IsVisible) return false;

            try
            {
                point = ToWindowCoordinates(point);

                System.Windows.Point topLeft = PanelChrome.PointToScreen(new System.Windows.Point(0, 0));
                System.Windows.Point bottomRight = PanelChrome.PointToScreen(
                    new System.Windows.Point(PanelChrome.ActualWidth, PanelChrome.ActualHeight));

                return point.X >= topLeft.X && point.X < bottomRight.X
                    && point.Y >= topLeft.Y && point.Y < bottomRight.Y;
            }
            catch
            {
                // No source yet, or mid-teardown, or a view transition between layout
                // passes. Treat an unanswerable hit test as on the card: a panel that
                // stays open one click too long is a nuisance, whereas dismissing here
                // eats the click that was meant for a control and looks like the button
                // did nothing. The activation watches still close the panel either way.
                return true;
            }
        }

        /// <summary>
        /// Brings a hook coordinate into the space <see cref="Visual.PointToScreen"/>
        /// answers in, so the two can be compared.
        /// </summary>
        /// <remarks>
        /// A low-level mouse hook reports per-monitor-aware screen coordinates - true
        /// physical pixels - whatever the receiving process is. This process is only
        /// system-DPI aware, so its own coordinates are fixed to the DPI that was the
        /// system's when it started. While those agree, comparing them directly works.
        ///
        /// They stop agreeing the moment the machine is on a display at a different
        /// scale from the one it booted on: plugging in an external monitor and letting
        /// the internal panel switch off is the ordinary way to get there. Windows then
        /// virtualises this process's coordinates, and every point PointToScreen returns
        /// is off by the ratio between the two scales. The card's rectangle lands
        /// somewhere the pointer can never be, so every press inside the panel tested as
        /// a press outside it and dismissed the panel - which read as the panel closing
        /// whenever anything in it was clicked.
        ///
        /// This is the conversion for exactly that: a no-op for a per-monitor-aware
        /// process, and the inverse of the virtualisation otherwise. It is left
        /// unconverted if the call fails, which is no worse than not calling it.
        /// </remarks>
        private PointI ToWindowCoordinates(PointI point)
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return point;

            PointI converted = point;
            return PhysicalToLogicalPointForPerMonitorDPI(hwnd, ref converted) ? converted : point;
        }

        private DateTime _shownAt = DateTime.MinValue;
        private DateTime _deactivateDismissedAt = DateTime.MinValue;
        private static readonly TimeSpan ActivationSettleTime = TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// How far ahead of the reported press its deactivation may land. A mouse click
        /// deactivates us and reaches the icon in the same instant, but the taskbar takes
        /// touch natively at finger-down while a notify icon only hears about it through
        /// the mouse messages Windows synthesizes at finger-up, once the gesture
        /// recognizer has ruled out a drag or a press-and-hold. So a tap reports its press
        /// a whole tap-duration after it dismissed us, and this has to span that - a hold
        /// long enough to overrun it has already become a press-and-hold, not a tap.
        /// </summary>
        private static readonly TimeSpan TrayPressLead = TimeSpan.FromMilliseconds(700);

        /// <summary>
        /// A tray click takes activation away from the panel, and that Deactivated can
        /// arrive before the notify icon reports the press - so the panel is already
        /// closing by the time the click itself is delivered. Reports whether the close
        /// in flight belongs to that press, letting the click be spent on the close it
        /// caused instead of toggling the panel straight back open.
        ///
        /// Claiming a dismissal also spends it, so at most one click is ever absorbed:
        /// if the window above does misjudge an unrelated click, the next one still opens
        /// the panel rather than leaving the icon dead.
        /// </summary>
        internal bool TryClaimTrayPressDismissal(DateTime trayPressedAt)
        {
            if (_targetVisible) return false;
            if (_deactivateDismissedAt < trayPressedAt - TrayPressLead) return false;
            if (_deactivateDismissedAt > DateTime.UtcNow) return false;

            _deactivateDismissedAt = DateTime.MinValue;
            return true;
        }

        private bool _detailShown;
        private int _viewTransitionVersion;

        /// <summary>
        /// A view transition owns both heights until it finishes. A tile added from the
        /// picker changes the grid and closes the picker in the same breath, and without
        /// this the re-fit would set a height the transition then animated away from.
        /// </summary>
        private bool _viewTransitionRunning;
        private bool _remeasureWhenSettled;
        private double _nativeViewportHeight;
        private double _mainChromeHeight;
        private double _mainHostHeight;

        /// <summary>
        /// Moves between the tile grid and a detail list without ever resizing the
        /// layered native window. Only the bottom-aligned card and its clipped viewport
        /// morph, so WPF cannot expose an intermediate full-height surface or move the
        /// tray anchor while composing a frame.
        /// </summary>
        private void TransitionToView(bool showDetail)
        {
            if (_detailShown == showDetail) return;
            _detailShown = showDetail;
            _viewTransitionRunning = true;
            int transitionVersion = ++_viewTransitionVersion;

            FrameworkElement incoming = showDetail ? DetailView : MainView;
            FrameworkElement outgoing = showDetail ? MainView : DetailView;
            TranslateTransform incomingShift = showDetail ? DetailViewShift : MainViewShift;
            TranslateTransform outgoingShift = showDetail ? MainViewShift : DetailViewShift;

            StopViewTransitionEases();
            StopViewAnimation(incoming, incomingShift);
            StopViewAnimation(outgoing, outgoingShift);

            double currentHostHeight = TransitionHost.ActualHeight;
            double currentChromeHeight = PanelChrome.ActualHeight;
            PanelChrome.Height = currentChromeHeight;
            TransitionHost.Height = currentHostHeight;

            // Put the incoming tree into layout first. Detail option containers are
            // generated after the collection changes, so measuring synchronously here
            // can see only the header and clip the option list.
            incoming.Visibility = Visibility.Visible;
            incoming.Opacity = 0.001;
            incoming.IsHitTestVisible = true;
            outgoing.IsHitTestVisible = false;
            UpdateLayout();

            // Both trees are static during the short cross-fade. Prime retained surfaces
            // before the Background callback starts the clocks, so opacity, translation,
            // and viewport clipping do not repeatedly rasterise every control.
            SetTransitionCache(incoming, true);
            SetTransitionCache(outgoing, true);

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (transitionVersion != _viewTransitionVersion) return;

                // Measure after WPF has generated and bound every incoming item. The
                // explicit host still clips the nearly transparent view until motion.
                if (showDetail) FitDetailList();
                incoming.InvalidateMeasure();
                incoming.Measure(new System.Windows.Size(
                    Math.Max(1, TransitionHost.ActualWidth), double.PositiveInfinity));
                double targetHostHeight = Math.Max(1, incoming.DesiredSize.Height);
                double targetChromeHeight = targetHostHeight
                    + Math.Max(0, currentChromeHeight - currentHostHeight);

                // Before the animation, so the card's bottom edge stays put while the
                // window's top edge moves instead of the card jumping at the end.
                EnsureViewportFits(targetChromeHeight);

                incomingShift.Y = showDetail ? 8 : -6;

                _chromeHeightEase.Start(
                    currentChromeHeight, targetChromeHeight, 240,
                    Controls.FrameEase.CubicInOut,
                    h => PanelChrome.Height = h,
                    completed: () => CompleteViewTransition(
                        transitionVersion, incoming, outgoing, incomingShift, outgoingShift,
                        targetChromeHeight, targetHostHeight));

                _hostHeightEase.Start(
                    currentHostHeight, targetHostHeight, 240,
                    Controls.FrameEase.CubicInOut,
                    h => TransitionHost.Height = h);

                _outgoingFade.Start(
                    outgoing.Opacity, 0, 100,
                    Controls.FrameEase.CubicOut,
                    v => outgoing.Opacity = v);

                _outgoingSlide.Start(
                    outgoingShift.Y, showDetail ? -4 : 4, 120,
                    Controls.FrameEase.CubicOut,
                    y => outgoingShift.Y = y);

                _incomingFade.Start(
                    0.001, 1, 165,
                    Controls.FrameEase.CubicOut,
                    v => incoming.Opacity = v,
                    delayMs: 48);

                _incomingSlide.Start(
                    incomingShift.Y, 0, 190,
                    Controls.FrameEase.CubicOut,
                    y => incomingShift.Y = y,
                    delayMs: 32);
            }), DispatcherPriority.Background);
        }

        private void CompleteViewTransition(
            int transitionVersion,
            FrameworkElement incoming,
            FrameworkElement outgoing,
            TranslateTransform incomingShift,
            TranslateTransform outgoingShift,
            double targetChromeHeight,
            double targetHostHeight)
        {
            if (transitionVersion != _viewTransitionVersion) return;

            StopViewTransitionEases();
            StopViewAnimation(incoming, incomingShift);
            StopViewAnimation(outgoing, outgoingShift);
            incoming.Visibility = Visibility.Visible;
            incoming.Opacity = 1;
            incomingShift.Y = 0;
            // Keep the outgoing surface laid out and cached at zero opacity. Returning
            // to it therefore never has to reconstruct the full tile page mid-motion.
            outgoing.Visibility = Visibility.Visible;
            outgoing.Opacity = 0;
            outgoing.IsHitTestVisible = false;
            outgoingShift.Y = 0;
            incoming.IsHitTestVisible = true;

            SetTransitionCache(incoming, false);
            SetTransitionCache(outgoing, false);

            PanelChrome.Height = targetChromeHeight;
            TransitionHost.Height = targetHostHeight;

            _viewTransitionRunning = false;
            if (!_remeasureWhenSettled) return;
            _remeasureWhenSettled = false;
            RemeasureMainView();
        }

        private static void StopViewAnimation(FrameworkElement view, TranslateTransform shift)
        {
            double opacity = view.Opacity;
            double y = shift.Y;
            view.BeginAnimation(OpacityProperty, null);
            shift.BeginAnimation(TranslateTransform.XProperty, null);
            shift.BeginAnimation(TranslateTransform.YProperty, null);
            view.Opacity = opacity;
            shift.X = 0;
            shift.Y = y;
        }

        private readonly Controls.FrameEase _chromeHeightEase = new();
        private readonly Controls.FrameEase _hostHeightEase = new();
        private readonly Controls.FrameEase _outgoingFade = new();
        private readonly Controls.FrameEase _outgoingSlide = new();
        private readonly Controls.FrameEase _incomingFade = new();
        private readonly Controls.FrameEase _incomingSlide = new();

        /// <summary>
        /// Drops every ease that composes a view transition, leaving each property at
        /// whatever value it currently holds.
        /// </summary>
        private void StopViewTransitionEases()
        {
            _chromeHeightEase.Stop();
            _hostHeightEase.Stop();
            _outgoingFade.Stop();
            _outgoingSlide.Stop();
            _incomingFade.Stop();
            _incomingSlide.Stop();
        }

        private static void SetTransitionCache(FrameworkElement view, bool cached)
        {
            if (cached && view.CacheMode is null)
            {
                System.Windows.Media.RenderOptions.SetCachingHint(view, System.Windows.Media.CachingHint.Cache);
                view.CacheMode = new System.Windows.Media.BitmapCache { SnapsToDevicePixels = true };
            }
            else if (!cached && view.CacheMode is not null)
            {
                view.CacheMode = null;
                System.Windows.Media.RenderOptions.SetCachingHint(view, System.Windows.Media.CachingHint.Unspecified);
            }
        }

        /// <summary>
        /// Puts both views back to the tile page without animating, so a panel that was
        /// closed on a detail page does not play a transition the next time it opens.
        /// </summary>
        private void ResetToMainView()
        {
            EndTileDrag();
            SetTileBitmapCache(true);

            _detailShown = false;
            _viewTransitionRunning = false;
            _viewTransitionVersion++;

            PanelChrome.BeginAnimation(HeightProperty, null);
            TransitionHost.BeginAnimation(HeightProperty, null);
            if (_mainChromeHeight > 0) PanelChrome.Height = _mainChromeHeight;
            if (_mainHostHeight > 0) TransitionHost.Height = _mainHostHeight;

            foreach (var (view, shift) in new (FrameworkElement View, TranslateTransform Shift)[]
            {
                (MainView, MainViewShift),
                (DetailView, DetailViewShift)
            })
            {
                SetTransitionCache(view, false);
                view.BeginAnimation(OpacityProperty, null);
                shift.BeginAnimation(TranslateTransform.XProperty, null);
                shift.BeginAnimation(TranslateTransform.YProperty, null);
                shift.X = 0;
                shift.Y = 0;
            }

            MainView.Visibility = Visibility.Visible;
            MainView.Opacity = 1;
            MainView.IsHitTestVisible = true;
            DetailView.Visibility = Visibility.Collapsed;
            DetailView.Opacity = 0;
            DetailView.IsHitTestVisible = false;

            // The heights restored above are the ones the grid last measured at. A tile
            // added or removed while a detail page was open makes them stale, and this is
            // the point where the grid becomes the visible page again.
            if (_remeasureWhenSettled)
            {
                _remeasureWhenSettled = false;
                RemeasureMainView();
            }
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

        /// <summary>The work area the panel is anchored in, in DIPs.</summary>
        private FlyoutBounds _anchorWorkArea;

        /// <summary>Set once the first capture has run, whatever it found.</summary>
        private bool _anchorCaptured;

        /// <summary>Which edge of that work area the taskbar, and so the tray, is on.</summary>
        private TaskbarEdge _anchorEdge = TaskbarEdge.Bottom;

        /// <summary>Height of the work area the panel is anchored in, in DIPs.</summary>
        private double _anchorHeight;

        /// <summary>
        /// The destination monitor's scale, captured with the anchor. The placement is
        /// computed in that monitor's DIPs and has to be turned back into pixels with
        /// the same number - never with whatever scale the HWND currently carries.
        /// </summary>
        private double _anchorScale = 1;

        /// <summary>
        /// The same work area in physical pixels. WPF can round equal DIP offsets to
        /// different pixels on the two axes, so the final visual alignment uses this
        /// unscaled rectangle as its source of truth.
        /// </summary>
        private System.Drawing.Rectangle _anchorWorkingAreaPixels;

        /// <summary>
        /// Records the corner the panel is pinned to, and the edge that put it there.
        /// Captured once per open: the height animation re-places the window on every
        /// frame, and re-reading the cursor's screen each time would make the panel jump
        /// if the pointer crossed monitors mid-transition. Once per open is also what
        /// makes a taskbar moved while Arsenal is running take effect - the next press
        /// reads the new layout with nothing to restart.
        /// </summary>
        private void CaptureTrayAnchor()
        {
            var point = System.Windows.Forms.Cursor.Position;
            var screen = System.Windows.Forms.Screen.FromPoint(point);
            var workingArea = screen.WorkingArea;
            _anchorWorkingAreaPixels = workingArea;
            uint dpi = DestinationDpi(point);
            double scale = dpi > 0 ? dpi / 96d : 1d;
            _anchorScale = scale;
            _anchorWorkArea = new FlyoutBounds(
                workingArea.Left / scale,
                workingArea.Top / scale,
                workingArea.Right / scale,
                workingArea.Bottom / scale);
            _anchorHeight = workingArea.Height / scale;
            _anchorEdge = TaskbarEdgeForScreen(screen);
            _anchorCaptured = true;

            // Before anything measures: the card is glued to one edge of a window that is
            // taller than it is, and which edge that is changes what a detail page does
            // to the layout it grows into.
            ApplyCardAlignment();
        }

        /// <summary>
        /// The scale of the monitor the panel is about to open on.
        /// </summary>
        /// <remarks>
        /// Arsenal is PerMonitorV2 (<c>ApplicationHighDpiMode</c> in Arsenal.UI.csproj,
        /// applied by DpiAwareEntryPoint), so every monitor has its own scale and the
        /// panel has to be measured in the scale of the one it is going to. Reading it
        /// from the window instead answers for wherever the HWND happens to be standing
        /// - which, on the open after a display change, is still the monitor that has
        /// just gone away.
        /// </remarks>
        private uint DestinationDpi(System.Drawing.Point point)
        {
            var nativePoint = new PointI { X = point.X, Y = point.Y };
            IntPtr monitor = MonitorFromPoint(nativePoint, MonitorDefaultToNearest);
            if (monitor != IntPtr.Zero
                && GetDpiForMonitor(monitor, MonitorDpiTypeEffective, out uint dpiX, out _) == 0
                && dpiX > 0)
            {
                return dpiX;
            }

            return GetDpiForWindow(new WindowInteropHelper(this).EnsureHandle());
        }

        /// <summary>
        /// Glues the card to the edge of the window nearest the taskbar, so the window
        /// slack it floats in is always on the far side of it.
        /// </summary>
        private void ApplyCardAlignment()
        {
            VerticalAlignment alignment = TrayFlyout.TrayIsAtTheTop(_anchorEdge)
                ? VerticalAlignment.Top
                : VerticalAlignment.Bottom;

            if (PanelChrome.VerticalAlignment == alignment) return;
            PanelChrome.VerticalAlignment = alignment;
            PanelShadow.VerticalAlignment = alignment;

            // The overlay layer is sized to the card, so it has to be glued to the same
            // edge. Left behind, it sits over whatever transparent slack the window is
            // carrying and the scrim reads as a box beside the panel instead of on it.
            PanelOverlayLayer.VerticalAlignment = alignment;
        }

        /// <summary>
        /// Answers which edge the taskbar is docked to for the monitor the panel is about
        /// to open on.
        /// </summary>
        /// <remarks>
        /// The shell reports the position of the primary taskbar only, so that answer is
        /// used directly when that bar is actually on this monitor - it is exact, and it
        /// stays right for an auto-hidden bar, which reserves no work area to infer from.
        /// On any other monitor the reserved space is the better signal, because that is
        /// what a secondary taskbar moves; the primary bar's edge is the fallback, since
        /// Windows docks every secondary taskbar to the same edge as the primary.
        /// </remarks>
        private static TaskbarEdge TaskbarEdgeForScreen(System.Windows.Forms.Screen screen)
        {
            if (EdgeOverride is TaskbarEdge forced) return forced;

            TaskbarEdge shellEdge = TaskbarEdge.Bottom;
            if (TryGetTaskbarPosition(out TaskbarEdge edge, out System.Drawing.Rectangle taskbar))
            {
                shellEdge = edge;
                if (taskbar.IntersectsWith(screen.Bounds)) return shellEdge;
            }

            return TrayFlyout.EdgeFromReservedSpace(screen.Bounds, screen.WorkingArea, shellEdge);
        }

        /// <summary>
        /// Forces an edge for a <c>--quick-test</c> run, so all four layouts can be seen
        /// without restarting the shell. Null in every normal launch.
        /// </summary>
        private static readonly TaskbarEdge? EdgeOverride = ReadEdgeOverride();

        private static TaskbarEdge? ReadEdgeOverride()
        {
            const string Prefix = "--panel-edge=";
            string? value = Environment.GetCommandLineArgs()
                .FirstOrDefault(arg => arg.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase));
            if (value is null) return null;
            return Enum.TryParse(value[Prefix.Length..], ignoreCase: true, out TaskbarEdge edge)
                ? edge
                : null;
        }

        private static bool TryGetTaskbarPosition(out TaskbarEdge edge, out System.Drawing.Rectangle bounds)
        {
            edge = TaskbarEdge.Bottom;
            bounds = System.Drawing.Rectangle.Empty;

            var data = new AppBarData { cbSize = Marshal.SizeOf<AppBarData>() };
            if (SHAppBarMessage(AbmGetTaskbarPos, ref data) == IntPtr.Zero) return false;

            edge = data.uEdge switch
            {
                AbeLeft => TaskbarEdge.Left,
                AbeTop => TaskbarEdge.Top,
                AbeRight => TaskbarEdge.Right,
                _ => TaskbarEdge.Bottom
            };
            bounds = System.Drawing.Rectangle.FromLTRB(
                data.rc.Left, data.rc.Top, data.rc.Right, data.rc.Bottom);
            return bounds.Width > 0 && bounds.Height > 0;
        }

        /// <summary>
        /// Gap between the visible card and the work area, applied equally to the taskbar
        /// edge and the screen edge so the panel is seated in its corner rather than
        /// floating off it.
        /// </summary>
        private const double TrayGap = 11;

        public void PositionNearTray()
        {
            if (!_anchorCaptured) CaptureTrayAnchor();

            double panelWidth = ActualWidth > 0 ? ActualWidth : Width;
            double panelHeight = ActualHeight > 0 ? ActualHeight : 650;

            // The card is inset from the window by the gutter its shadow needs, so the
            // window is placed that much nearer the edge to land the card on the gap.
            // Read from the live margin: shrinking the gutter must not move the card.
            Thickness margin = MotionRoot.Margin;
            FlyoutPlacement placement = TrayFlyout.Place(
                _anchorEdge,
                _anchorWorkArea,
                panelWidth,
                panelHeight,
                TrayGap,
                new FlyoutGutter(margin.Left, margin.Top, margin.Right, margin.Bottom));

            _enterX = placement.EnterX;
            _enterY = placement.EnterY;

            // Moved as pixels rather than through Left and Top. Those are DIPs, and WPF
            // turns them into pixels with the scale of the monitor the HWND is standing
            // on - which on the open after a display change is the one that has just
            // gone away, not the one this placement was measured for. Multiplying by the
            // anchor's own scale and moving the HWND keeps the two ends in one space.
            MoveToPixels(placement.Left * _anchorScale, placement.Top * _anchorScale);

            // 11 DIPs at 150% is 16.5 physical pixels. Depending on the fractional
            // window origin, WPF can resolve that as 16px on one axis and 17px on the
            // other. Measure the rendered card after layout and move only the native
            // HWND by the residual pixel delta; this guarantees the gap against the
            // taskbar is exactly the gap against the screen edge at every DPI.
            UpdateLayout();
            MatchTaskbarGapToScreenGap();
        }

        /// <summary>
        /// Puts the window's top-left corner on a physical pixel, whatever scale the
        /// window currently believes it is drawn at.
        /// </summary>
        private void MoveToPixels(double left, double top)
        {
            IntPtr hwnd = new WindowInteropHelper(this).EnsureHandle();
            if (hwnd == IntPtr.Zero) return;

            SetWindowPos(
                hwnd,
                IntPtr.Zero,
                (int)Math.Round(left, MidpointRounding.AwayFromZero),
                (int)Math.Round(top, MidpointRounding.AwayFromZero),
                0,
                0,
                SetWindowPositionFlags.NoSize | SetWindowPositionFlags.NoZOrder | SetWindowPositionFlags.NoActivate);
        }

        /// <summary>
        /// Equalises the two visible gaps in physical pixels, after WPF has rounded the
        /// placement above onto the device grid.
        /// </summary>
        /// <remarks>
        /// The gap against the screen edge is the reference and the gap against the
        /// taskbar is moved onto it, because the screen edge is the one the eye has a
        /// straight line to compare against. Which axis is which follows the taskbar: a
        /// bar along the bottom leaves the right-hand gap as the reference, a bar down
        /// either side leaves the bottom one.
        /// </remarks>
        private void MatchTaskbarGapToScreenGap()
        {
            if (_anchorWorkingAreaPixels.IsEmpty || PanelChrome.ActualWidth <= 0 || PanelChrome.ActualHeight <= 0)
                return;

            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            System.Windows.Point topLeft = PanelChrome.PointToScreen(new System.Windows.Point(0, 0));
            System.Windows.Point bottomRight = PanelChrome.PointToScreen(
                new System.Windows.Point(PanelChrome.ActualWidth, PanelChrome.ActualHeight));

            // Opening/closing motion is a render-only translation. Remove its current
            // device-pixel contribution so placement is based on the card's resting
            // geometry even when PositionNearTray runs before the first animation.
            double shiftX = PanelTransform.X * dpi.DpiScaleX;
            double shiftY = PanelTransform.Y * dpi.DpiScaleY;
            topLeft.X -= shiftX;
            topLeft.Y -= shiftY;
            bottomRight.X -= shiftX;
            bottomRight.Y -= shiftY;

            double leftGap = topLeft.X - _anchorWorkingAreaPixels.Left;
            double rightGap = _anchorWorkingAreaPixels.Right - bottomRight.X;
            double topGap = topLeft.Y - _anchorWorkingAreaPixels.Top;
            double bottomGap = _anchorWorkingAreaPixels.Bottom - bottomRight.Y;

            // Placement already keeps an oversized window's origin on screen. Do not
            // undo that safety by pulling the bottom-aligned card towards the taskbar.
            // The responsive viewport normally prevents this case; the guard also
            // protects the first layout frame and any unexpected oversized content.
            if (leftGap < 0 || rightGap < 0 || topGap < 0 || bottomGap < 0) return;

            int horizontal = 0;
            int vertical = 0;
            switch (_anchorEdge)
            {
                case TaskbarEdge.Bottom:
                    vertical = Delta(bottomGap, rightGap);
                    break;
                case TaskbarEdge.Top:
                    vertical = -Delta(topGap, rightGap);
                    break;
                case TaskbarEdge.Left:
                    horizontal = -Delta(leftGap, bottomGap);
                    break;
                case TaskbarEdge.Right:
                    horizontal = Delta(rightGap, bottomGap);
                    break;
            }

            if (horizontal == 0 && vertical == 0) return;

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out NativeRect bounds)) return;

            SetWindowPos(
                hwnd,
                IntPtr.Zero,
                bounds.Left + horizontal,
                bounds.Top + vertical,
                0,
                0,
                SetWindowPositionFlags.NoSize | SetWindowPositionFlags.NoZOrder | SetWindowPositionFlags.NoActivate);

            // How far the window has to move, in whole device pixels, for the first gap
            // to equal the second. Positive shrinks the first gap.
            static int Delta(double gap, double reference)
                => (int)Math.Round(gap - reference, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// Fits the tile page to the current monitor and locks the transparent HWND to
        /// that size for this open. Detail pages remain compact because their visible
        /// card is bottom-aligned inside these stable transparent bounds.
        /// </summary>
        private void LockNativeViewport()
        {
            // Before the height is read: a second page of tiles must be clipped away
            // first, or the locked window would be sized to hold every one of them.
            FitTilePageViewport();
            FitMainView();
            UpdateLayout();

            // Build the tile surface while the panel is settling after its first layout,
            // not on the same input frame that starts a page switch. Keeping this cache
            // warm turns every later page transition into one compositor translation.
            SetTileBitmapCache(true);

            double chrome = PanelChrome.Padding.Top + PanelChrome.Padding.Bottom
                + PanelChrome.BorderThickness.Top + PanelChrome.BorderThickness.Bottom;
            _mainHostHeight = Math.Max(1, MainView.DesiredSize.Height);
            _mainChromeHeight = _mainHostHeight + chrome;
            _nativeViewportHeight = _mainChromeHeight
                + MotionRoot.Margin.Top + MotionRoot.Margin.Bottom;

            SizeToContent = System.Windows.SizeToContent.Manual;
            Height = _nativeViewportHeight;
            PanelChrome.Height = _mainChromeHeight;
            TransitionHost.Height = _mainHostHeight;
            UpdateLayout();
        }

        /// <summary>
        /// Lets the normal layout stand on roomy displays. On a short work area it caps
        /// the main surface and gives it a scrollbar, keeping the header and every
        /// control reachable instead of clipping the top of the bottom-aligned card.
        /// </summary>
        private void FitMainView()
        {
            double width = Math.Max(1, TransitionHost.ActualWidth);
            double chrome = PanelChrome.Padding.Top + PanelChrome.Padding.Bottom
                + PanelChrome.BorderThickness.Top + PanelChrome.BorderThickness.Bottom;

            MainView.MaxHeight = double.PositiveInfinity;
            MainContent.InvalidateMeasure();
            MainContent.Measure(new System.Windows.Size(width, double.PositiveInfinity));

            MainView.MaxHeight = Math.Max(160, MaxCardHeight() - chrome);
            MainView.InvalidateMeasure();
            MainView.Measure(new System.Windows.Size(width, double.PositiveInfinity));
        }

        private int _viewportRefitVersion;

        private void QueueViewportRefit()
        {
            int version = ++_viewportRefitVersion;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (version != _viewportRefitVersion || !IsVisible || _nativeViewportHeight <= 0) return;

                CaptureTrayAnchor();
                LockNativeViewport();
                PositionNearTray();
            }), DispatcherPriority.Loaded);
        }

        #region Tile pages and dragging

        /// <summary>
        /// The items panel, which is only reachable through the visual tree - an
        /// ItemsPanelTemplate's name is scoped to the template, not to this window.
        /// </summary>
        private Controls.AnimatedTileGrid? TileGrid => FindGrid(QuickTilesGrid);

        private static Controls.AnimatedTileGrid? FindGrid(DependencyObject root)
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is Controls.AnimatedTileGrid grid) return grid;
                if (FindGrid(child) is { } nested) return nested;
            }
            return null;
        }

        /// <summary>Height of one page of tiles, or 0 before the grid has measured.</summary>
        private double TilePageHeight { get; set; }
        private int _tilePageWheelDirection;

        /// <summary>
        /// Wheel travel banked toward the next page change, in wheel-delta units.
        /// </summary>
        /// <remarks>
        /// This used to be a single "gesture consumed" flag, which spent one page per
        /// gesture and then discarded every packet until the wheel had been still for the
        /// idle interval. Holding a spin therefore moved exactly one page and then
        /// appeared to stop responding. Banking the travel instead means a wheel notch is
        /// always worth a page, and a touchpad swipe is worth however many notches of
        /// travel it actually carried.
        /// </remarks>
        private double _tilePageWheelTravel;

        /// <summary>
        /// Earliest tick at which the next page change may start, so a fast spin cannot
        /// outrun the slide and turn several pages into one blur.
        /// </summary>
        private long _tilePageWheelNextAllowedAt;

        private static readonly long TilePageWheelSpacingTicks =
            TimeSpan.FromMilliseconds(130).Ticks;

        /// <summary>
        /// Clips the tile viewport to a page. Tiles past the first page sit below the
        /// clip and are reached by sliding the whole list, so the panel keeps its height
        /// however many tiles are on it.
        /// </summary>
        /// <summary>Guards the mutual call with <see cref="SlideToTilePage(bool)"/>.</summary>
        private bool _fittingTilePages;

        private void FitTilePageViewport()
        {
            if (_fittingTilePages || TileGrid is not { } grid) return;

            _fittingTilePages = true;
            try
            {
                QuickTilesGrid.UpdateLayout();
                double rowHeight = grid.RowHeight;
                if (rowHeight <= 0) return;

                int rowsPerPage = QuickPanelViewModel.TilesPerPage / Math.Max(1, grid.Columns);
                int rows = Math.Min(rowsPerPage, Math.Max(1, grid.RowCount));

                TilePageHeight = rowHeight * rowsPerPage;
                TilePageViewport.Height = rowHeight * rows;
            }
            finally
            {
                _fittingTilePages = false;
            }

            SlideToTilePage(animate: false);
        }

        private void SlideToTilePage() => SlideToTilePage(animate: true);

        private void TilePageWheelIdleTimer_Tick(object? sender, EventArgs e)
        {
            _tilePageWheelIdleTimer.Stop();
            // Travel left over from a gesture that stopped short of a page is not carried
            // into the next one, so a page never turns from a flick the user has finished.
            _tilePageWheelTravel = 0;
            _tilePageWheelDirection = 0;
            _tilePageWheelNextAllowedAt = 0;
        }

        /// <summary>
        /// Banks this packet's travel and reports whether it has bought a page change.
        /// </summary>
        private bool TryBeginTilePageWheel(int delta)
        {
            if (delta == 0)
                return false;

            int direction = Math.Sign(delta);
            if (direction != _tilePageWheelDirection)
            {
                // A reversal is a new gesture; whatever was banked pointed the other way.
                _tilePageWheelTravel = 0;
                _tilePageWheelDirection = direction;
                _tilePageWheelNextAllowedAt = 0;
            }

            _tilePageWheelIdleTimer.Stop();
            _tilePageWheelIdleTimer.Start();

            // Capped so a spin faster than the rate limit below cannot build a backlog of
            // pages that would keep turning after the wheel has stopped.
            _tilePageWheelTravel = Math.Min(
                _tilePageWheelTravel + Math.Abs(delta),
                WheelDeltaPerNotch * 2d);

            if (_tilePageWheelTravel < WheelDeltaPerNotch)
                return false;

            // A spin faster than the slide can present would just blur pages together, so
            // hold the extra travel until the previous change has had time to be seen.
            long now = DateTime.UtcNow.Ticks;
            if (now < _tilePageWheelNextAllowedAt)
                return false;

            _tilePageWheelTravel -= WheelDeltaPerNotch;
            _tilePageWheelNextAllowedAt = now + TilePageWheelSpacingTicks;
            return true;
        }

        private const double WheelDeltaPerNotch = 120d;

        private void TilePageViewport_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (DataContext is not QuickPanelViewModel vm || !vm.HasTilePages || _dragActive)
                return;

            // A precision touchpad emits several small wheel packets for one gesture.
            // The idle timer re-arms independently after the stream finishes, instead
            // of making the next input packet both re-arm and get discarded. A genuine
            // direction reversal is accepted immediately even before that timer fires.
            e.Handled = true;
            if (!TryBeginTilePageWheel(e.Delta))
                return;

            if (e.Delta < 0 && vm.CanGoForwardTilePage)
            {
                vm.NextTilePage();
            }
            else if (e.Delta > 0 && vm.CanGoBackTilePage)
            {
                vm.PreviousTilePage();
            }
        }

        private void SlideToTilePage(bool animate)
        {
            if (DataContext is not QuickPanelViewModel vm) return;

            // A page change can be the first thing that ever needs the measurement - the
            // arrows are reachable before anything else has had cause to take it.
            if (TilePageHeight <= 0) FitTilePageViewport();
            if (TilePageHeight <= 0) return;

            double target = -vm.TilePage * TilePageHeight;

            // Held, not restarted from zero: pressing the arrow twice quickly should carry
            // on from wherever the first slide had reached.
            double current = TilePageShift.Y;
            _tilePageSlide.Stop();
            TilePageShift.Y = current;

            // A dragged tile must remain attached to the pointer. Moving the backing
            // page while pointer coordinates already refer to the destination page
            // makes cross-page drops jump and land in the wrong slot.
            if (!animate || _dragActive || Math.Abs(current - target) < 0.5)
            {
                TilePageShift.Y = target;
                return;
            }

            // The cache is normally warmed before input reaches this method. The guard is
            // for a page command fired unusually early during first layout.
            SetTileBitmapCache(true);

            // Driven per presented frame rather than by a WPF timeline. The timeline moved
            // this transform only a handful of times across the whole 320ms - the slide
            // arrived as two or three jumps - whatever DesiredFrameRate it was given. See
            // FrameEase for the measurements.
            _tilePageSlide.Start(
                current,
                target,
                320d,
                Controls.FrameEase.SineInOut,
                y => TilePageShift.Y = y);
        }

        private readonly Controls.FrameEase _tilePageSlide = new();
        private readonly Controls.FrameEase _tileSettleX = new();
        private readonly Controls.FrameEase _tileSettleY = new();

        /// <summary>
        /// Keeps the complete two-page tile strip as one retained compositor surface.
        /// Creating it at page-switch time caused a large first-frame rasterisation, and
        /// removing it at completion caused a second visible hitch. It is disabled only
        /// while a tile is following the pointer, when its contents genuinely change on
        /// every input frame.
        /// </summary>
        /// <summary>
        /// Gives back what the panel only needs while it is on screen.
        /// </summary>
        /// <remarks>
        /// This window is a singleton: once opened it is hidden rather than destroyed, so
        /// everything it holds stays held for the rest of the session. Two things are
        /// worth releasing. The tile grid carries a <see cref="System.Windows.Media.BitmapCache"/>,
        /// which is a rasterised copy of the whole grid at screen resolution and is of no
        /// use to a hidden window. And nothing on this path ever collected: the main
        /// window trims when it returns to the tray, the panel did not, which is why
        /// closing the panel released nothing until the main window happened to be opened
        /// and closed afterwards.
        ///
        /// The trim waits for the close animation. It induces a gen2 collection, and doing
        /// that while the last frames are still being composited on a layered window is
        /// exactly where a stutter would show.
        /// </remarks>
        private void OnPanelVisibilityChanged(bool visible)
        {
            if (visible)
            {
                SetTileBitmapCache(true);
                return;
            }

            SetTileBitmapCache(false);
            Services.BackgroundMemoryRelease.Schedule();
        }

        private void SetTileBitmapCache(bool cached)
        {
            if (cached && QuickTilesGrid.CacheMode is null)
            {
                System.Windows.Media.RenderOptions.SetCachingHint(QuickTilesGrid, System.Windows.Media.CachingHint.Cache);
                QuickTilesGrid.CacheMode = new System.Windows.Media.BitmapCache { SnapsToDevicePixels = true };
            }
            else if (!cached && QuickTilesGrid.CacheMode is not null)
            {
                QuickTilesGrid.CacheMode = null;
                System.Windows.Media.RenderOptions.SetCachingHint(QuickTilesGrid, System.Windows.Media.CachingHint.Unspecified);
            }
        }

        private Controls.QuickTile? _draggingTile;
        private QuickTileSlot? _draggingSlot;
        private System.Windows.Point _dragOrigin;

        /// <summary>Where in the tile the pointer took hold of it.</summary>
        private System.Windows.Point _grabWithinTile;
        private bool _dragActive;
        private long _lastDragPageSwitchAt;
        private long _dragPageEdgeSince;
        private int _dragPageEdgeDirection;

        /// <summary>
        /// Changes page when a dragged tile reaches the top or bottom edge. Mouse
        /// capture keeps these events arriving just outside the clipped viewport too.
        /// </summary>
        private bool TrySwitchTilePageDuringDrag(System.Windows.Input.MouseEventArgs e)
        {
            if (!_dragActive || DataContext is not QuickPanelViewModel vm || !vm.HasTilePages)
                return false;

            long now = Environment.TickCount64;
            System.Windows.Point viewportPoint = e.GetPosition(TilePageViewport);
            const double edge = 24;
            int direction = viewportPoint.Y <= edge ? -1
                : viewportPoint.Y >= TilePageViewport.ActualHeight - edge ? 1
                : 0;

            if (direction == 0)
            {
                _dragPageEdgeDirection = 0;
                _dragPageEdgeSince = 0;
                return false;
            }
            if (direction != _dragPageEdgeDirection)
            {
                _dragPageEdgeDirection = direction;
                _dragPageEdgeSince = now;
                return false;
            }
            if (now - _dragPageEdgeSince < 180 || now - _lastDragPageSwitchAt < 420)
                return false;

            int before = vm.TilePage;

            if (direction < 0 && vm.CanGoBackTilePage)
                vm.PreviousTilePage();
            else if (direction > 0 && vm.CanGoForwardTilePage)
                vm.NextTilePage();

            if (vm.TilePage == before) return false;

            _lastDragPageSwitchAt = now;
            _dragPageEdgeSince = now;
            QuickTilesGrid.UpdateLayout();
            return true;
        }

        /// <summary>
        /// Reordering by dragging, done with mouse capture rather than
        /// <see cref="DragDrop.DoDragDrop"/>: that runs its own modal loop and takes the
        /// foreground with it, which this window reads as being clicked away from and
        /// answers by closing itself mid-drag.
        /// </summary>
        private void HookTileDragging()
        {
            QuickTilesGrid.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (DataContext is not QuickPanelViewModel { IsEditingTiles: true }) return;
                if (FindTile(e.OriginalSource as DependencyObject) is not { } tile) return;
                if (tile.DataContext is not QuickTileSlot slot) return;

                _draggingTile = tile;
                _draggingSlot = slot;
                _dragOrigin = e.GetPosition(QuickTilesGrid);
                _dragActive = false;
            };

            QuickTilesGrid.PreviewMouseMove += (_, e) =>
            {
                if (_draggingTile is null || e.LeftButton != MouseButtonState.Pressed) return;

                System.Windows.Point here = e.GetPosition(QuickTilesGrid);
                if (!_dragActive)
                {
                    if (Math.Abs(here.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance
                        && Math.Abs(here.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;

                    _dragActive = true;
                    _lastDragPageSwitchAt = 0;
                    _dragPageEdgeSince = 0;
                    _dragPageEdgeDirection = 0;
                    SetTileBitmapCache(false);
                    _draggingTile.CaptureMouse();
                    _draggingTile.Opacity = 0.9;

                    // On the panel's own child, not on the tile inside it: ZIndex is read
                    // off the direct children of the panel that arranges them, so setting
                    // it on the tile did nothing and the one being dragged slid underneath
                    // every tile it passed over.
                    if (ContainerOf(_draggingTile) is { } lifted)
                    {
                        System.Windows.Controls.Panel.SetZIndex(lifted, 10);
                        if (TileGrid is { } starting)
                        {
                            starting.DraggingChild = lifted;

                            // Where inside the tile it was picked up. Everything after this
                            // is measured from the cell the tile currently occupies, so the
                            // grab point survives the cell changing underneath it.
                            System.Windows.Point cell = starting.CellOrigin(lifted) ?? default;
                            _grabWithinTile = new System.Windows.Point(
                                _dragOrigin.X - cell.X, _dragOrigin.Y - cell.Y);
                        }
                    }
                }

                if (TrySwitchTilePageDuringDrag(e))
                    here = e.GetPosition(QuickTilesGrid);

                FollowPointer(here);
                ReorderUnderPointer(e);
            };

            QuickTilesGrid.PreviewMouseLeftButtonUp += (_, _) => EndTileDrag();
            QuickTilesGrid.MouseLeave += (_, e) => { if (e.LeftButton != MouseButtonState.Pressed) EndTileDrag(); };
        }

        /// <summary>
        /// Puts the dragged tile back under the pointer.
        /// </summary>
        /// <remarks>
        /// The offset is measured from the cell the tile currently sits in rather than
        /// from where the drag began. Those are the same thing until the tile is reordered
        /// - and at that moment the cell moves out from under it, so an offset anchored to
        /// the starting point left the tile displaced by a whole cell and the pointer no
        /// longer on it.
        /// </remarks>
        private void FollowPointer(System.Windows.Point here)
        {
            if (TileGrid is not { } grid) return;
            if (ContainerOf(_draggingTile) is not { } container) return;

            System.Windows.Point cell = grid.CellOrigin(container) ?? default;
            TranslateTransform offset = Controls.AnimatedTileGrid.OffsetOf(container);
            offset.X = here.X - cell.X - _grabWithinTile.X;
            offset.Y = here.Y - cell.Y - _grabWithinTile.Y;
        }

        /// <summary>Moves the dragged tile into whichever slot the pointer is over.</summary>
        private void ReorderUnderPointer(System.Windows.Input.MouseEventArgs e)
        {
            if (DataContext is not QuickPanelViewModel vm || _draggingSlot is null) return;
            if (TileGrid is not { } grid || grid.RowHeight <= 0) return;

            // The pointer's position within the list, not the viewport: the list is
            // shifted up by whole pages, so a drag on page two is past that offset.
            System.Windows.Point point = e.GetPosition(QuickTilesGrid);
            int columns = Math.Max(1, grid.Columns);
            double columnWidth = QuickTilesGrid.ActualWidth / columns;
            if (columnWidth <= 0) return;

            int column = Math.Clamp((int)(point.X / columnWidth), 0, columns - 1);
            int row = Math.Max(0, (int)(point.Y / grid.RowHeight));
            int index = Math.Clamp(row * columns + column, 0, vm.Tiles.Count - 1);

            if (vm.Tiles.IndexOf(_draggingSlot) == index) return;

            vm.MoveTile(_draggingSlot, index);

            // Laid out at once, then re-pinned: the tile's cell has just changed, and
            // FollowPointer measures from that cell.
            if (ContainerOf(_draggingTile) is { } container) grid.DraggingChild = container;
            QuickTilesGrid.UpdateLayout();
            FollowPointer(point);
        }

        private void EndTileDrag()
        {
            if (_draggingTile is null) return;

            Controls.QuickTile tile = _draggingTile;
            bool wasDragging = _dragActive;
            _draggingTile = null;
            _dragActive = false;

            tile.ReleaseMouseCapture();
            tile.Opacity = 1;

            if (TileGrid is { } grid) grid.DraggingChild = null;

            if (ContainerOf(tile) is { } container)
            {
                TranslateTransform offset = Controls.AnimatedTileGrid.OffsetOf(container);
                _tileSettleX.Start(offset.X, 0, 170, Controls.FrameEase.CubicOut, v => offset.X = v);

                // Dropped back into the stack only once it has finished travelling, so it
                // does not disappear behind its neighbours part-way through settling.
                _tileSettleY.Start(
                    offset.Y, 0, 170, Controls.FrameEase.CubicOut,
                    v => offset.Y = v,
                    completed: () =>
                    {
                        System.Windows.Controls.Panel.SetZIndex(container, 0);
                        if (!_dragActive && _draggingTile is null) SetTileBitmapCache(true);
                    });
            }
            else if (wasDragging)
            {
                SetTileBitmapCache(true);
            }

            if (wasDragging && DataContext is QuickPanelViewModel vm) vm.CommitTileOrder();
            _draggingSlot = null;
        }

        /// <summary>The panel child holding a tile - what the grid arranges and animates.</summary>
        private UIElement? ContainerOf(Controls.QuickTile? tile)
        {
            if (tile is null || TileGrid is not { } grid) return null;

            DependencyObject? node = tile;
            while (node is not null)
            {
                DependencyObject? parent = System.Windows.Media.VisualTreeHelper.GetParent(node);
                if (ReferenceEquals(parent, grid)) return node as UIElement;
                node = parent;
            }
            return null;
        }

        private static Controls.QuickTile? FindTile(DependencyObject? source)
        {
            while (source is not null)
            {
                if (source is Controls.QuickTile tile) return tile;
                source = source is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                    ? System.Windows.Media.VisualTreeHelper.GetParent(source)
                    : System.Windows.LogicalTreeHelper.GetParent(source);
            }
            return null;
        }

        #endregion

        /// <summary>
        /// The tallest the visible card may be drawn: the work area it is anchored in,
        /// less the gutter its shadow needs and the gap it keeps off the taskbar.
        /// </summary>
        private double MaxCardHeight()
        {
            double workArea = _anchorHeight > 0 ? _anchorHeight : SystemParameters.WorkArea.Height;
            double usable = Math.Min(workArea, MaxHeight);
            return Math.Max(240, usable - MotionRoot.Margin.Top - MotionRoot.Margin.Bottom - TrayGap);
        }

        /// <summary>
        /// Caps the option list so a long one scrolls rather than growing the card past
        /// the screen.
        /// </summary>
        /// <remarks>
        /// The tile picker lists every tile the machine supports, which is several times
        /// what a flyout can show. Uncapped, the card measured taller than the work area,
        /// and because it is anchored to the bottom of the screen the excess ran off the
        /// top - the list appeared clipped at the top and parked at its last entry, with
        /// no way to reach the first.
        /// </remarks>
        private void FitDetailList()
        {
            // Measured unconstrained first, so the header's real height is known before
            // the remaining room is handed to the list.
            DetailScroll.MaxHeight = double.PositiveInfinity;
            DetailView.InvalidateMeasure();
            DetailView.Measure(new System.Windows.Size(
                Math.Max(1, TransitionHost.ActualWidth), double.PositiveInfinity));

            double chrome = PanelChrome.Padding.Top + PanelChrome.Padding.Bottom
                + PanelChrome.BorderThickness.Top + PanelChrome.BorderThickness.Bottom;
            DetailScroll.MaxHeight = Math.Max(160, MaxCardHeight() - chrome - DetailHeader.DesiredSize.Height);
        }

        /// <summary>
        /// Grows the transparent window when the card no longer fits inside it. It never
        /// shrinks: the card is bottom-aligned within the window, so spare room above it
        /// is invisible, while too little clips whatever is on screen.
        /// </summary>
        private void EnsureViewportFits(double cardHeight)
        {
            double needed = cardHeight + MotionRoot.Margin.Top + MotionRoot.Margin.Bottom;
            if (needed <= _nativeViewportHeight) return;

            _nativeViewportHeight = needed;
            Height = needed;
            PositionNearTray();
        }

        /// <summary>
        /// Re-fits the card after the tile grid gains or loses a row.
        /// </summary>
        /// <remarks>
        /// <see cref="LockNativeViewport"/> pins the card and the transparent window to
        /// the height the tile page measured on the first open, which is what keeps the
        /// detail transition from resizing the HWND. A tile added or removed is the one
        /// thing that legitimately changes that height, so the pinned values are
        /// recomputed here - and the window is allowed to grow, though never to shrink,
        /// because the card is bottom-aligned inside it and a window left slightly tall
        /// is invisible while one left short clips the grid.
        /// </remarks>
        private void RemeasureMainView()
        {
            if (_nativeViewportHeight <= 0) return;
            if (_viewTransitionRunning || _detailShown)
            {
                // Not now: a transition owns both heights while it runs, and the tile page
                // is not the one on screen anyway. It gets re-fitted on the way back.
                _remeasureWhenSettled = true;
                return;
            }
            if (TransitionHost.ActualWidth <= 0) return;

            _remeasureWhenSettled = false;

            double padding = Math.Max(0, PanelChrome.ActualHeight - TransitionHost.ActualHeight);

            FitMainView();
            MainView.InvalidateMeasure();
            MainView.Measure(new System.Windows.Size(TransitionHost.ActualWidth, double.PositiveInfinity));
            _mainHostHeight = Math.Max(1, MainView.DesiredSize.Height);
            _mainChromeHeight = _mainHostHeight + padding;

            PanelChrome.BeginAnimation(HeightProperty, null);
            TransitionHost.BeginAnimation(HeightProperty, null);
            TransitionHost.Height = _mainHostHeight;
            PanelChrome.Height = _mainChromeHeight;

            EnsureViewportFits(_mainChromeHeight);
            UpdateLayout();
            PositionNearTray();
        }

        /// <summary>How far back along its entrance the card starts, in DIPs.</summary>
        private const double EnterDistance = 54;

        /// <summary>How far back the card retreats on the way out. Shorter than it came.</summary>
        private const double ExitDistance = 40;

        /// <summary>
        /// Unit vector pointing at the taskbar: the direction the card arrives from and
        /// leaves towards. Set with the placement, so one ease drives whichever axis the
        /// current taskbar edge calls for.
        /// </summary>
        private double _enterX;
        private double _enterY = 1;

        /// <summary>Current distance back along that vector, in DIPs.</summary>
        private double _panelOffset;

        private void SetPanelOffset(double distance)
        {
            _panelOffset = distance;
            PanelTransform.X = _enterX * distance;
            PanelTransform.Y = _enterY * distance;
        }

        public void ShowAnimated()
        {
            _targetVisible = true;
            _shownAt = DateTime.UtcNow;
            _deactivateDismissedAt = DateTime.MinValue;

            _panelFade.Stop();
            _panelSlide.Stop();
            Opacity = 0;

            // Placed and measured at rest, so neither the entrance offset nor a previous
            // exit can be mistaken for the card's own geometry.
            SetPanelOffset(0);
            Show();

            // Read the taskbar first: which edge it is on decides which edge of the
            // window the card is glued to, and that has to be settled before the
            // viewport below is measured and pinned.
            CaptureTrayAnchor();
            MainView.ScrollToTop();

            // Re-fit on every open because the monitor or its scale may have changed
            // while the panel was hidden. The resulting viewport remains fixed for the
            // visible session, so transitions cannot make the tray anchor tremble.
            UpdateLayout();
            LockNativeViewport();
            PositionNearTray();

            // Render-only, so it costs no layout and cannot disturb the placement above.
            SetPanelOffset(EnterDistance);
            TakeForeground();

            _panelFade.Start(0, 1, 160, Controls.FrameEase.QuinticOut, v => Opacity = v);
            _panelSlide.Start(EnterDistance, 0, 220, Controls.FrameEase.QuinticOut, SetPanelOffset);
        }

        private readonly Controls.FrameEase _panelFade = new();
        private readonly Controls.FrameEase _panelSlide = new();

        public void HideAnimated(Action? completed = null)
        {
            _targetVisible = false;
            if (!IsVisible)
            {
                completed?.Invoke();
                return;
            }

            double currentOpacity = Opacity;
            double currentOffset = _panelOffset;
            _panelFade.Stop();
            _panelSlide.Stop();
            Opacity = currentOpacity;
            SetPanelOffset(currentOffset);

            // Kept short: the panel is a layered window, so each frame costs a full
            // surface copy and a long exit reads as the window lagging behind the click.
            _panelFade.Start(currentOpacity, 0, 120, Controls.FrameEase.QuarticIn, v => Opacity = v);
            _panelSlide.Start(
                currentOffset,
                ExitDistance,
                140,
                Controls.FrameEase.QuarticIn,
                SetPanelOffset,
                completed: () =>
                {
                    if (!_targetVisible)
                    {
                        Hide();
                        // Leave the detail page and edit mode behind so the panel always
                        // opens on the plain tile grid, and snap the views back while
                        // hidden rather than animating them.
                        if (DataContext is QuickPanelViewModel vm) vm.ReturnToRest();
                        ResetToMainView();
                    }
                    Opacity = 1;
                    SetPanelOffset(0);
                    completed?.Invoke();
                });
        }

        public void HideForModal()
        {
            if (!IsVisible) return;

            var frame = new DispatcherFrame();
            var fallback = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
            fallback.Tick += (_, _) =>
            {
                fallback.Stop();
                frame.Continue = false;
            };
            HideAnimated(() => frame.Continue = false);
            fallback.Start();
            Dispatcher.PushFrame(frame);
        }

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
