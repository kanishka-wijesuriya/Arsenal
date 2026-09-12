using Arsenal.Application.Extensions;
using Arsenal.Application.Services.Contracts;
using Arsenal.AutoUpdate;
using Arsenal.Battery;
using Arsenal.Display;
using Arsenal.Gpu;
using Arsenal.Helpers;
using Arsenal.Peripherals;
using Arsenal.UI.ViewModels;
using Arsenal.UI.Services;
using Arsenal.UI.Services.Remote;
using Arsenal.UI.Views.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;
using WpfUi = Wpf.Ui.Appearance;

namespace Arsenal.UI
{
    public partial class App : System.Windows.Application, IUiBridge
    {
#if ARSENAL_STORE
        public const bool CanSelfUpdate = false;
#else
        public const bool CanSelfUpdate = true;
#endif

        public static IServiceProvider Services { get; private set; } = null!;
        public static Forms.NotifyIcon? TrayIcon { get; private set; }

        private MainWindow? _mainWindow;
        private QuickPanelWindow? _quickPanelWindow;
        private TrayMenuWindow? _trayMenuWindow;
        private AutoUpdateControl? _autoUpdateControl;
        /// <summary>Held when the feed finds a release while the window is hidden.</summary>
        private ReleaseUpdate? _pendingUpdate;
        private bool _hardwareInitialized;
        private string? _latestModeLabel;
        private string? _latestUpdateStatus;
        private string? _latestCalibrationStatus;
        private bool? _latestAsusOptimizationState;
        private DateTime _trayLeftPressAt = DateTime.MinValue;
        private DateTime _lastTrayLeftClickAt = DateTime.MinValue;
        private static readonly TimeSpan TrayLeftPressWindow = TimeSpan.FromMilliseconds(700);
        private static readonly TimeSpan TrayLeftClickDebounce = TimeSpan.FromMilliseconds(320);
        private readonly System.Timers.Timer _powerSettleTimer = new() { AutoReset = false };

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            AppDomain.CurrentDomain.UnhandledException += (s, ev) => Logger.WriteLine("Unhandled: " + ev.ExceptionObject);
            DispatcherUnhandledException += (s, ev) => { Logger.WriteLine("Dispatcher Unhandled: " + ev.Exception); ev.Handled = true; };

            ApplyConfiguredCulture();
            string action = e.Args.FirstOrDefault()?.Trim().ToLowerInvariant() ?? string.Empty;

            if (action == "--install-colors")
            {
                ColorProfileHelper.InstallProfileFilesAsync().GetAwaiter().GetResult();
                Shutdown();
                return;
            }

            if (action == "--allow-companion-network")
            {
                Environment.ExitCode = CompanionFirewall.AllowPrivateNetwork(persist: false) ? 0 : 1;
                Shutdown();
                return;
            }

            // The executable itself starts normally. Elevation is an explicit,
            // persisted setup/settings choice, so only relaunch after that choice has
            // been made. A cancelled UAC prompt leaves this launch running normally.
            if (AppConfig.Is("run_as_admin") && !ProcessHelper.IsUserAdministrator() &&
                action is not "charge")
            {
                string elevatedAction = action.Length == 0 ? "--elevated" : string.Join(' ', e.Args);
                if (ProcessHelper.RunAsAdmin(elevatedAction))
                {
                    Shutdown();
                    return;
                }
            }

            Logger.WriteLine("----------------------");
            Logger.WriteLine("Arsenal Launch: " + AppConfig.GetModel() + " " +
                Assembly.GetExecutingAssembly().GetName().Version +
                " (Admin: " + ProcessHelper.IsUserAdministrator() + ")");

            // "charge" is the scheduled-task action: it applies the limit and exits, and
            // must not touch instance management at all. It used to fall through to
            // CheckAlreadyRunning, which kills every other Arsenal process - so the
            // logon charge task was killing the running app.
            bool backgroundAction = action is "charge" or "--allow-companion-network";
            bool replaceExisting = e.Args.Contains(ProcessHelper.ReplaceArgument);

            if (!backgroundAction)
            {
                // An ordinary second launch should raise the window that is already
                // there rather than restarting the app.
                if (!replaceExisting && ProcessHelper.TryActivateExistingInstance())
                {
                    Shutdown();
                    return;
                }

                ProcessHelper.ExitRequested += OnExternalExitRequested;
                // Always subscribe to the exit broadcast so a replacing launch can stop
                // this instance cleanly; only actually take over when asked to.
                ProcessHelper.CheckAlreadyRunning(takeOver: replaceExisting);

                ProcessHelper.ShowRequested += () => Dispatcher.Invoke(ShowMainWindow);
                ProcessHelper.ListenForActivation();
            }

            var serviceCollection = new ServiceCollection();
            ConfigureServices(serviceCollection);
            Services = serviceCollection.BuildServiceProvider();

            // Set UI Bridge
            Program.Bridge = this;

            ApplyConfiguredTheme();

            // Initialize Hardware Core
            InitHardwareCore();

            // Keep the original task-scheduler action fast and non-interactive.
            if (action == "charge")
            {
                BatteryControl.SetBatteryChargeLimit();
                try { Input.InputDispatcher.StartupBacklight(); }
                catch (Exception ex) { Logger.WriteLine("Startup Backlight: " + ex.Message); }
                Shutdown();
                return;
            }

            // Initialize System Tray
            InitTrayIcon();

            // Start the bridge before views bind SettingsViewModel, so the address,
            // pairing code and certificate identity render correctly on first open.
            Services.GetRequiredService<RemoteCompanionService>().Start();

            // Keep the sensor snapshot warm at the same low tray cadence as before.
            // It is an application service, not a reason to retain Quick Panel/Home UI.
            Services.GetRequiredService<IDeviceStateService>().PausePolling();

