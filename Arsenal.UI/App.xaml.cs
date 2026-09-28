using Arsenal.Application.Models;
using Arsenal.Application.Services.Contracts;
using Arsenal.AutoUpdate;
using Arsenal.Battery;
using Arsenal.Display;
using Arsenal.Helpers;
using Arsenal.Peripherals;
using Arsenal.UI.ViewModels;
using Arsenal.UI.Services.Remote;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Forms = System.Windows.Forms;

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
        private AutoUpdateControl? _autoUpdateControl;
        /// <summary>Held when the feed finds a release while the window is hidden.</summary>
        private ReleaseUpdate? _pendingUpdate;
        private string? _latestModeLabel;
        private string? _latestUpdateStatus;
        private string? _latestCalibrationStatus;
        private bool? _latestAsusOptimizationState;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            AppDomain.CurrentDomain.UnhandledException += (s, ev) => Logger.WriteLine("Unhandled: " + ev.ExceptionObject);
            DispatcherUnhandledException += (s, ev) => { Logger.WriteLine("Dispatcher Unhandled: " + ev.Exception); ev.Handled = true; };

            string action = e.Args.FirstOrDefault()?.Trim().ToLowerInvariant() ?? string.Empty;

            // The isolated UI lifetime smoke supplies a minimal service provider before
            // starting the dispatcher. A normal Arsenal process can never enter this
            // branch because its provider is built below.
            if (Services is not null &&
                Environment.GetEnvironmentVariable("ARSENAL_UI_LIFETIME_SMOKE_HOST") == "1")
                return;

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

            // Mica is no longer an application option. Remove the retired preference
            // once so old installations do not carry a setting that has no effect.
            if (AppConfig.Exists("opaque_window")) AppConfig.Remove("opaque_window");

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

            // Explicit destinations still open visibly. The preference applies to an
            // ordinary desktop/sign-in launch, while the command-line switches remain
            // an unconditional way to start in the tray.
            bool startMinimized = ApplicationLaunch.ShouldStartMinimized(
                e.Args,
                AppConfig.Is(ApplicationLaunch.StartMinimizedSetting));
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
                    "An update is ready", ToastIcon.Charger, $"Arsenal {release.Version}");
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

        private void OnExternalExitRequested()
        {
            Dispatcher.BeginInvoke(new Action(ExitApplication));
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
    }
}
