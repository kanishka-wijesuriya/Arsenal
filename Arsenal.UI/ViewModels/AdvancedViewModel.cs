using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Arsenal.Application.Models;
using Arsenal.Application.Services;
using Arsenal.Application.Services.Contracts;
using Arsenal.AutoUpdate;
using Arsenal.Gpu;
using Arsenal.Helpers;
using Arsenal.Mode;
using Arsenal.UI.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;

namespace Arsenal.UI.ViewModels
{
    public partial class AdvancedViewModel : ObservableObject
    {
        private bool _isReady;

        /// <summary>
        /// Set while state is being written back from the thing that owns it rather than
        /// from the user.
        /// </summary>
        /// <remarks>
        /// These rows are switches now, so the property is both what the user moves and
        /// what the service manager, the overlay and AllyControl report back into. Without
        /// this the report would read as a second user request and toggle straight back.
        /// </remarks>
        private bool _applyingExternalState;

        private void SetFromHardware(Action apply)
        {
            _applyingExternalState = true;
            try { apply(); }
            finally { _applyingExternalState = false; }
        }

        /// <summary>True when a switch moved because the user moved it.</summary>
        private bool IsUserChange => _isReady && !_applyingExternalState;

        /// <summary>
        /// Normal launches are deliberately non-admin. Machine-wide actions either
        /// request elevation when invoked or explain that it is required.
        /// </summary>
        public bool IsElevated { get; } = Arsenal.Helpers.ProcessHelper.IsUserAdministrator();

        public bool NeedsElevation => !IsElevated;

