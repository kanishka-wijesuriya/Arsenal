using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Arsenal.Application.Models;
using Arsenal.Application.Services;
using Arsenal.Application.Services.Contracts;
using Arsenal.AutoUpdate;
using Arsenal.Helpers;
using Arsenal.Mode;
using Arsenal.UI.Services;
using Arsenal.UI.Services.Remote;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using MediaColor = System.Windows.Media.Color;

namespace Arsenal.UI.ViewModels
{
    public partial class DevicesViewModel : ObservableObject
    {
        private readonly IPeripheralService _peripheralService;

        [ObservableProperty]
        private ObservableCollection<PeripheralDeviceModel> _devices = new();

        [ObservableProperty]
        private PeripheralDeviceModel? _selectedDevice;

        [ObservableProperty]
        private int _deviceDpi = 800;

        [ObservableProperty]
        private int _devicePollingRate = 1000;

        [ObservableProperty]
        private int _deviceSleepMinutes = 3;

        [ObservableProperty]
        private int _keyboardLightingMode;

        [ObservableProperty]
        private int _keyboardLightingBrightness = 100;

        [ObservableProperty]
        private int _keyboardLightingSpeed = 1;

        [ObservableProperty]
        private bool _keyboardAuraSync;

        [ObservableProperty]
        private MediaColor _keyboardPrimaryColor = MediaColor.FromRgb(255, 0, 0);

        [ObservableProperty]
        private MediaColor _keyboardSecondaryColor = MediaColor.FromRgb(0, 0, 0);

        [ObservableProperty]
        private int _keyboardProfile;

        [ObservableProperty]
        private int _keyboardLowBatteryWarning;

        [ObservableProperty]
        private bool _keyboardOledEnabled;

        [ObservableProperty]
        private int _keyboardOledBrightness = 100;

        [ObservableProperty]
        private int _keyboardOledMode;

        public ObservableCollection<PeripheralOptionModel> KeyboardProfileOptions { get; } = new();
        public ObservableCollection<PeripheralOptionModel> KeyboardOledOptions { get; } = new();
        public IReadOnlyList<PeripheralOptionModel> SleepTimeoutOptions { get; } = new[]
        {
            new PeripheralOptionModel { Value = 0, Label = AppStrings.Get("Never") },
            new PeripheralOptionModel { Value = 1, Label = "1 min" },
            new PeripheralOptionModel { Value = 2, Label = "2 min" },
            new PeripheralOptionModel { Value = 3, Label = "3 min" },
            new PeripheralOptionModel { Value = 5, Label = "5 min" },
            new PeripheralOptionModel { Value = 10, Label = "10 min" }
        };
        public bool IsMouseSelected => SelectedDevice?.IsMouse == true;
        public bool IsKeyboardSelected => SelectedDevice?.IsKeyboard == true;

        /// <summary>
        /// The polling rates this mouse reports, as thumb stops. The series doubles
        /// rather than stepping evenly, so a fixed step would let the thumb rest on a
        /// rate the device does not have and the service would round the write away
        /// without the readout ever saying so.
        /// </summary>
        [ObservableProperty]
        private System.Windows.Media.DoubleCollection _devicePollingRateTicks = new();

        [ObservableProperty]
        private int _devicePollingRateMinimum = 125;

        [ObservableProperty]
        private int _devicePollingRateMaximum = 1000;

        /// <summary>A mouse with one rate has nothing to choose between.</summary>
        public bool HasPollingRateChoice => DevicePollingRateTicks.Count > 1;
        public bool IsKeyboardManualLighting => !KeyboardAuraSync;
        public string KeyboardPrimaryHex => $"#{KeyboardPrimaryColor.R:X2}{KeyboardPrimaryColor.G:X2}{KeyboardPrimaryColor.B:X2}";
        public string KeyboardSecondaryHex => $"#{KeyboardSecondaryColor.R:X2}{KeyboardSecondaryColor.G:X2}{KeyboardSecondaryColor.B:X2}";
        public System.Windows.Media.SolidColorBrush KeyboardPrimaryBrush => new(KeyboardPrimaryColor);
        public System.Windows.Media.SolidColorBrush KeyboardSecondaryBrush => new(KeyboardSecondaryColor);

        [ObservableProperty]
        private bool _isAllyDevice = false;

        [ObservableProperty]
        private int _allyVibration = 100;

        [ObservableProperty]
        private int _allyLeftStickDeadzone = 0;

        [ObservableProperty]
        private int _allyRightStickDeadzone = 0;

