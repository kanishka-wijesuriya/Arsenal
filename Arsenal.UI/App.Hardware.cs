using Arsenal.Application.Models;
using Arsenal.Application.Services.Contracts;
using Arsenal.Battery;
using Arsenal.Display;
using Arsenal.Gpu;
using Arsenal.Helpers;
using Arsenal.Peripherals;
using Arsenal.UI.Services;
using Arsenal.UI.Views.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Arsenal.UI
{
    public partial class App
    {
        private bool _hardwareInitialized;
        private int _restoreAfterResume;
        private readonly System.Timers.Timer _powerSettleTimer = new() { AutoReset = false };

        private void InitHardwareCore()
        {
            try
            {
                ProcessHelper.SetPriority();
                Program.InitCoreHardware();
                Program.currentSource = Program.ReadPowerSource();

                ProcessHelper.KillSmartDisplayControl();
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
                _ = RestoreHotkeysAfterServicesStopAsync();
            }
            catch (Exception ex)
            {
                Logger.WriteLine("InitHardwareCore error: " + ex.Message);
            }
        }

        private async Task RestoreHotkeysAfterServicesStopAsync()
        {
            try
            {
                if (await AsusService.StopOnStartupAsync() && !Dispatcher.HasShutdownStarted)
                    Program.inputDispatcher?.Init();
            }
            catch (Exception ex)
            {
                Logger.WriteLine("ASUS startup services: " + ex.Message);
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
                PeripheralsProvider.DetectAllAsusHeadsets();
                global::Startup.StartupCheck();

                // Release unneeded startup allocations once hardware initialization has settled.
                Arsenal.UI.Services.BackgroundMemoryRelease.Schedule();
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
                Interlocked.Exchange(ref _restoreAfterResume, 0);
                _powerSettleTimer.Stop();
                GPUModeControl.suspended = true;
                Program.gpuControl?.StandardModeFix();
                Program.modeControl?.ShutdownReset();
                Input.InputDispatcher.ShutdownStatusLed();
                USB.XGM.NotifyShutdown();
                return;
            }

            if (e.Mode == PowerModes.Resume)
            {
                GPUModeControl.suspended = false;
                Interlocked.Exchange(ref _restoreAfterResume, 1);
            }

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
            if (GPUModeControl.suspended) return;

            bool resumed = Interlocked.Exchange(ref _restoreAfterResume, 0) != 0;
            var source = Program.ReadPowerSource();
            bool sourceChanged = source != Program.currentSource;
            if (!sourceChanged && !resumed) return;

            if (sourceChanged)
            {
                Logger.WriteLine($"Power source: {Program.currentSource} -> {source}");
                Program.currentSource = source;
            }

            BatteryControl.AutoBattery();
            if (Services.GetRequiredService<IProfileService>().IsAutoSwitchEnabled)
                Program.modeControl?.AutoPerformance(true);
            ApplyKeyboardBacklight();
            if (resumed)
            {
                Input.InputDispatcher.InitFNLock();
                PeripheralsProvider.RestoreKeyboardLighting();
            }
            Program.gpuControl?.AutoGPUMode(delay: 1000);
            ScreenControl.AutoScreen();
        }

        private static void SystemEvents_SessionSwitch(object? sender, SessionSwitchEventArgs e)
        {
            if (e.Reason is SessionSwitchReason.SessionLogon or SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect)
            {
                Helpers.ProcessHelper.KillSmartDisplayControl();
                USB.Aura.sessionLock = false;
                if (!AppConfig.Is("skip_aura")) USB.Aura.ApplyAura();
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
    }
}