        [RelayCommand]
        public void RestartAsAdministrator() => Arsenal.Helpers.ProcessHelper.RunAsAdmin();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanStartAsusServices))]
        [NotifyPropertyChangedFor(nameof(CanStopAsusServices))]
        private bool _asusOptimizationRunning = true;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanStartAsusServices))]
        [NotifyPropertyChangedFor(nameof(CanStopAsusServices))]
        private bool _isAsusServiceOperationRunning;

        /// <summary>
        /// Which of the two actions the row offers, if either.
        /// </summary>
        /// <remarks>
        /// Starting and stopping services is an operation, not a setting: it takes time,
        /// it can need elevation, and it can fail. A switch would claim it had already
        /// happened the moment it was moved. One button that names the action available
        /// from where the services actually are says the true thing instead, and while
        /// the operation is in flight neither is offered.
        /// </remarks>
        public bool CanStartAsusServices => !IsAsusServiceOperationRunning && !AsusOptimizationRunning;
        public bool CanStopAsusServices => !IsAsusServiceOperationRunning && AsusOptimizationRunning;

        [ObservableProperty]
        private bool _fnLockEnabled = false;

        [ObservableProperty]
        private bool _aspmEnabled = true;

        [ObservableProperty] private bool _standbyNetworkingEnabled;
        [ObservableProperty] private bool _nvidiaPlatformEnabled;
        [ObservableProperty] private bool _statusLedEnabled;
        [ObservableProperty] private bool _numberPadEnabled;
        [ObservableProperty] private bool _killGpuAppsEnabled;
        [ObservableProperty] private bool _optimizedUsbCEnabled;
        [ObservableProperty] private bool _disableOverdrive;
        [ObservableProperty] private bool _forceOverdrive;
        [ObservableProperty] private bool _alwaysOnTop;
        [ObservableProperty] private bool _bootSoundEnabled;
        [ObservableProperty] private bool _autoClamshellEnabled;
        [ObservableProperty] private int _hibernateAfterMinutes;
        [ObservableProperty] private bool _hardwareOverlayEnabled;

        public OverlaySettingsViewModel OverlaySettings { get; } = new();
        [ObservableProperty] private bool _overlayGameOnly;
        [ObservableProperty] private bool _hasHandheldControls;
        [ObservableProperty] private bool _autoTdpEnabled;
        [ObservableProperty] private int _fpsLimit;

        /// <summary>
        /// Whether this laptop has the touchpad number pad the row controls. False on
        /// every model without one, which is most of them - the control did nothing
        /// there, and a toggle that does nothing is worse than no toggle.
        /// </summary>
        public bool HasNumberPad { get; private set; }

        /// <summary>
        /// Whether the firmware answers for the keyboard and chassis status LEDs.
        /// </summary>
        public bool HasStatusLed { get; private set; }

        /// <summary>
        /// The track has to reach whatever Windows is already set to. These are
        /// preferences rather than hardware registers, so there is no firmware bound to
        /// read - but a power plan can hold a longer idle-hibernate delay than any round
        /// number we would pick, and the slider clamps what it cannot draw. That clamp
        /// runs through <see cref="OnHibernateAfterMinutesChanged"/> straight into the
        /// active power plan, so a track that stopped short would quietly shorten the
        /// user's hibernate delay just by opening the page.
        /// </summary>
        public int HibernateAfterMaximum { get; private set; } = 720;

        public AdvancedViewModel()
        {
            AsusOptimizationRunning = AsusService.IsAsusOptimizationRunning();
            FnLockEnabled = AppConfig.Is("fn_lock");
            AspmEnabled = AppConfig.IsNotFalse("aspm");
            StandbyNetworkingEnabled = AppConfig.IsNotFalse("standby_networking");
            NvidiaPlatformEnabled = AppConfig.Is("nv_platform");
            // A DeviceGet on a feature the firmware does not implement comes back
            // negative, so the same read that gives the current state also says
            // whether this machine has the feature at all.
            int statusLed = Program.acpi.DeviceGet(AsusACPI.StatusLed);
            HasStatusLed = statusLed >= 0;
            StatusLedEnabled = statusLed > 0;

            // Upstream's rule: the model has to claim a number pad and the touchpad
            // driver has to answer for it. Asking twice would run the ioctl twice,
            // so the one read decides both whether to show the row and its state.
            int numberPad = AppConfig.IsNumberPad() ? Arsenal.Input.NumberPad.Get() : -1;
            HasNumberPad = numberPad >= 0;
            NumberPadEnabled = numberPad == 1;
            KillGpuAppsEnabled = AppConfig.Is("kill_gpu_apps");
            OptimizedUsbCEnabled = AppConfig.Is("optimized_usbc");
            DisableOverdrive = AppConfig.Is("no_overdrive");
            ForceOverdrive = AppConfig.Is("force_overdrive");
            AlwaysOnTop = AppConfig.Is("topmost");
            int bootSound = Program.acpi.DeviceGet(AsusACPI.BootSound);
            BootSoundEnabled = bootSound is >= 0 and <= ushort.MaxValue ? bootSound == 1 : AppConfig.Is("boot_sound");
            AutoClamshellEnabled = AppConfig.Is("toggle_clamshell_mode");
            HibernateAfterMinutes = Math.Max(0, PowerNative.GetHibernateAfter());

            // Widened before the page binds, so nothing already in force is out of reach.
            HibernateAfterMaximum = Math.Max(HibernateAfterMaximum, HibernateAfterMinutes);
            HardwareOverlayEnabled = AppConfig.IsOverlay();
            OverlayGameOnly = AppConfig.IsOverlayGameOnly();
            HasHandheldControls = AppConfig.IsAlly();
            AutoTdpEnabled = Ally.AllyControl.IsAutoTdpEnabled;
            Ally.AllyControl.OnAutoTDPChanged += value => System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                () => SetFromHardware(() => AutoTdpEnabled = value));
            Ally.AllyControl.OnFPSLimitChanged += value => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => FpsLimit = value);
            InitIgpuMemory();
            _isReady = true;
        }

        // ===== iGPU memory ============================================================

        /// <summary>
        /// The sizes AMD firmware offers for the integrated GPU's share of RAM. Empty on
        /// Intel, where the driver reads a registry value instead, and on the Ally, which
        /// has a fixed set of its own.
        /// </summary>
        private int[] _vramOptions = [];

        public bool HasIgpuMemory { get; private set; }
        public List<PeripheralOptionModel> IgpuMemoryOptions { get; } = new();

        [ObservableProperty]
        private int _igpuMemory;

        private void InitIgpuMemory()
        {
            int unitMb = 0;
            if (PawnIO.CpuInfo.IsAMD) _vramOptions = Program.acpi.GetVramOptions(out unitMb);

            if (_vramOptions.Length > 0)
            {
                for (int i = 0; i < _vramOptions.Length; i++)
                    AddIgpuMemoryOption(i, _vramOptions[i] == 0 ? "Auto" : ((double)_vramOptions[i] * unitMb / 1024).ToString("0.#") + " GB");

                int current = Program.acpi.GetVramMem();
                if (current == 0) current = AppConfig.Get("vram_mem", 0);
                IgpuMemory = Math.Max(0, Array.IndexOf(_vramOptions, current));
            }
            else if (IntelVram.Init())
            {
                AddIgpuMemoryOption(0, "Auto");
                for (int i = 0; i < IntelVram.Sizes.Length; i++) AddIgpuMemoryOption(i + 1, IntelVram.Sizes[i] + " GB");
                IgpuMemory = IntelVram.Index;
            }
            else
            {
                if (!AppConfig.IsAlly()) return;

                int apuMem = Program.acpi.GetAPUMem();
                if (apuMem < 0) return;

                AddIgpuMemoryOption(0, "Auto");
                for (int i = 1; i <= 8; i++) AddIgpuMemoryOption(i, i + " GB");
                IgpuMemory = apuMem;
            }

            HasIgpuMemory = true;
        }

        private void AddIgpuMemoryOption(int index, string label) =>
            IgpuMemoryOptions.Add(new PeripheralOptionModel { Value = index, Label = label });

        partial void OnIgpuMemoryChanged(int value)
        {
            if (!_isReady || value < 0) return;

            // The Intel size is a machine-wide registry value.
            if (IntelVram.Supported && !ProcessHelper.IsUserAdministrator())
            {
                ProcessHelper.RunAsAdmin();
                return;
            }

            if (IntelVram.Supported) IntelVram.SetIndex(value);
            else if (_vramOptions.Length == 0) Program.acpi.SetAPUMem(value);
            else
            {
                Program.acpi.SetVramMem(_vramOptions[value]);
                AppConfig.Set("vram_mem", _vramOptions[value]);
            }

            var restart = new Arsenal.UI.Services.GpuModeConfirmationContent(
                "Restart to apply",
                "The integrated GPU takes its new share of memory when Windows starts. Save your work before restarting.",
                "Restart now",
                "Later");
            if (Arsenal.UI.Services.GpuModeConfirmation.Show(System.Windows.Application.Current?.MainWindow, restart))
                Process.Start(new ProcessStartInfo(ProcessHelper.SystemPath("shutdown"), "/r /t 1") { CreateNoWindow = true, UseShellExecute = false });
        }

        [RelayCommand]
        public async Task StartAsusServices() => await SetAsusServicesRunning(true);

        [RelayCommand]
        public async Task StopAsusServices() => await SetAsusServicesRunning(false);

        /// <summary>Kept for the companion bridge, which sends one command for both.</summary>
        [RelayCommand]
        public async Task ToggleAsusServices()
        {
            await SetAsusServicesRunning(!AsusOptimizationRunning);
        }

        public async Task SetAsusServicesRunning(bool shouldRun)
        {
            if (IsAsusServiceOperationRunning) return;

            // Asked of the service manager rather than of the bound property, which is
            // only ever a report of what was last seen there.
            if (AsusService.IsAsusOptimizationRunning() == shouldRun)
            {
                SyncAsusServiceState();
                return;
            }

            if (!ProcessHelper.IsUserAdministrator())
            {
                ProcessHelper.RunAsAdmin(shouldRun ? "--start-asus-services" : "--stop-asus-services");
                // The elevated copy does the work. This one re-reads rather than assuming
                // it happened, so the row never offers the action it did not perform.
                SyncAsusServiceState();
                return;
            }

            IsAsusServiceOperationRunning = true;
            try
            {
                await Task.Run(shouldRun ? AsusService.StartAsusServices : AsusService.StopAsusServices);
            }
            finally
            {
                SyncAsusServiceState();
                IsAsusServiceOperationRunning = false;
            }
        }

        private void SyncAsusServiceState()
            => AsusOptimizationRunning = AsusService.IsAsusOptimizationRunning();

        [RelayCommand]
        public void ToggleFnLock()
        {
            Input.InputDispatcher.ToggleFnLock();
            FnLockEnabled = AppConfig.Is("fn_lock");
        }

        [RelayCommand]
        public void OpenPowerPlanSettings()
        {
            Process.Start(new ProcessStartInfo(ProcessHelper.SystemPath("control.exe"), "powercfg.cpl") { UseShellExecute = true });
        }

        [RelayCommand]
        public void SaveSystemTweaks()
        {
            AppConfig.Set("aspm", AspmEnabled ? 1 : 0);
            PowerNative.SetBalancedASPM(AspmEnabled ? 0 : 2);
            AppConfig.Set("standby_networking", StandbyNetworkingEnabled ? 1 : 0);
            PowerNative.SetConnectivityInStandby(StandbyNetworkingEnabled ? 0 : 1, StandbyNetworkingEnabled ? 0 : 2);
            AppConfig.Set("nv_platform", NvidiaPlatformEnabled ? 1 : 0);
            AppConfig.Set("kill_gpu_apps", KillGpuAppsEnabled ? 1 : 0);
            AppConfig.Set("optimized_usbc", OptimizedUsbCEnabled ? 1 : 0);
            AppConfig.Set("no_overdrive", DisableOverdrive ? 1 : 0);
            AppConfig.Set("force_overdrive", ForceOverdrive ? 1 : 0);
            AppConfig.Set("topmost", AlwaysOnTop ? 1 : 0);
            AppConfig.Set("toggle_clamshell_mode", AutoClamshellEnabled ? 1 : 0);
            PowerNative.SetHibernateAfter(HibernateAfterMinutes);
            Arsenal.Input.InputDispatcher.SetStatusLED(StatusLedEnabled);
            AppConfig.Set("status_led", StatusLedEnabled ? 1 : 0);
            Arsenal.Input.NumberPad.Set(NumberPadEnabled);
            Program.acpi.DeviceSet(AsusACPI.BootSound, BootSoundEnabled ? 1 : 0, "BootSound");
            AppConfig.Set("boot_sound", BootSoundEnabled ? 1 : 0);
            if (AutoClamshellEnabled) Program.clamshellControl?.ToggleLidAction();
            else ClamshellModeControl.DisableClamshellMode();
            Arsenal.Display.ScreenControl.AutoScreen(true);
            if (System.Windows.Application.Current?.MainWindow is System.Windows.Window window) window.Topmost = AlwaysOnTop;
            ToastManager.Show("Saved", ToastIcon.Charger, "Advanced settings applied");
        }

        partial void OnAspmEnabledChanged(bool value) { if (!_isReady) return; AppConfig.Set("aspm", value ? 1 : 0); PowerNative.SetBalancedASPM(value ? 0 : 2); }
        partial void OnStandbyNetworkingEnabledChanged(bool value) { if (!_isReady) return; AppConfig.Set("standby_networking", value ? 1 : 0); PowerNative.SetConnectivityInStandby(value ? 0 : 1, value ? 0 : 2); }
        partial void OnNvidiaPlatformEnabledChanged(bool value) { if (_isReady) AppConfig.Set("nv_platform", value ? 1 : 0); }
        partial void OnStatusLedEnabledChanged(bool value) { if (!_isReady) return; AppConfig.Set("status_led", value ? 1 : 0); Arsenal.Input.InputDispatcher.SetStatusLED(value); }
        partial void OnNumberPadEnabledChanged(bool value) { if (_isReady) Arsenal.Input.NumberPad.Set(value); }
        partial void OnKillGpuAppsEnabledChanged(bool value) { if (_isReady) AppConfig.Set("kill_gpu_apps", value ? 1 : 0); }
        partial void OnOptimizedUsbCEnabledChanged(bool value) { if (_isReady) AppConfig.Set("optimized_usbc", value ? 1 : 0); }
        partial void OnDisableOverdriveChanged(bool value) { if (!_isReady) return; AppConfig.Set("no_overdrive", value ? 1 : 0); Arsenal.Display.ScreenControl.AutoScreen(true); }
        // Re-reads the panel so the "+ OD" label and the overdrive write path pick up
        // the override immediately, without forcing a refresh-rate change.
        partial void OnForceOverdriveChanged(bool value) { if (!_isReady) return; AppConfig.Set("force_overdrive", value ? 1 : 0); Arsenal.Display.ScreenControl.InitScreen(); }
        partial void OnAlwaysOnTopChanged(bool value) { if (!_isReady) return; AppConfig.Set("topmost", value ? 1 : 0); if (System.Windows.Application.Current?.MainWindow is System.Windows.Window window) window.Topmost = value; }
        partial void OnBootSoundEnabledChanged(bool value) { if (!_isReady) return; AppConfig.Set("boot_sound", value ? 1 : 0); Program.acpi.DeviceSet(AsusACPI.BootSound, value ? 1 : 0, "BootSound"); }
        partial void OnAutoClamshellEnabledChanged(bool value) { if (!_isReady) return; AppConfig.Set("toggle_clamshell_mode", value ? 1 : 0); if (value) Program.clamshellControl?.ToggleLidAction(); else ClamshellModeControl.DisableClamshellMode(); }
        partial void OnHibernateAfterMinutesChanged(int value) { if (_isReady) PowerNative.SetHibernateAfter(Math.Max(0, value)); }

        [RelayCommand]
        public void OpenLog()
        {
            Process.Start(new ProcessStartInfo(Logger.logFile) { UseShellExecute = true });
        }

        // The three commands below stay as the flip, with the work moved into the
        // property. The switches drive the property directly, and the tray menu, command
        // palette and companion bridge keep calling these unchanged.

        [RelayCommand]
        public void ToggleHardwareOverlay() => HardwareOverlayEnabled = !HardwareOverlayEnabled;

        partial void OnHardwareOverlayEnabledChanged(bool value)
        {
            if (!IsUserChange) return;
            AppConfig.Set("overlay", value ? 1 : 0);
            if (value) Program.hardwareOverlay?.StartOverlay();
            else Program.hardwareOverlay?.StopOverlay();
        }

        [RelayCommand]
        public void ToggleOverlayGameOnly() => OverlayGameOnly = !OverlayGameOnly;

        partial void OnOverlayGameOnlyChanged(bool value)
        {
            if (!IsUserChange) return;
            AppConfig.Set("overlay_game_only", value ? 1 : 0);
            if (!HardwareOverlayEnabled) return;
            Program.hardwareOverlay?.StopOverlay();
            Program.hardwareOverlay?.StartOverlay();
        }

        [RelayCommand]
        public void ToggleAutoTdp() => AutoTdpEnabled = !AutoTdpEnabled;

        /// <summary>
        /// AllyControl owns this state and flips it itself, so it is only asked when the
        /// switch and the controller actually disagree - asking otherwise would flip it
        /// away from what the switch now shows.
        /// </summary>
        partial void OnAutoTdpEnabledChanged(bool value)
        {
            if (!IsUserChange) return;
            if (Ally.AllyControl.IsAutoTdpEnabled != value) Program.allyControl?.ToggleAutoTDP();
        }

        [RelayCommand]
        public void CycleFpsLimit() => Program.allyControl?.ToggleFPSLimit();
    }
}