        public DevicesViewModel(IPeripheralService peripheralService)
        {
            _peripheralService = peripheralService;
            IsAllyDevice = AppConfig.IsAlly();
            KeyboardAuraSync = Arsenal.Peripherals.PeripheralsProvider.IsKeyboardAuraSync;

            _peripheralService.DevicesChanged += () =>
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    Devices.Clear();
                    foreach (var d in _peripheralService.Devices)
                        Devices.Add(d);
                    if (SelectedDevice == null && Devices.Count > 0)
                        SelectedDevice = Devices[0];
                });
            };

            foreach (var d in _peripheralService.Devices)
                Devices.Add(d);
            if (Devices.Count > 0)
                SelectedDevice = Devices[0];
        }

        [RelayCommand]
        public void RefreshDevices()
        {
            Task.Run(_peripheralService.RefreshDevices);
        }

        partial void OnSelectedDeviceChanged(PeripheralDeviceModel? value)
        {
            OnPropertyChanged(nameof(IsMouseSelected));
            OnPropertyChanged(nameof(IsKeyboardSelected));
            if (value == null) return;

            // Bounds before values, so the sliders are sized for this device before the
            // device's own numbers land on them.
            var rates = value.PollingRates is { Count: > 0 } ? value.PollingRates : new List<int> { value.PollingRate };
            DevicePollingRateTicks = new System.Windows.Media.DoubleCollection(rates.Select(hz => (double)hz));
            DevicePollingRateMinimum = rates[0];
            DevicePollingRateMaximum = rates[^1];
            OnPropertyChanged(nameof(HasPollingRateChoice));

            DeviceDpi = value.CurrentDpi;
            DevicePollingRate = value.PollingRate;
            DeviceSleepMinutes = value.SleepMinutes;
            KeyboardLowBatteryWarning = value.LowBatteryWarningPercent;
            KeyboardLightingMode = value.LightingMode;
            KeyboardLightingBrightness = value.LightingBrightness;
            KeyboardLightingSpeed = value.LightingSpeed;
            KeyboardPrimaryColor = ToMediaColor(value.PrimaryColorArgb);
            KeyboardSecondaryColor = ToMediaColor(value.SecondaryColorArgb);
            KeyboardProfile = value.Profile;
            KeyboardOledEnabled = value.KeyboardOledEnabled;
            KeyboardOledBrightness = value.KeyboardOledBrightness;
            KeyboardOledMode = value.KeyboardOledMode;

            KeyboardProfileOptions.Clear();
            for (int i = 0; i < value.ProfileCount; i++)
                KeyboardProfileOptions.Add(new PeripheralOptionModel { Value = i, Label = $"Profile {i + 1}" });

            KeyboardOledOptions.Clear();
            KeyboardOledOptions.Add(new PeripheralOptionModel { Value = 0, Label = "Off" });
            for (int i = 0; i < value.KeyboardOledAnimationCount; i++)
                KeyboardOledOptions.Add(new PeripheralOptionModel { Value = i + 1, Label = $"Animation {i + 1}" });
            if (value.HasKeyboardOled)
                KeyboardOledOptions.Add(new PeripheralOptionModel { Value = value.KeyboardOledAnimationCount + 1, Label = "Clock" });
        }

        partial void OnKeyboardPrimaryColorChanged(MediaColor value)
        {
            OnPropertyChanged(nameof(KeyboardPrimaryHex));
            OnPropertyChanged(nameof(KeyboardPrimaryBrush));
        }

        partial void OnKeyboardSecondaryColorChanged(MediaColor value)
        {
            OnPropertyChanged(nameof(KeyboardSecondaryHex));
            OnPropertyChanged(nameof(KeyboardSecondaryBrush));
        }

        partial void OnKeyboardAuraSyncChanged(bool value)
        {
            OnPropertyChanged(nameof(IsKeyboardManualLighting));
            Arsenal.Peripherals.PeripheralsProvider.SetKeyboardAuraSync(value);
            if (value) _ = Task.Run(Arsenal.Peripherals.PeripheralsProvider.SyncKeyboardsWithAura);
        }

        [RelayCommand]
        public async Task ApplyDeviceSettings()
        {
            if (SelectedDevice?.IsMouse != true) return;
            string id = SelectedDevice.Id;
            await Task.Run(() =>
            {
                _peripheralService.SetDpi(id, DeviceDpi);
                _peripheralService.SetPollingRate(id, DevicePollingRate);
                _peripheralService.SetSleepTimeout(id, DeviceSleepMinutes);
            });
        }

        [RelayCommand]
        public async Task ApplyKeyboardLighting()
        {
            if (SelectedDevice?.IsKeyboard != true) return;
            string id = SelectedDevice.Id;
            int primary = ToDrawingArgb(KeyboardPrimaryColor);
            int secondary = ToDrawingArgb(KeyboardSecondaryColor);
            await Task.Run(() => _peripheralService.SetKeyboardLighting(
                id, KeyboardLightingMode, primary, secondary, KeyboardLightingSpeed, KeyboardLightingBrightness));
        }

        [RelayCommand]
        public async Task ApplyKeyboardEnergy()
        {
            if (SelectedDevice?.IsKeyboard != true) return;
            await Task.Run(() => _peripheralService.SetKeyboardEnergy(
                SelectedDevice.Id, DeviceSleepMinutes, KeyboardLowBatteryWarning));
        }

        [RelayCommand]
        public async Task SetKeyboardProfile(object? profileParam)
        {
            if (SelectedDevice?.IsKeyboard != true || !int.TryParse(profileParam?.ToString(), out int profile)) return;
            KeyboardProfile = profile;
            await Task.Run(() => _peripheralService.SetKeyboardProfile(SelectedDevice.Id, profile));
        }

        [RelayCommand]
        public async Task ApplyKeyboardOled()
        {
            if (SelectedDevice?.HasKeyboardOled != true) return;
            await Task.Run(() => _peripheralService.SetKeyboardOled(
                SelectedDevice.Id, KeyboardOledEnabled, KeyboardOledBrightness, KeyboardOledMode));
        }

        [RelayCommand]
        public void SetKeyboardPresetColor(object? parameter)
        {
            if (parameter is string value && System.Windows.Media.ColorConverter.ConvertFromString(value) is MediaColor color)
                KeyboardPrimaryColor = color;
        }

        private static MediaColor ToMediaColor(int argb) => MediaColor.FromArgb(
            (byte)((argb >> 24) & 0xFF),
            (byte)((argb >> 16) & 0xFF),
            (byte)((argb >> 8) & 0xFF),
            (byte)(argb & 0xFF));

        private static int ToDrawingArgb(MediaColor color) =>
            (color.A << 24) | (color.R << 16) | (color.G << 8) | color.B;
    }

    public partial class AutomationViewModel : ObservableObject
    {
        private readonly IProfileService _profileService;
        private bool _isReady;

        [ObservableProperty]
        private int _acMode = AsusACPI.PerformanceBalanced;

        [ObservableProperty]
        private int _batteryMode = AsusACPI.PerformanceSilent;

        [ObservableProperty]
        private bool _isAutoSwitchEnabled = true;

        [ObservableProperty]
        private bool _isAutoEcoOnBattery = true;

        [ObservableProperty]
        private bool _isClamshellMode = false;

        public AutomationViewModel(IProfileService profileService, IPerformanceService performanceService, IGpuService gpuService)
        {
            _profileService = profileService;
            Refresh();

            // These values are owned by config, and the Performance page, the hotkeys
            // and the automatic power-source switch all write to it. Without following
            // those changes this page kept whatever it read at construction - and since
            // every setter here saves both modes, touching anything afterwards wrote the
            // stale pair straight back over the real ones.
            performanceService.ModeChanged += _ => System.Windows.Application.Current?.Dispatcher.BeginInvoke(Refresh);
            gpuService.GpuModeChanged += _ => System.Windows.Application.Current?.Dispatcher.BeginInvoke(Refresh);
        }

        /// <summary>
        /// Re-reads everything from config. <see cref="_isReady"/> is cleared for the
        /// duration so the property setters do not treat a refresh as an edit and
        /// immediately write it back.
        /// </summary>
        public void Refresh()
        {
            _isReady = false;
            try
            {
                AcMode = _profileService.AutoAcMode;
                BatteryMode = _profileService.AutoBatteryMode;
                IsAutoSwitchEnabled = _profileService.IsAutoSwitchEnabled;
                IsAutoEcoOnBattery = AppConfig.Is("gpu_auto");
                IsClamshellMode = AppConfig.Is("clamshell");
            }
            finally { _isReady = true; }
        }

        [RelayCommand]
        public void SaveSettings()
        {
            _profileService.AutoAcMode = AcMode;
            _profileService.AutoBatteryMode = BatteryMode;
            _profileService.IsAutoSwitchEnabled = IsAutoSwitchEnabled;
            AppConfig.Set("gpu_auto", IsAutoEcoOnBattery ? 1 : 0);
            AppConfig.Set("clamshell", IsClamshellMode ? 1 : 0);
            if (IsClamshellMode) Program.clamshellControl?.ToggleLidAction();
            else ClamshellModeControl.DisableClamshellMode();
        }

        partial void OnAcModeChanged(int value) { if (_isReady) SaveSettings(); }
        partial void OnBatteryModeChanged(int value) { if (_isReady) SaveSettings(); }
        partial void OnIsAutoSwitchEnabledChanged(bool value) { if (_isReady) SaveSettings(); }
        partial void OnIsAutoEcoOnBatteryChanged(bool value) { if (_isReady) SaveSettings(); }
        partial void OnIsClamshellModeChanged(bool value) { if (_isReady) SaveSettings(); }
    }

    public partial class UpdatesViewModel : ObservableObject
    {
        private readonly IUpdateService _updateService;

        [ObservableProperty]
        private bool _isChecking = false;

        [ObservableProperty]
        private string _statusMessage = AppStrings.Get("UpdatesReadyToScanThisLaptop");

        [ObservableProperty]
        private ObservableCollection<UpdateInfo> _asusUpdates = new();

        [ObservableProperty]
        private ObservableCollection<UpdateInfo> _visibleDrivers = new();

        [ObservableProperty]
        private int _selectedDriverFilter;

        [ObservableProperty] private int _driverCount;
        [ObservableProperty] private int _outdatedDriverCount;
        [ObservableProperty] private int _currentDriverCount;
        [ObservableProperty] private int _optionalDriverCount;

        public string AllDriversFilterText => AppStrings.Format("UpdatesFilterAll", DriverCount);
        public string OutdatedDriversFilterText => AppStrings.Format("UpdatesFilterUpdates", OutdatedDriverCount);
        public string CurrentDriversFilterText => AppStrings.Format("UpdatesFilterCurrent", CurrentDriverCount);
        public string OptionalDriversFilterText => AppStrings.Format("UpdatesFilterOptional", OptionalDriverCount);

        public UpdatesViewModel(IUpdateService updateService)
        {
            _updateService = updateService;
        }

        [RelayCommand]
        public async Task CheckUpdates()
        {
            IsChecking = true;
            StatusMessage = AppStrings.Get("UpdatesReadingAsusSupport");
            try
            {
                List<UpdateInfo> drivers = await _updateService.CheckAsusUpdatesAsync();
                AsusUpdates.Clear();
                foreach (UpdateInfo driver in drivers) AsusUpdates.Add(driver);

                DriverCount = drivers.Count;
                OutdatedDriverCount = drivers.Count(driver => driver.State == UpdateState.Outdated);
                CurrentDriverCount = drivers.Count(driver => driver.State is UpdateState.UpToDate or UpdateState.Newer);
                OptionalDriverCount = drivers.Count(driver => driver.State is UpdateState.Unknown or UpdateState.NotInstalled);
                RefreshFilterLabels();
                ApplyDriverFilter();

                StatusMessage = OutdatedDriverCount > 0
                    ? AppStrings.Format(
                        OutdatedDriverCount == 1 ? "UpdatesDriverUpdateAvailableOne" : "UpdatesDriverUpdatesAvailableMany",
                        OutdatedDriverCount)
                    : OptionalDriverCount > 0
                        ? AppStrings.Format(
                            OptionalDriverCount == 1 ? "UpdatesVerifiedOptionalMissingOne" : "UpdatesVerifiedOptionalMissingMany",
                            OptionalDriverCount)
                        : AppStrings.Get("UpdatesEveryDriverUpToDate");
            }
            catch (Exception ex)
            {
                StatusMessage = AppStrings.Get("UpdatesDriverScanFailed");
                Logger.WriteLine("Driver page scan: " + ex.Message);
            }
            finally { IsChecking = false; }
        }

        [RelayCommand]
        public void SetDriverFilter(object? filterParam)
        {
            if (!int.TryParse(filterParam?.ToString(), out int filter)) filter = 0;
            SelectedDriverFilter = Math.Clamp(filter, 0, 3);
        }

        partial void OnSelectedDriverFilterChanged(int value) => ApplyDriverFilter();

        private void ApplyDriverFilter()
        {
            IEnumerable<UpdateInfo> source = SelectedDriverFilter switch
            {
                1 => AsusUpdates.Where(driver => driver.State == UpdateState.Outdated),
                2 => AsusUpdates.Where(driver => driver.State is UpdateState.UpToDate or UpdateState.Newer),
                3 => AsusUpdates.Where(driver => driver.State is UpdateState.Unknown or UpdateState.NotInstalled),
                _ => AsusUpdates
            };

            VisibleDrivers.Clear();
            foreach (UpdateInfo driver in source) VisibleDrivers.Add(driver);
        }

        private void RefreshFilterLabels()
        {
            OnPropertyChanged(nameof(AllDriversFilterText));
            OnPropertyChanged(nameof(OutdatedDriversFilterText));
            OnPropertyChanged(nameof(CurrentDriversFilterText));
            OnPropertyChanged(nameof(OptionalDriversFilterText));
        }

        /// <summary>One cancellation source per package being fetched.</summary>
        private readonly Dictionary<string, CancellationTokenSource> _downloads = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Fetches the package into the application's own downloads folder instead of
        /// handing the URL to the default browser.
        /// </summary>
        [RelayCommand]
        public async Task DownloadDriver(UpdateInfo? driver)
        {
            if (driver is null || string.IsNullOrWhiteSpace(driver.DownloadUrl)) return;
            if (driver.DownloadState == DriverDownloadState.Downloading) return;

            // Already fetched and still where we left it, so there is nothing to do but
            // let the card offer to run it.
            if (!string.IsNullOrEmpty(driver.DownloadedPath) && File.Exists(driver.DownloadedPath))
            {
                driver.DownloadState = DriverDownloadState.Ready;
                return;
            }

            var cancellation = new CancellationTokenSource();
            _downloads[driver.Title] = cancellation;

            driver.DownloadProgress = -1;
            driver.DownloadState = DriverDownloadState.Downloading;

            // Progress<T> captures this thread's context, so the reports land back on the
            // UI thread and the bar can be written to directly.
            var progress = new Progress<int>(percent => driver.DownloadProgress = percent);

            try
            {
                string? path = await _updateService.DownloadAsusPackageAsync(
                    driver.DownloadUrl, progress, cancellation.Token, driver.Sha256);

                if (cancellation.IsCancellationRequested)
                {
                    driver.DownloadState = DriverDownloadState.Idle;
                }
                else if (string.IsNullOrEmpty(path))
                {
                    driver.DownloadState = DriverDownloadState.Failed;
                    StatusMessage = AppStrings.Format("UpdatesDownloadFailed", driver.Title);
                }
                else
                {
                    driver.DownloadedPath = path;
                    driver.DownloadState = DriverDownloadState.Ready;
                    StatusMessage = AppStrings.Format("UpdatesDownloadReady", driver.Title);
                }
            }
            finally
            {
                _downloads.Remove(driver.Title);
                cancellation.Dispose();
            }
        }

        [RelayCommand]
        public void CancelDownload(UpdateInfo? driver)
        {
            if (driver is null) return;
            if (_downloads.TryGetValue(driver.Title, out CancellationTokenSource? cancellation)) cancellation.Cancel();
        }

        /// <summary>
        /// Hands the downloaded package to the shell. ASUS packages are self-extracting
        /// installers that ask for elevation themselves, so this stays a plain launch and
        /// stays an explicit second click by the user rather than part of the download.
        /// </summary>
        [RelayCommand]
        public void InstallDriver(UpdateInfo? driver)
        {
            if (driver is null || string.IsNullOrEmpty(driver.DownloadedPath)) return;
            if (!File.Exists(driver.DownloadedPath))
            {
                // Moved or cleaned up behind our back; offer the download again.
                driver.DownloadedPath = string.Empty;
                driver.DownloadState = DriverDownloadState.Idle;
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(driver.DownloadedPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Driver install launch: " + ex.Message);
                StatusMessage = AppStrings.Format("UpdatesDownloadFailed", driver.Title);
            }
        }

        [RelayCommand]
        public void ShowDriverInFolder(UpdateInfo? driver)
        {
            string path = driver?.DownloadedPath ?? string.Empty;
            try
            {
                if (File.Exists(path))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                else if (Directory.Exists(_updateService.DownloadFolder))
                    Process.Start(new ProcessStartInfo(_updateService.DownloadFolder) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Driver reveal: " + ex.Message);
            }
        }
    }

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
        [ObservableProperty] private int _keyboardTimeoutSeconds;
        [ObservableProperty] private int _keyboardAcTimeoutSeconds;
        [ObservableProperty] private bool _hardwareOverlayEnabled;
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

        /// <summary>Same reasoning for the backlight timeouts, which are ours to store.</summary>
        public int KeyboardTimeoutMaximum { get; private set; } = 600;

        public int KeyboardAcTimeoutMaximum { get; private set; } = 600;

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
            KeyboardTimeoutSeconds = Math.Max(0, AppConfig.Get("keyboard_timeout", 60));
            KeyboardAcTimeoutSeconds = Math.Max(0, AppConfig.Get("keyboard_ac_timeout", 0));

            // Widened before the page binds, so nothing already in force is out of reach.
            HibernateAfterMaximum = Math.Max(HibernateAfterMaximum, HibernateAfterMinutes);
            KeyboardTimeoutMaximum = Math.Max(KeyboardTimeoutMaximum, KeyboardTimeoutSeconds);
            KeyboardAcTimeoutMaximum = Math.Max(KeyboardAcTimeoutMaximum, KeyboardAcTimeoutSeconds);
            HardwareOverlayEnabled = AppConfig.IsOverlay();
            OverlayGameOnly = AppConfig.IsOverlayGameOnly();
            HasHandheldControls = AppConfig.IsAlly();
            AutoTdpEnabled = Ally.AllyControl.IsAutoTdpEnabled;
            Ally.AllyControl.OnAutoTDPChanged += value => System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                () => SetFromHardware(() => AutoTdpEnabled = value));
            Ally.AllyControl.OnFPSLimitChanged += value => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => FpsLimit = value);
            _isReady = true;
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
            Process.Start(new ProcessStartInfo("control.exe", "powercfg.cpl") { UseShellExecute = true });
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
            AppConfig.Set("keyboard_timeout", KeyboardTimeoutSeconds);
            AppConfig.Set("keyboard_ac_timeout", KeyboardAcTimeoutSeconds);
            PowerNative.SetHibernateAfter(HibernateAfterMinutes);
            Arsenal.Input.InputDispatcher.SetStatusLED(StatusLedEnabled);
            AppConfig.Set("status_led", StatusLedEnabled ? 1 : 0);
            Arsenal.Input.NumberPad.Set(NumberPadEnabled);
            Program.inputDispatcher?.InitBacklightTimer();
            Program.acpi.DeviceSet(AsusACPI.BootSound, BootSoundEnabled ? 1 : 0, "BootSound");
            AppConfig.Set("boot_sound", BootSoundEnabled ? 1 : 0);
            if (AutoClamshellEnabled) Program.clamshellControl?.ToggleLidAction();
            else ClamshellModeControl.DisableClamshellMode();
            Arsenal.Display.ScreenControl.AutoScreen(true);
            if (System.Windows.Application.Current?.MainWindow is System.Windows.Window window) window.Topmost = AlwaysOnTop;
            ToastManager.Show(AppStrings.Get("SavedToastTitle"), ToastIcon.Charger, AppStrings.Get("AdvancedSettingsAppliedToast"));
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
        partial void OnKeyboardTimeoutSecondsChanged(int value) { if (!_isReady) return; AppConfig.Set("keyboard_timeout", Math.Max(0, value)); Program.inputDispatcher?.InitBacklightTimer(); }
        partial void OnKeyboardAcTimeoutSecondsChanged(int value) { if (!_isReady) return; AppConfig.Set("keyboard_ac_timeout", Math.Max(0, value)); Program.inputDispatcher?.InitBacklightTimer(); }

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

    public partial class SettingsViewModel : ObservableObject
    {
        private bool _isReady;

        public bool CanSelfUpdate => App.CanSelfUpdate;

        [ObservableProperty]
        private bool _runOnStartup = false;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(AdministratorDescription))]
        private bool _runAsAdministrator;

        [ObservableProperty]
        private bool _minimizeToTray = true;

        [ObservableProperty]
        private int _selectedTheme = 0; // 0=System, 1=Dark, 2=Light

        /// <summary>Turns the Mica backdrop off for this app only.</summary>
        [ObservableProperty]
        private bool _disableTransparency;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsCustomAccent))]
        [NotifyPropertyChangedFor(nameof(AccentDescription))]
        private int _selectedAccentSource; // 0=Windows, 1=Custom

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CustomAccentBrush))]
        private System.Windows.Media.Color _customAccentColor;

        [ObservableProperty]
        private string _customAccentHex = string.Empty;

        [ObservableProperty]
        private int _selectedLanguage = 0;

        [ObservableProperty]
        private string _selectedLanguageCode = string.Empty;

        [ObservableProperty]
        private bool _checkUpdatesOnStartup = true;

        [ObservableProperty] private bool _toastEnabled = true;
        [ObservableProperty] private int _toastStyle;
        [ObservableProperty] private int _toastPosition;
        [ObservableProperty] private int _toastDurationSeconds = 4;
        [ObservableProperty] private bool _toastProgress = true;

        public IReadOnlyList<string> ToastStyles { get; } = new[] { "Fluent", "Compact", "Accent" };
        public IReadOnlyList<string> ToastPositions { get; } = new[]
        {
            AppStrings.Get("ToastPositionTopRight"),
            AppStrings.Get("ToastPositionBottomRight"),
            AppStrings.Get("ToastPositionTopCenter"),
            AppStrings.Get("ToastPositionBottomCenter")
        };

        public bool IsCustomAccent => SelectedAccentSource == 1;

        public string AccentDescription => IsCustomAccent
            ? AppStrings.Get("SettingsAccentCustomDescription")
            : AppStrings.Get("SettingsAccentWindowsDescription");

        public System.Windows.Media.SolidColorBrush CustomAccentBrush
        {
            get
            {
                var brush = new System.Windows.Media.SolidColorBrush(CustomAccentColor);
                brush.Freeze();
                return brush;
            }
        }

        public IReadOnlyList<LanguageOption> AvailableLanguages { get; } = new[]
        {
            new LanguageOption("", AppStrings.Get("SettingsUseWindowsLanguage")),
            new LanguageOption("en", "English"),
            new LanguageOption("ar", "Arabic"),
            new LanguageOption("cs-CZ", "Czech"),
            new LanguageOption("da", "Danish"),
            new LanguageOption("de", "German"),
            new LanguageOption("es", "Spanish"),
            new LanguageOption("fr", "French"),
            new LanguageOption("hu", "Hungarian"),
            new LanguageOption("id", "Indonesian"),
            new LanguageOption("it", "Italian"),
            new LanguageOption("ja", "Japanese"),
            new LanguageOption("ko", "Korean"),
            new LanguageOption("lt", "Lithuanian"),
            new LanguageOption("pl", "Polish"),
            new LanguageOption("pt-BR", "Portuguese (Brazil)"),
            new LanguageOption("pt-PT", "Portuguese (Portugal)"),
            new LanguageOption("ro", "Romanian"),
            new LanguageOption("tr", "Turkish"),
            new LanguageOption("uk", "Ukrainian"),
            new LanguageOption("vi", "Vietnamese"),
            new LanguageOption("zh-CN", "Chinese (Simplified)"),
            new LanguageOption("zh-TW", "Chinese (Traditional)")
        };

        public SettingsViewModel()
        {
            RunAsAdministrator = AppConfig.Is("run_as_admin");
            RunOnStartup = Startup.IsScheduled();
            MinimizeToTray = AppConfig.IsNotFalse("minimize_to_tray");
            SelectedTheme = AppConfig.Get("theme", 0);
            DisableTransparency = Arsenal.UI.App.IsOpaqueWindow;
            SelectedAccentSource = Math.Clamp(AppConfig.Get(AccentColorService.SourceSetting, 0), 0, 1);
            CustomAccentColor = AccentColorService.GetCustomAccent();
            CustomAccentHex = AccentColorService.ToHex(CustomAccentColor);
            SelectedLanguageCode = AppConfig.GetString("language") ?? string.Empty;
            CheckUpdatesOnStartup = CanSelfUpdate && AppConfig.IsNotFalse("check_updates");
            ToastEnabled = AppConfig.IsNotFalse("toast_enabled");
            ToastStyle = Math.Clamp(AppConfig.Get("toast_style", 0), 0, 2);
            ToastPosition = Math.Clamp(AppConfig.Get("toast_position", 0), 0, 3);
            ToastDurationSeconds = Math.Clamp((int)Math.Round(AppConfig.Get("toast_duration", 3500) / 1000d), 2, 12);
            ToastProgress = AppConfig.IsNotFalse("toast_progress");
            _isReady = true;
        }

        [RelayCommand]
        public void ToggleStartup()
        {
            if (RunOnStartup)
            {
                Startup.Schedule();
            }
            else
            {
                Startup.UnSchedule();
            }
        }

        [RelayCommand]
        public void SavePreferences()
        {
            AppConfig.Set("minimize_to_tray", MinimizeToTray ? 1 : 0);
            AppConfig.Set("theme", SelectedTheme);
            AppConfig.Set("check_updates", CanSelfUpdate && CheckUpdatesOnStartup ? 1 : 0);
            AppConfig.Set("language", SelectedLanguageCode ?? string.Empty);

            Arsenal.UI.App.ApplyConfiguredTheme();
        }

        [RelayCommand]
        public void SetTheme(object? themeParam)
        {
            SelectedTheme = themeParam is int value ? value : int.TryParse(themeParam?.ToString(), out int parsed) ? parsed : 0;
            SavePreferences();
        }

        [RelayCommand]
        public void SetAccentSource(object? sourceParam)
        {
            SelectedAccentSource = sourceParam is int value
                ? Math.Clamp(value, 0, 1)
                : int.TryParse(sourceParam?.ToString(), out int parsed) ? Math.Clamp(parsed, 0, 1) : 0;

            AppConfig.Set(AccentColorService.SourceSetting, SelectedAccentSource);
            if (IsCustomAccent)
                AppConfig.Set(AccentColorService.ColorSetting, AccentColorService.ToHex(CustomAccentColor));
            Arsenal.UI.App.ApplyConfiguredAccent();
        }

        [RelayCommand]
        public void ApplyCustomAccentHex()
        {
            if (AccentColorService.TryParse(CustomAccentHex, out System.Windows.Media.Color color))
            {
                CustomAccentColor = color;
                CustomAccentHex = AccentColorService.ToHex(color);
                AppConfig.Set(AccentColorService.ColorSetting, CustomAccentHex);
            }
            else
            {
                CustomAccentHex = AccentColorService.ToHex(CustomAccentColor);
            }
        }

        [RelayCommand]
        public void TestToast() => ToastManager.Show(
            AppStrings.Get("SettingsToastPreviewTitle"),
            ToastIcon.Charger,
            AppStrings.Get("SettingsToastPreviewBody"));

        /// <summary>
        /// Several features write under HKLM and are silently skipped without
        /// elevation: the ASUS ChargingRate value behind the battery icon's
        /// smart-charging shield, the charge-limit scheduled task, and the panel
        /// refresh flag.
        /// </summary>
        public bool IsElevated { get; } = Arsenal.Helpers.ProcessHelper.IsUserAdministrator();

        public bool NeedsElevation => !IsElevated;

        public string AdministratorDescription => RunAsAdministrator
            ? IsElevated
                ? "Enabled. This session is running with administrator rights; turn it off to make future launches standard."
                : "Enabled. Windows will request administrator approval for this and future manual launches."
            : "Disabled. Arsenal opens normally and requests elevation only for actions that need it.";

        [RelayCommand]
        public void RunSetup() => (System.Windows.Application.Current as App)?.ShowSetup();

        partial void OnRunOnStartupChanged(bool value)
        {
            if (!_isReady) return;
            if (value) Startup.Schedule(); else Startup.UnSchedule();
        }

        partial void OnRunAsAdministratorChanged(bool value)
        {
            if (!_isReady) return;

            AppConfig.Set("run_as_admin", value ? 1 : 0);
            if (value && !IsElevated)
            {
                AppConfig.Flush();
                if (ProcessHelper.RunAsAdmin("--settings")) return;

                AppConfig.Set("run_as_admin", 0);
                _isReady = false;
                RunAsAdministrator = false;
                _isReady = true;
                return;
            }

            // Keep an existing sign-in task's privilege level synchronized with the
            // preference. The current elevated process stays elevated until it exits.
            if (RunOnStartup)
            {
                Startup.UnSchedule();
                Startup.Schedule();
            }
        }

        partial void OnMinimizeToTrayChanged(bool value) { if (_isReady) SavePreferences(); }

        partial void OnDisableTransparencyChanged(bool value)
        {
            if (!_isReady) return;
            AppConfig.Set(Arsenal.UI.App.OpaqueWindowSetting, value ? 1 : 0);
            // Repaints the grounds and swaps the backdrop in the same pass.
            Arsenal.UI.App.ApplyConfiguredTheme();
        }
        partial void OnCheckUpdatesOnStartupChanged(bool value) { if (_isReady) SavePreferences(); }
        partial void OnCustomAccentColorChanged(System.Windows.Media.Color value)
        {
            CustomAccentHex = AccentColorService.ToHex(value);
            if (!_isReady) return;
            AppConfig.Set(AccentColorService.ColorSetting, CustomAccentHex);
            if (IsCustomAccent) Arsenal.UI.App.ApplyConfiguredAccent();
        }
        partial void OnToastEnabledChanged(bool value) { if (_isReady) SaveToastPreferences(); }
        partial void OnToastStyleChanged(int value) { if (_isReady) SaveToastPreferences(); }
        partial void OnToastPositionChanged(int value) { if (_isReady) SaveToastPreferences(); }
        partial void OnToastDurationSecondsChanged(int value) { if (_isReady) SaveToastPreferences(); }
        partial void OnToastProgressChanged(bool value) { if (_isReady) SaveToastPreferences(); }

        private void SaveToastPreferences()
        {
            AppConfig.Set("toast_enabled", ToastEnabled ? 1 : 0);
            AppConfig.Set("toast_style", Math.Clamp(ToastStyle, 0, 2));
            AppConfig.Set("toast_position", Math.Clamp(ToastPosition, 0, 3));
            AppConfig.Set("toast_duration", Math.Clamp(ToastDurationSeconds, 2, 12) * 1000);
            AppConfig.Set("toast_progress", ToastProgress ? 1 : 0);
        }
    }

    public sealed record LanguageOption(string Code, string DisplayName);

    public partial class AboutViewModel : ObservableObject
    {
        public bool CanSelfUpdate => App.CanSelfUpdate;

        /// <summary>Where the complete corresponding source for this build lives.</summary>
        /// <remarks>
        /// Arsenal is GPL-3.0, so anyone holding a binary is entitled to its source.
        /// The About page is where a person looks for that, which makes this link part
        /// of the licence obligation rather than a nicety.
        /// </remarks>
        public const string SourceUrl = "https://github.com/kanishka-wijesuriya/Arsenal";

        private const string LicenseUrl = "https://get-arsenal.com/license";
        private const string UpstreamUrl = "https://github.com/seerge/g-helper";

        private readonly IUpdateService _updateService;

        [ObservableProperty]
        private string _appName = "Arsenal";

        [ObservableProperty]
        private string _version = string.Empty;

        [ObservableProperty]
        private string _hardwareModel = "ASUS ROG / TUF";

        [ObservableProperty]
        private string _biosVersion = "N/A";

        /// <summary>
        /// The machine's specification, one property per row.
        /// </summary>
        /// <remarks>
        /// Named properties rather than a collection so the page can be built from the
        /// app's own SettingsRow primitives. SettingsGroup is itself an ItemsControl that
        /// stacks those rows and draws the hairlines between them - nesting a second
        /// ItemsControl inside it left the group with one child, no dividers, and none of
        /// the padding every other page gets.
        ///
        /// Anything the machine would not answer for stays empty, and the row hides
        /// itself, which is the same treatment the capability-gated rows elsewhere use.
        /// </remarks>
        [ObservableProperty] private string _specModel = string.Empty;
        [ObservableProperty] private string _specBios = string.Empty;
        [ObservableProperty] private string _specProcessor = string.Empty;
        [ObservableProperty] private string _specMemory = string.Empty;
        [ObservableProperty] private string _specGraphics = string.Empty;
        [ObservableProperty] private string _specDisplay = string.Empty;
        [ObservableProperty] private string _specStorage = string.Empty;
        [ObservableProperty] private string _specOperatingSystem = string.Empty;

        [ObservableProperty]
        private bool _isLoadingSpecifications = true;

        [ObservableProperty]
        private UpdateInfo _updateInfo = new();

        [ObservableProperty]
        private bool _isCheckingForUpdate;

        [ObservableProperty]
        private bool _isInstallingUpdate;

        [ObservableProperty]
        private string _updateStatus = AppStrings.Get("AboutReadyToCheckForUpdate");

        public AboutViewModel(IUpdateService updateService)
        {
            _updateService = updateService;
            Version = ReadDisplayVersion();
            HardwareModel = AppConfig.GetModel();
            BiosVersion = AppConfig.GetBiosAndModel().Item1;

            string assemblyVersion = ReleaseVersion.CurrentString();
            UpdateInfo = new UpdateInfo { CurrentVersion = assemblyVersion, LatestVersion = assemblyVersion };
            _updateService.UpdateStatusChanged += info => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                UpdateInfo = info;
                IsCheckingForUpdate = false;
                UpdateStatus = info.IsUpdateAvailable
                    ? AppStrings.Format("AboutUpdateAvailable", info.LatestVersion)
                    : AppStrings.Get("AboutUpToDate");
            });

            LoadSpecifications();
        }

        [RelayCommand]
        private void OpenSource() => OpenExternal(SourceUrl);

        [RelayCommand]
        private void OpenLicense() => OpenExternal(LicenseUrl);

        [RelayCommand]
        private void OpenUpstream() => OpenExternal(UpstreamUrl);

        private static void OpenExternal(string url)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Logger.WriteLine("Could not open " + url + ": " + ex.Message); }
        }

        [RelayCommand]
        public async Task CheckForUpdate()
        {
            if (!CanSelfUpdate) return;
            IsCheckingForUpdate = true;
            UpdateStatus = AppStrings.Get("AboutCheckingForUpdate");
            try
            {
                UpdateInfo info = await _updateService.CheckForUpdatesAsync(true);
                UpdateInfo = info;
                UpdateStatus = info.IsUpdateAvailable
                    ? AppStrings.Format("AboutUpdateAvailable", info.LatestVersion)
                    : AppStrings.Get("AboutUpToDate");
            }
            catch (Exception ex)
            {
                UpdateStatus = AppStrings.Get("AboutUpdateCheckFailed");
                Logger.WriteLine("About update check: " + ex.Message);
            }
            finally { IsCheckingForUpdate = false; }
        }

        [RelayCommand]
        public async Task DownloadUpdate()
        {
            if (!CanSelfUpdate || string.IsNullOrWhiteSpace(UpdateInfo.DownloadUrl) || IsInstallingUpdate) return;

            // The card owns the download, its progress bar and its failure state, and
            // it is the same card the automatic check raises. Handing off keeps one
            // update experience rather than two that behave differently.
            if (System.Windows.Application.Current?.MainWindow is Views.Windows.MainWindow window)
            {
                window.ShowApplicationUpdate(UpdateInfo);
                return;
            }

            // No window to draw the card in - the About page cannot be reached without
            // one, but the fallback keeps the button working rather than dead.
            IsInstallingUpdate = true;
            UpdateStatus = AppStrings.Get("AboutInstallingUpdate");
            try
            {
                if (!await _updateService.DownloadAndInstallUpdateAsync())
                    UpdateStatus = AppStrings.Get("AboutUpdateInstallFailed");
            }
            catch (Exception ex)
            {
                UpdateStatus = AppStrings.Get("AboutUpdateInstallFailed");
                Logger.WriteLine("About update install: " + ex.Message);
            }
            finally { IsInstallingUpdate = false; }
        }

        /// <summary>
        /// Prefers the informational version, which carries the pre-release label the
        /// assembly version cannot hold - "1.0.0-beta.1" rather than "1.0.0.0".
        /// </summary>
        private static string ReadDisplayVersion()
        {
            return "v" + ReleaseVersion.CurrentDisplayString();
        }

        private async void LoadSpecifications()
        {
            SystemSpecSheet specs = await Task.Run(SystemSpecs.Get);

            SpecModel = specs.Model ?? string.Empty;
            SpecBios = specs.Bios ?? string.Empty;
            SpecProcessor = specs.Processor ?? string.Empty;
            SpecMemory = specs.Memory ?? string.Empty;
            SpecGraphics = specs.Graphics ?? string.Empty;
            SpecDisplay = specs.Display ?? string.Empty;
            SpecStorage = specs.Storage ?? string.Empty;
            SpecOperatingSystem = specs.OperatingSystem ?? string.Empty;

            IsLoadingSpecifications = false;
        }
    }

    public partial class CommandPaletteViewModel : ObservableObject
    {
        private readonly ISettingsSearchService _searchService;

        [ObservableProperty]
        private string _searchQuery = string.Empty;

        [ObservableProperty]
        private ObservableCollection<SearchItem> _results = new();

        [ObservableProperty]
        private SearchItem? _selectedItem;

        public CommandPaletteViewModel(ISettingsSearchService searchService)
        {
            _searchService = searchService;
            UpdateResults();
        }

        partial void OnSearchQueryChanged(string value)
        {
            UpdateResults();
        }

        /// <summary>True only when a query was typed and matched nothing.</summary>
        public bool HasNoResults => Results.Count == 0 && !string.IsNullOrWhiteSpace(SearchQuery);

        private void UpdateResults()
        {
            Results.Clear();
            var matches = _searchService.Search(SearchQuery);
            foreach (var item in matches)
                Results.Add(item);

            // Always preselect the top hit, so Enter runs the best match without any
            // arrow keys. Clearing it when empty stops Enter firing a stale selection.
            SelectedItem = Results.Count > 0 ? Results[0] : null;
            OnPropertyChanged(nameof(HasNoResults));
        }

        [RelayCommand]
        public void ExecuteSelected()
        {
            if (SelectedItem?.Action != null)
            {
                SelectedItem.Action.Invoke();
            }
        }
    }
}