            // Windows are intentionally created on first use. A normal logon launch
            // spends most of its life in the tray, so constructing three complete WPF
            // trees here retained their controls, bindings and render resources before
            // the user had opened any of them. OnExplicitShutdown keeps the tray process
            // alive safely even when no native window has been created yet.

            // Show main window on start unless launched with --minimized / --startup
            bool startMinimized = e.Args.Contains("--minimized") || e.Args.Contains("--startup") || e.Args.Contains("-m");
            if (!startMinimized && action is not ("--quick" or "--quick-test" or "--tray-test"))
            {
                ShowMainWindow();
            }

            // Never on an automated launch: setup is modal and would block a logon
            // start or a scheduled action behind a dialog nobody is there to answer.
            if (!startMinimized && (action is "" or "--elevated" or "--setup") &&
                (action == "--setup" || SetupViewModel.IsSetupNeeded))
            {
                ShowSetup();
            }

            if (action is "cpu" or "gpu" or "uv")
            {
                EnsureMainWindow().NavigateToTag("Performance");
            }
            else if (action == "services")
            {
                EnsureMainWindow().NavigateToTag("Advanced");
            }
            else if (action == "--settings")
            {
                EnsureMainWindow().NavigateToTag("Settings");
            }
            else if (action is "--stop-asus-services" or "--start-asus-services")
            {
                EnsureMainWindow().NavigateToTag("Advanced");
                bool shouldRun = action == "--start-asus-services";
                Dispatcher.BeginInvoke(async () =>
                    await Services.GetRequiredService<AdvancedViewModel>().SetAsusServicesRunning(shouldRun));
            }
            else if (action == "colors")
            {
                EnsureMainWindow().NavigateToTag("Display");
                _ = Task.Run(Display.ColorProfileHelper.InstallProfile);
            }
            else if (action is "--quick" or "--quick-test")
            {
                ToggleQuickPanel();
            }
            else if (action == "--tray-test")
            {
                ToggleTrayMenu();
            }

            if (CanSelfUpdate && (AppConfig.IsNotFalse("check_updates") || action == "autoupdate"))
            {
                _autoUpdateControl = new AutoUpdateControl();
                _autoUpdateControl.UpdateAvailable += PromptForApplicationUpdate;
                _autoUpdateControl.CheckForUpdates();
            }

            if (AppConfig.IsOverlay()) Program.hardwareOverlay?.StartOverlay();

            Task.Run(StartDeferredHardwareWork);
        }

        /// <summary>
        /// Shows the in-window update card for a release the signed feed reported.
        ///
        /// The card lives inside the main window, so a check that lands while Arsenal
        /// is sitting in the tray has nowhere to draw it. Rather than pulling the
        /// window up over whatever the user is doing - which is exactly what the old
        /// message box did - the release is held and the card is shown the next time
        /// the window is opened, with a toast to say it is waiting.
        /// </summary>
        private void PromptForApplicationUpdate(ReleaseUpdate release)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_mainWindow?.IsUpdateCardOpen == true) return;

                if (_mainWindow?.IsVisible == true)
                {
                    _mainWindow.ShowApplicationUpdate(release);
                    return;
                }

                _pendingUpdate = release;
                Program.Bridge?.ShowToast(
                    AppStrings.Get("UpdateReady"), ToastIcon.Charger, $"Arsenal {release.Version}");
            });
        }

        /// <summary>Shows a held release once the window the card needs actually exists.</summary>
        private void ShowPendingUpdateIfAny()
        {
            if (_pendingUpdate is null || _mainWindow?.IsVisible != true || _mainWindow.IsUpdateCardOpen) return;
            ReleaseUpdate release = _pendingUpdate;
            _pendingUpdate = null;
            _mainWindow.ShowApplicationUpdate(release);
        }

        /// <summary>
        /// Shown once on first run, and on demand from Settings.
        /// </summary>
        /// <summary>
        /// Re-applies the keyboard backlight level, which is also what marks the
        /// backlight as on inside Aura.
        ///
        /// This matters beyond brightness: Heatmap, Ambient, Audio Spectrum and Audio
        /// Pulse all write colours directly and begin with a check on that flag, so
        /// while it is false they run and silently produce nothing. The flag starts
        /// false and the keyboard inactivity timeout sets it false again, so without
        /// this call those four modes only work in the window after someone happens to
        /// press a brightness key. The original calls it from SetAutoModes, at startup
        /// and on every power change.
        /// </summary>
        private static void ApplyKeyboardBacklight()
        {
            try
            {
                if (AppConfig.IsAlly()) Program.allyControl?.Init();
                else Input.InputDispatcher.AutoKeyboard();
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Keyboard backlight: " + ex.Message);
            }
        }

        public void ShowSetup()
        {
            try
            {
                // An in-window panel, so the main window has to be up to host it.
                ShowMainWindow();
                _mainWindow!.ShowSetup();
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Setup panel: " + ex.Message);
            }
        }

        private static void ApplyConfiguredCulture()
        {
            string language = AppConfig.GetString("language");
            if (string.IsNullOrWhiteSpace(language)) return;
            try
            {
                var culture = CultureInfo.GetCultureInfo(language);
                CultureInfo.CurrentUICulture = culture;
                Thread.CurrentThread.CurrentUICulture = culture;
            }
            catch (CultureNotFoundException)
            {
                Logger.WriteLine("Unknown language: " + language);
            }
        }

        public static void ApplyConfiguredTheme()
        {
            try
            {
                bool light = IsConfiguredLightTheme();
                WpfUi.ApplicationThemeManager.Apply(light ? WpfUi.ApplicationTheme.Light : WpfUi.ApplicationTheme.Dark);

                if (Current != null)
                {
                    Current.Resources["SurfaceBase"] = Brush(light ? "#F5F5F5" : "#171819");
                    Current.Resources["SurfaceLayer"] = Brush(light ? "#FAFAFA" : "#1D1F20");
                    Current.Resources["SurfaceCard"] = Brush(light ? "#FFFFFFFF" : "#252728");
                    Current.Resources["SurfaceCardHover"] = Brush(light ? "#FFF3F3F3" : "#2C2F30");
                    Current.Resources["SurfaceSunken"] = Brush(light ? "#FFEFEFEF" : "#141516");
                    Current.Resources["StrokeSubtle"] = Brush(light ? "#18000000" : "#2AFFFFFF");
                    Current.Resources["StrokeDivider"] = Brush(light ? "#12000000" : "#18FFFFFF");
                    Current.Resources["TextPrimary"] = Brush(light ? "#1A1A1A" : "#F4F4F4");
                    Current.Resources["TextSecondary"] = Brush(light ? "#5B5B5B" : "#B6B8BA");
                    Current.Resources["TextTertiary"] = Brush(light ? "#777777" : "#86898C");

                    // Status colours are tuned for a dark ground; darken them for light
                    // mode so warning and error text stays readable on white cards.
                    Current.Resources["StatusSuccess"] = Brush(light ? "#1E7A26" : "#6CCB5F");
                    Current.Resources["StatusWarning"] = Brush(light ? "#9A6700" : "#F2C94C");
                    Current.Resources["StatusCritical"] = Brush(light ? "#C42B1C" : "#FF6B6B");

                    Current.Resources["DividerBrush"] = FadedLine(light ? "#24000000" : "#26FFFFFF");
                    ApplyNavigationForeground();
                    ApplyAccentResources(light);
                    ApplyWindowSurfaces(light);
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Theme apply error: " + ex.Message);
            }
        }

        /// <summary>
        /// Config key. 1 turns the Mica backdrop off for this app only and paints the
        /// window on flat colours instead.
        /// </summary>
        public const string OpaqueWindowSetting = "opaque_window";

        /// <summary>
        /// Whether to paint on flat colours rather than let Mica through. On unless the
        /// key says otherwise, so a fresh install opens opaque.
        /// </summary>
        /// <remarks>
        /// Read through IsNotFalse rather than Is, which is what makes an absent key
        /// mean opaque. Anyone who has turned transparency on has a stored zero and
        /// keeps it; only machines that never touched the setting change behaviour.
        /// </remarks>
        public static bool IsOpaqueWindow => AppConfig.IsNotFalse(OpaqueWindowSetting);

        /// <summary>
        /// Paints the window's two grounds and switches the backdrop to match.
        /// </summary>
        /// <remarks>
        /// Transparent grounds let Mica through, which is the default. Turning the
        /// setting on has to do both halves: flat brushes alone would still be composited
        /// over a Mica backdrop, and dropping the backdrop alone would leave the window
        /// painting on whatever the bare FluentWindow ground happens to be.
        ///
        /// The dark values are the ones asked for. Light mode gets the equivalents from
        /// the existing light palette rather than those same near-black colours.
        /// </remarks>
        /// <summary>
        /// The library's own foregrounds for a navigation destination, in the order the
        /// template reaches for them. Every state resolves to the same ink.
        /// </summary>
        private static readonly string[] NavigationForegroundKeys =
        {
            "NavigationViewItemForeground",
            "NavigationViewItemForegroundPointerOver",
            "NavigationViewItemForegroundPressed",
            "NavigationViewItemForegroundLeftFluent",
            "NavigationViewItemForegroundPointerOverLeftFluent",
        };

        /// <summary>
        /// Paints the navigation column's ink: the destinations in the pane, and the
        /// title-bar icons that sit in the same gutter.
        /// </summary>
        /// <remarks>
        /// Written straight onto the application dictionary, and after the library's
        /// theme has been applied. ApplicationThemeManager.Apply appends its own
        /// dictionary last, so the same key merged in from one of ours would be the
        /// losing entry - the same reason NavigationViewContentBackground is set from
        /// here rather than declared in DesignTokens.xaml.
        ///
        /// One colour in every state. A selected destination is already marked by its
        /// pill and its indicator, so recolouring its text as well would say the same
        /// thing twice; hover and press keep their wash behind the item.
        ///
        /// The same ink in both themes, which is what was asked for. It is a light
        /// warm grey, so it is quiet against the light theme's pane.
        /// </remarks>
        private static void ApplyNavigationForeground()
        {
            if (Current is null) return;

            SolidColorBrush ink = Brush("#B1B1A9");
            Current.Resources["NavigationForeground"] = ink;
            foreach (string key in NavigationForegroundKeys)
                Current.Resources[key] = ink;
        }

        private static void ApplyWindowSurfaces(bool light)
        {
            if (Current is null) return;

            bool opaque = IsOpaqueWindow;

            System.Windows.Media.Brush sidebar = opaque
                ? Brush(light ? "#F3F3F3" : "#111111")
                : System.Windows.Media.Brushes.Transparent;

            // With Mica on, the navigation ground stays fully transparent so the backdrop
            // reads through it. The content ground takes a thin wash instead of nothing:
            // the two grounds have to differ for the content area to have a visible shape
            // at all, and its top-left corner rounding under the title bar is the whole
            // point of the collapsed layout. This is the same layer-over-backdrop the
            // Windows 11 shell uses, not a solid fill.
            System.Windows.Media.Brush content = opaque
                ? Brush(light ? "#FAFAFA" : "#151515")
                : Brush(light ? "#5AFFFFFF" : "#12FFFFFF");

            Current.Resources["AppSidebarBackground"] = sidebar;
            Current.Resources["AppContentBackground"] = content;

            // The NavigationView template lays its own wash over the whole content area -
            // NavigationViewContentBackground, a 30% #3A3A3A. It sits above the grounds
            // above, so the content area rendered #202020 instead of #151515, and the
            // title-bar strip #1D1D1D instead of the navigation colour. We paint that
            // ground ourselves, so the control's own is removed rather than fought with.
            // Set on the application dictionary, which is what the template's lookup
            // actually reaches; a value merged in from a child dictionary does not win.
            Current.Resources["NavigationViewContentBackground"] = System.Windows.Media.Brushes.Transparent;

            if (Current.MainWindow is Wpf.Ui.Controls.FluentWindow window)
                ApplyWindowBackdrop(window);
        }

        /// <summary>
        /// Puts one window's backdrop and ground in step with the transparency setting.
        /// </summary>
        /// <remarks>
        /// Called from the window's own constructor as well as from a theme refresh.
        /// The theme is applied during startup, before any window exists, so a window
        /// created later would otherwise keep the Mica backdrop its XAML asks for even
        /// when the user has turned transparency off.
        /// </remarks>
        public static void ApplyWindowBackdrop(Wpf.Ui.Controls.FluentWindow window)
        {
            bool opaque = IsOpaqueWindow;

            window.WindowBackdropType = opaque
                ? Wpf.Ui.Controls.WindowBackdropType.None
                : Wpf.Ui.Controls.WindowBackdropType.Mica;

            // The grounds cover the whole client area, so this only shows during a
            // resize - but leaving it transparent there flickers through to the desktop
            // once the backdrop is gone.
            window.Background = opaque
                ? (Current?.Resources["AppContentBackground"] as System.Windows.Media.Brush
                    ?? System.Windows.Media.Brushes.Black)
                : System.Windows.Media.Brushes.Transparent;

            ApplyCompositionGround(window, opaque);
            ApplyImmersiveDarkMode(window);
        }

        /// <summary>
        /// Tells DWM whether this window is a dark one.
        /// </summary>
        /// <remarks>
        /// DWM draws the frame around the client area itself, including the strip it
        /// exposes while a window is being resized, and it picks those colours from this
        /// flag rather than from anything the application paints. Left unset the window
        /// is treated as light, so that strip is drawn light - the white edge that
        /// appears on whichever side is being dragged.
        ///
        /// The library sets it as part of applying a backdrop, which is why the setting
        /// had to be toggled after launch to take effect: switching to Mica applied it,
        /// and switching back to opaque left it applied. Starting opaque never applied
        /// it at all. Setting it here means the state after launch matches the state
        /// after a toggle.
        /// </remarks>
        private static void ApplyImmersiveDarkMode(Window window)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            int dark = IsConfiguredLightTheme() ? 0 : 1;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        }

        private const int DwmwaUseImmersiveDarkMode = 20;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>
        /// The colour WPF clears this window's composition target to, which is what a
        /// resize uncovers before anything has been drawn into the new space.
        /// </summary>
        /// <remarks>
        /// WPF's default here is opaque white, and it stays white with the backdrop off:
        /// the Mica path is the only one the library makes transparent, because there the
        /// point is to let DWM through. So turning transparency off left the clear colour
        /// white, and dragging an edge outwards showed it along that edge until the next
        /// frame caught up - the whole of the reported white band.
        ///
        /// The content ground is the right colour to use: the sidebar's differs by four
        /// values, which is not visible, and the content is by far the larger area.
        ///
        /// Note this is the render target's own clear colour, not a paint. Content draws
        /// over it exactly as before, so nothing is drawn twice. Filling the client area
        /// through GDI to achieve the same thing does double-paint, and flickers.
        /// </remarks>
        private static void ApplyCompositionGround(Window window, bool opaque)
        {
            // Only exists once the window has a handle, so this is a no-op when called
            // from a constructor; the window re-applies it from OnSourceInitialized.
            if (PresentationSource.FromVisual(window) is not HwndSource source) return;
            if (source.CompositionTarget is not { } target) return;

            target.BackgroundColor = opaque
                ? (Current?.Resources["AppContentBackground"] as System.Windows.Media.SolidColorBrush)?.Color
                    ?? System.Windows.Media.Colors.Black
                : System.Windows.Media.Colors.Transparent;
        }

        /// <summary>
        /// Refreshes only accent resources. The Settings colour picker uses this path
        /// while dragging so it does not reload the full light/dark theme on every move.
        /// </summary>
        public static void ApplyConfiguredAccent()
        {
            try
            {
                if (Current != null) ApplyAccentResources(IsConfiguredLightTheme());
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Accent apply error: " + ex.Message);
            }
        }

        private static bool IsConfiguredLightTheme()
        {
            int theme = AppConfig.Get("theme", 0);
            bool systemLight = Convert.ToInt32(Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 0)) != 0;
            return theme == 2 || (theme == 0 && systemLight);
        }

        private static void ApplyAccentResources(bool light)
        {
            System.Windows.Media.Color configuredAccent = AccentColorService.GetConfiguredAccent();
            bool followsWindows = AccentColorService.UsesWindowsAccent;
            WpfUi.ApplicationTheme applicationTheme = light ? WpfUi.ApplicationTheme.Light : WpfUi.ApplicationTheme.Dark;

            // WPF UI owns the native-looking controls; Arsenal owns the surrounding
            // cards, tiles and charts. Apply once to WPF UI, then reuse its most-used
            // derived shade so the two layers never drift into different accent hues.
            WpfUi.ApplicationAccentColorManager.Apply(
                configuredAccent,
                applicationTheme,
                systemGlassColor: followsWindows,
                systemAccentColor: followsWindows);

            System.Windows.Media.Color accent = WpfUi.ApplicationAccentColorManager.SecondaryAccent;
            if (accent.A == 0)
                accent = configuredAccent;
            else
                accent = System.Windows.Media.Color.FromRgb(accent.R, accent.G, accent.B);

            System.Windows.Media.Color card = light
                ? System.Windows.Media.Colors.White
                : System.Windows.Media.Color.FromRgb(0x25, 0x27, 0x28);
            System.Windows.Media.Color foreground = AccentColorService.ContrastingText(accent);
            System.Windows.Media.Color subtle = AccentColorService.WithAlpha(accent, light ? (byte)0x20 : (byte)0x2E);
            System.Windows.Media.Color selected = AccentColorService.Blend(card, accent, light ? 0.14 : 0.20);

            Current.Resources["AccentPrimaryColor"] = accent;
            Current.Resources["AccentForegroundColor"] = foreground;
            Current.Resources["AccentSubtleColor"] = subtle;
            Current.Resources["SurfaceSelectedColor"] = selected;
            Current.Resources["AccentPrimary"] = Brush(accent);
            Current.Resources["AccentForeground"] = Brush(foreground);
            Current.Resources["AccentSubtle"] = Brush(subtle);
            Current.Resources["SurfaceSelected"] = Brush(selected);
        }

        private static SolidColorBrush Brush(string hex)
        {
            var brush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        private static SolidColorBrush Brush(System.Windows.Media.Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// A hairline that fades out at both ends, used to separate settings rows.
        /// </summary>
        private static System.Windows.Media.LinearGradientBrush FadedLine(string hex)
        {
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
            var transparent = System.Windows.Media.Color.FromArgb(0, color.R, color.G, color.B);

            var brush = new System.Windows.Media.LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0, 0),
                EndPoint = new System.Windows.Point(1, 0)
            };
            brush.GradientStops.Add(new System.Windows.Media.GradientStop(transparent, 0));
            brush.GradientStops.Add(new System.Windows.Media.GradientStop(color, 0.06));
            brush.GradientStops.Add(new System.Windows.Media.GradientStop(color, 0.94));
            brush.GradientStops.Add(new System.Windows.Media.GradientStop(transparent, 1));
            brush.Freeze();
            return brush;
        }

        private void ConfigureServices(IServiceCollection services)
        {
            // Register Application layer services
            services.AddArsenalApplicationServices();

            // Register ViewModels
            AddTrackedSingleton<MainViewModel>(services);
            AddTrackedSingleton<QuickPanelViewModel>(services);
            AddTrackedSingleton<HomeViewModel>(services);
            AddTrackedSingleton<PerformanceViewModel>(services);
            AddTrackedSingleton<DisplayViewModel>(services);
            AddTrackedSingleton<BatteryViewModel>(services);
            AddTrackedSingleton<LightingViewModel>(services);
            AddTrackedSingleton<DevicesViewModel>(services);
            AddTrackedSingleton<AutomationViewModel>(services);
            AddTrackedSingleton<UpdatesViewModel>(services);
            AddTrackedSingleton<AdvancedViewModel>(services);
            AddTrackedSingleton<MobileCompanionViewModel>(services);
            AddTrackedSingleton<SettingsViewModel>(services);
            AddTrackedSingleton<AboutViewModel>(services);
            services.AddSingleton<RemoteCompanionService>();

            // Register Windows
            services.AddSingleton<MainWindow>();
            services.AddSingleton<QuickPanelWindow>();

            services.AddSingleton<TrayMenuWindow>();
        }

        /// <summary>
        /// Microsoft.Extensions.DependencyInjection already creates singletons on demand,
        /// but the hardware bridge used to resolve every page view model just to update a
        /// property. Keeping the Lazy wrapper registered lets bridge notifications update
        /// an existing page without constructing pages the user has never opened.
        /// </summary>
        private void AddTrackedSingleton<T>(IServiceCollection services) where T : class
        {
            services.AddSingleton(provider => new Lazy<T>(() =>
            {
                T viewModel = ActivatorUtilities.CreateInstance<T>(provider);
                ApplyPendingBridgeState(viewModel);
                return viewModel;
            }, LazyThreadSafetyMode.ExecutionAndPublication));
            services.AddSingleton(provider => provider.GetRequiredService<Lazy<T>>().Value);
        }

        private static T? GetCreatedViewModel<T>() where T : class
        {
            Lazy<T>? lazy = Services.GetService<Lazy<T>>();
            return lazy is { IsValueCreated: true } ? lazy.Value : null;
        }

        private void ApplyPendingBridgeState<T>(T viewModel) where T : class
        {
            switch (viewModel)
            {
                case MainViewModel main when _latestModeLabel is not null:
                    main.CurrentModeName = _latestModeLabel;
                    break;
                case AboutViewModel about when _latestUpdateStatus is not null:
                    about.UpdateStatus = _latestUpdateStatus;
                    break;
                case PerformanceViewModel performance when _latestCalibrationStatus is not null:
                    performance.CalibrationStatus = _latestCalibrationStatus;
                    break;
                case AdvancedViewModel advanced when _latestAsusOptimizationState.HasValue:
                    advanced.AsusOptimizationRunning = _latestAsusOptimizationState.Value;
                    break;
            }
        }

        private void InitHardwareCore()
        {
            try
            {
                ProcessHelper.SetPriority();
                Program.InitCoreHardware();
                Program.currentSource = Program.ReadPowerSource();

                ProcessHelper.KillSmartDisplayControl();
                AsusService.StopOnStartup();
                ScreenControl.InitScreen();

                Program.inputDispatcher?.Init();
                Program.clamshellControl.RegisterDisplayEvents();
                Program.clamshellControl.ToggleLidAction();

                BatteryControl.AutoBattery(true);
                Input.InputDispatcher.InitScreenpad();
                Input.InputDispatcher.InitStatusLed();
                Input.NumberPad.Init();
                USB.XGM.Init();
                ApplyKeyboardBacklight();
                ScreenControl.InitMiniled();
                VisualControl.InitBrightness();

                Input.InputDispatcher.OnToggleApp += () => Dispatcher.Invoke(HandleHardwareButtonPress);

                // Seed the source before subscribing, so the first real transition is
                // recognised as one instead of being compared against a default.
                Program.currentSource = Program.ReadPowerSource();
                _powerSettleTimer.Elapsed += OnPowerSettled;

                // The ACPI charger event is usually the first notice of a plug change.
                // It shares the settle timer with PowerModeChanged, so the two sources
                // coalesce into a single check rather than each acting on their own.
                Input.InputDispatcher.OnChargerEvent += SchedulePowerCheck;

                SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;
                SystemEvents.SessionSwitch += SystemEvents_SessionSwitch;
                SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;

                PeripheralsProvider.RegisterForDeviceEvents();
                _hardwareInitialized = true;
            }
            catch (Exception ex)
            {
                Logger.WriteLine("InitHardwareCore error: " + ex.Message);
            }
        }

        private static void StartDeferredHardwareWork()
        {
            try
            {
                // InitCoreHardware already creates the native GPU provider. Only retry
                // here when the first probe found no supported provider; recreating a
                // healthy provider immediately retained an avoidable native allocation
                // spike during startup and needlessly reopened driver resources.
                if (HardwareControl.GpuControl is null)
                    HardwareControl.RecreateGpuControl();
                Program.gpuControl.CaptureNvBootState();
                Program.modeControl.AutoPerformance();
                if (!Program.gpuControl.AutoGPUMode(delay: 1000))
                {
                    Program.gpuControl.InitGPUMode();
                    ScreenControl.AutoScreen();
                }
                PeripheralsProvider.DetectAllAsusMice();
                PeripheralsProvider.DetectAllAsusKeyboards();
                global::Startup.StartupCheck();
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Deferred startup error: " + ex.Message);
            }
        }

        private void SystemEvents_PowerModeChanged(object? sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend)
            {
                GPUModeControl.suspended = true;
                Program.gpuControl?.StandardModeFix();
                Program.modeControl?.ShutdownReset();
                Input.InputDispatcher.ShutdownStatusLed();
                USB.XGM.NotifyShutdown();
                return;
            }

            if (e.Mode == PowerModes.Resume)
                GPUModeControl.suspended = false;

            SchedulePowerCheck();
        }

        /// <summary>
        /// Windows raises PowerModeChanged several times for a single plug or unplug.
        /// Restarting a one-shot timer collapses that burst into one check, the way the
        /// original's powerSettleTimer does - without it every event queued its own
        /// delayed task and each one re-applied the profile and raised its own toast.
        /// </summary>
        private void SchedulePowerCheck()
        {
            if (AppConfig.Is("disable_power_event")) return;
            _powerSettleTimer.Interval = Math.Max(AppConfig.Get("charger_delay"), 2000);
            _powerSettleTimer.Stop();
            _powerSettleTimer.Start();
        }

        private void OnPowerSettled(object? sender, System.Timers.ElapsedEventArgs e)
        {
            // Second guard, also from the original: act only on a real transition. A
            // burst that settles back to the same source is not a charger change.
            var source = Program.ReadPowerSource();
            if (source == Program.currentSource) return;

            Logger.WriteLine($"Power source: {Program.currentSource} -> {source}");
            Program.currentSource = source;

            BatteryControl.AutoBattery();
            if (Services.GetRequiredService<IProfileService>().IsAutoSwitchEnabled)
                Program.modeControl?.AutoPerformance(true);
            ApplyKeyboardBacklight();
            Program.gpuControl?.AutoGPUMode(delay: 1000);
            ScreenControl.AutoScreen();
        }

        private static void SystemEvents_SessionSwitch(object? sender, SessionSwitchEventArgs e)
        {
            if (e.Reason is SessionSwitchReason.SessionLogon or SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect)
            {
                Helpers.ProcessHelper.KillSmartDisplayControl();
                USB.Aura.sessionLock = false;
                USB.Aura.ApplyAura();
                ScreenControl.AutoScreen();
            }
            else if (e.Reason == SessionSwitchReason.SessionLock)
            {
                USB.Aura.sessionLock = true;
            }
        }

        private void SystemEvents_UserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
        {
            bool relevantCategory = e.Category is UserPreferenceCategory.General
                or UserPreferenceCategory.Color
                or UserPreferenceCategory.VisualStyle;
            bool followsWindowsTheme = AppConfig.Get("theme", 0) == 0;
            if (relevantCategory && (followsWindowsTheme || AccentColorService.UsesWindowsAccent))
                Dispatcher.Invoke(ApplyConfiguredTheme);
        }

        private void OnExternalExitRequested()
        {
            Dispatcher.BeginInvoke(new Action(ExitApplication));
        }

        private void InitTrayIcon()
        {
            TrayIcon = new Forms.NotifyIcon
            {
                Text = "Arsenal",
                Visible = true
            };

            try
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "arsenal.ico");
                if (File.Exists(iconPath))
                    TrayIcon.Icon = new Icon(iconPath);
                else
                    TrayIcon.Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty) ?? SystemIcons.Application;
            }
            catch
            {
                TrayIcon.Icon = SystemIcons.Application;
            }

            UpdateTrayTooltip();

            TrayIcon.MouseDown += (s, e) =>
            {
                if (e.Button == Forms.MouseButtons.Left)
                    _trayLeftPressAt = DateTime.UtcNow;
            };

            TrayIcon.MouseClick += (s, e) =>
            {
                if (e.Button == Forms.MouseButtons.Left)
                {
                    Dispatcher.Invoke(HandleTrayLeftClick);
                }
                else if (e.Button == Forms.MouseButtons.Right)
                {
                    Dispatcher.Invoke(ToggleTrayMenu);
                }
            };
        }

        private void HandleTrayLeftClick()
        {
            DateTime now = DateTime.UtcNow;
            if (now - _lastTrayLeftClickAt < TrayLeftClickDebounce) return;
            _lastTrayLeftClickAt = now;

            // The press that produced this click may already have closed the panel by
            // taking activation from it. Toggling would then read the panel as closed
            // and open it again, so a click that owns the close in flight is spent on it.
            if (_quickPanelWindow?.TryClaimTrayPressDismissal(_trayLeftPressAt) == true) return;

            ToggleQuickPanel();
        }

        internal bool IsTrayLeftPressActive
            => DateTime.UtcNow - _trayLeftPressAt <= TrayLeftPressWindow;

        private void HandleHardwareButtonPress()
        {
            ToggleQuickPanel();
        }

        public void ShowMainWindow()
        {
            MainWindow window = EnsureMainWindow();
            window.Show();
            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;
            window.Activate();
            window.Focus();
        }

        public void ToggleMainWindow()
        {
            MainWindow window = EnsureMainWindow();
            if (window.IsVisible)
            {
                window.Hide();
            }
            else
            {
                ShowMainWindow();
            }
        }

        public void ToggleQuickPanel()
        {
            QuickPanelWindow quickPanel = EnsureQuickPanelWindow();
            if (_trayMenuWindow?.IsVisible == true) _trayMenuWindow.Hide();

            // IsVisible stays true for the whole exit animation, so testing it made a
            // press during the close re-run the close instead of reopening - the panel
            // appeared to ignore the tray until the animation had finished. IsOpenOrOpening
            // asks where the panel is headed rather than where it currently is, which is
            // the question a toggle actually has.
            if (quickPanel.IsOpenOrOpening)
            {
                quickPanel.HideAnimated();
            }
            else
            {
                quickPanel.ShowAnimated();
            }
        }

        public void ToggleTrayMenu()
        {
            TrayMenuWindow trayMenu = EnsureTrayMenuWindow();
            if (_quickPanelWindow?.IsVisible == true) _quickPanelWindow.Hide();
            if (trayMenu.IsOpenOrOpening) trayMenu.HideAnimated();
            else trayMenu.ShowAnimated();
        }

        private MainWindow EnsureMainWindow()
        {
            if (_mainWindow is not null) return _mainWindow;
            _mainWindow = Services.GetRequiredService<MainWindow>();
            MainWindow = _mainWindow;
            _mainWindow.IsVisibleChanged += OnTelemetrySurfaceVisibilityChanged;
            return _mainWindow;
        }

        private QuickPanelWindow EnsureQuickPanelWindow()
        {
            if (_quickPanelWindow is not null) return _quickPanelWindow;
            _quickPanelWindow = Services.GetRequiredService<QuickPanelWindow>();
            _quickPanelWindow.IsVisibleChanged += OnTelemetrySurfaceVisibilityChanged;
            return _quickPanelWindow;
        }

        private TrayMenuWindow EnsureTrayMenuWindow()
            => _trayMenuWindow ??= Services.GetRequiredService<TrayMenuWindow>();

        private void OnTelemetrySurfaceVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            bool surfaceVisible = _mainWindow?.IsVisible == true || _quickPanelWindow?.IsVisible == true;

            IDeviceStateService? telemetry = Services.GetService<IDeviceStateService>();
            if (telemetry is not null)
            {
                if (surfaceVisible) telemetry.ResumePolling();
                else telemetry.PausePolling();
            }

            // A release found while the window was hidden has been waiting for a
            // window to be drawn in.
            if (_mainWindow?.IsVisible == true) ShowPendingUpdateIfAny();
        }

        public void ExitApplication()
        {
            _mainWindow?.AllowClose();
            Shutdown();
        }

        /// <summary>
        /// Windows is logging off, restarting or shutting down. It may terminate the
        /// process before OnExit ever runs, so the pending settings write has to happen
        /// here too - this is the path that loses a setting changed just before the user
        /// hits restart.
        /// </summary>
        protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
        {
            try { AppConfig.Flush(); } catch (Exception ex) { Logger.WriteLine("Config flush on session end: " + ex.Message); }
            base.OnSessionEnding(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            ProcessHelper.ExitRequested -= OnExternalExitRequested;

            // Settings are written on a two-second sliding debounce, so anything changed
            // in the last moments of the session is still only in memory. Nothing else
            // runs that timer to ground, and a tray app is very often closed within a
            // second of the toggle the user came to flip - which is what made settings
            // look like they saved only sometimes. Flush first, before any teardown
            // below can throw and take the pending write with it.
            try { AppConfig.Flush(); } catch (Exception ex) { Logger.WriteLine("Config flush on exit: " + ex.Message); }

            _powerSettleTimer.Stop();
            _powerSettleTimer.Elapsed -= OnPowerSettled;
            SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;
            SystemEvents.SessionSwitch -= SystemEvents_SessionSwitch;
            SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;

            if (_hardwareInitialized)
            {
                try { PeripheralsProvider.UnregisterForDeviceEvents(); } catch { }
                try { Program.clamshellControl?.UnregisterDisplayEvents(); } catch { }
                try { Program.gpuControl?.StandardModeFix(); } catch { }
                try { Program.hardwareOverlay?.StopOverlay(); } catch { }
            }

            // Null when a second instance handed off to the running one and shut down
            // before the container was ever built.
            // Let the provider release only services/view-models that were actually
            // created. Resolving optional services here just to dispose them used to
            // construct watchers during shutdown, and hand-maintaining this list missed
            // any future IDisposable singleton.
            try { (Services as IDisposable)?.Dispose(); } catch { }
            if (_hardwareInitialized)
            {
                // Sensor polling is stopped above, so native counters cannot be sampled
                // while their handles are being released.
                try { HardwareControl.Dispose(); } catch { }
                try { Program.allyControl?.Dispose(); } catch { }
                try { Program.toast?.Dispose(); } catch { }
                try { Program.acpi?.Dispose(); } catch { }
            }
            Program.Bridge = null;
            if (TrayIcon != null)
            {
                TrayIcon.Visible = false;
                TrayIcon.Dispose();
            }
            base.OnExit(e);
        }

        #region IUiBridge Implementation

        public void ShowMode(int mode) => Dispatch(() =>
        {
            if (GetCreatedViewModel<QuickPanelViewModel>() is { } quick) quick.SelectedPerformanceMode = mode;
            if (GetCreatedViewModel<HomeViewModel>() is { } home) home.CurrentPerformanceMode = mode;
            if (GetCreatedViewModel<MainViewModel>() is { } main)
                main.CurrentModeName = Services.GetRequiredService<IPerformanceService>().CurrentModeName;
        });

        public void SetModeLabel(string label)
        {
            _latestModeLabel = label;
            Dispatch(() =>
            {
                if (GetCreatedViewModel<MainViewModel>() is { } main) main.CurrentModeName = label;
            });
        }

        public void VisualiseGPUMode() => Dispatch(() =>
        {
            int mode = Services.GetRequiredService<IGpuService>().CurrentGpuMode;
            if (GetCreatedViewModel<QuickPanelViewModel>() is { } quick) quick.SelectedGpuMode = mode;
            if (GetCreatedViewModel<HomeViewModel>() is { } home) home.CurrentGpuMode = mode;
            if (GetCreatedViewModel<DisplayViewModel>() is { } display) display.CurrentGpuMode = mode;
        });

        public void VisualiseGPUOn() => VisualiseGPUMode();
        public void VisualiseGPUEco() => VisualiseGPUMode();
        public void VisualiseBattery(int limit) => Dispatch(() =>
        {
            if (GetCreatedViewModel<BatteryViewModel>() is { } battery) battery.ChargeLimit = limit;
            if (GetCreatedViewModel<QuickPanelViewModel>() is { } quick) quick.ChargeLimit = limit;
            UpdateTrayTooltip();
        });
        public void VisualiseBatteryFull() => Dispatch(() =>
        {
            if (GetCreatedViewModel<BatteryViewModel>() is { } battery)
                battery.IsFullChargeOverride = BatteryControl.chargeFull;
            UpdateTrayTooltip();
        });

        /// <summary>
        /// Puts the charge limit on the tray icon's tooltip. Windows owns its own
        /// battery icon and gives no way for an application to annotate it, so this and
        /// the marker on the Battery page are where the limit is visible.
        /// </summary>
        private static void UpdateTrayTooltip()
        {
            if (TrayIcon is null) return;

            int limit = BatteryControl.chargeFull ? 100 : AppConfig.Get("charge_limit", 100);
            TrayIcon.Text = limit >= 100
                ? "Arsenal"
                : $"Arsenal: charging stops at {limit}%";
        }
        public void VisualiseScreen(bool screenEnabled, bool screenAuto, int frequency, int maxFrequency, int overdrive, bool overdriveSetting, int miniled1, int miniled2, bool hdr, bool acm, int fhd, int hdrControl) => Dispatch(() =>
        {
            if (GetCreatedViewModel<DisplayViewModel>() is { } display)
            {
                display.CurrentRefreshRate = frequency;
                display.MaxRefreshRate = maxFrequency;
                display.IsAutoRefresh = screenAuto;
                display.IsOverdrive = overdrive > 0;
                display.IsOverdriveAvailable = overdriveSetting;
                display.IsMiniLed = miniled1 > 0 || miniled2 > 0;
                display.IsHdr = hdr;
                display.IsResolutionToggleSupported = fhd >= 0;
                display.IsHdrControlSupported = hdr && hdrControl >= 0;
            }

            if (GetCreatedViewModel<QuickPanelViewModel>() is { } quick)
            {
                quick.RefreshRate = frequency;
                quick.IsOverdrive = overdrive > 0;
                quick.IsOverdriveAvailable = overdriveSetting;
                quick.IsAutoRefresh = screenAuto;
                quick.IsMiniLed = miniled1 > 0 || miniled2 > 0;
            }
        });
        public void VisualiseUpdates(string text)
        {
            _latestUpdateStatus = text;
            Dispatch(() =>
            {
                if (GetCreatedViewModel<AboutViewModel>() is { } about) about.UpdateStatus = text;
            });
        }
        public void VisualiseArmoury(bool running)
        {
            _latestAsusOptimizationState = running;
            Dispatch(() =>
            {
                if (GetCreatedViewModel<AdvancedViewModel>() is { } advanced)
                    advanced.AsusOptimizationRunning = running;
            });
        }
        public void FansInit() => Dispatch(() => GetCreatedViewModel<PerformanceViewModel>()?.LoadCurrentProfile());
        public void GPUInit() => VisualiseGPUMode();
        public void LabelFansResult(string label)
        {
            _latestCalibrationStatus = label;
            Dispatch(() =>
            {
                if (GetCreatedViewModel<PerformanceViewModel>() is { } performance)
                    performance.CalibrationStatus = label;
            });
        }
        public bool ConfirmGpuModeRestart(int currentMode, int targetMode)
        {
            // When the phone asked for the switch, the phone is what must answer. A modal
            // on the PC would be invisible to whoever pressed the button, and would hold
            // the request open until somebody walked over to the machine.
            if (RemoteRestartPrompt.IsCompanionCommand)
            {
                bool toUltimate = targetMode == AsusACPI.GPUModeUltimate;
                return RemoteRestartPrompt.Ask(
                    toUltimate ? "Restart to enable Ultimate?" : "Restart to leave Ultimate?",
                    toUltimate
                        ? "Ultimate routes the display straight to the dedicated GPU. Windows has to restart to change the display path."
                        : "Leaving Ultimate hands the display back to the integrated GPU. Windows has to restart to change the display path.");
            }

            bool result = false;
            if (Dispatcher.CheckAccess())
            {
                _quickPanelWindow?.HideForModal();
                result = GpuRestartConfirmation.Show(_mainWindow, targetMode == AsusACPI.GPUModeUltimate);
            }
            else
                Dispatcher.Invoke(() =>
                {
                    _quickPanelWindow?.HideForModal();
                    result = GpuRestartConfirmation.Show(_mainWindow, targetMode == AsusACPI.GPUModeUltimate);
                });
            return result;
        }
        public void ShowToast(string text, ToastIcon icon, string? detail = null) => Dispatch(() =>
        {
            ToastManager.Show(text, icon, detail);
        });

        public void RunOnUi(Action action) => Dispatch(action);

        private void Dispatch(Action action)
        {
            if (Dispatcher.CheckAccess()) action();
            else Dispatcher.BeginInvoke(action);
        }

        #endregion
    }
}
