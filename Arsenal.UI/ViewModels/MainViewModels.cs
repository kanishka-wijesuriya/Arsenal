using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Arsenal.Application.Models;
using Arsenal.Application.Services.Contracts;
using Arsenal.UI.Services;
using Arsenal.Mode;
using Arsenal.Display;
using Arsenal.Helpers;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.ObjectModel;

namespace Arsenal.UI.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly IDeviceStateService _deviceStateService;
        private readonly IPerformanceService _performanceService;
        private readonly IGpuService _gpuService;
        private readonly IBatteryService _batteryService;
        private readonly ISettingsSearchService _searchService;

        [ObservableProperty]
        private HardwareTelemetry _telemetry = new();

        [ObservableProperty]
        private string _activePageTag = "Home";

        [ObservableProperty]
        private string _currentModeName = "Balanced";

        [ObservableProperty]
        private string _currentGpuStatus = "Standard";

        [ObservableProperty]
        private bool _isCommandPaletteOpen = false;

        /// <summary>First-run setup, shown as an in-window panel rather than a dialog.</summary>
        [ObservableProperty]
        private bool _isSetupOpen = false;

        [ObservableProperty]
        private bool _isUpdateOpen = false;

        [ObservableProperty]
        private string _modelName = "ASUS ROG / TUF";

        /// <summary>Covers the window while a GPU switch runs; it can take many seconds.</summary>
        [ObservableProperty]
        private bool _isGpuSwitching;

        /// <summary>
        /// The longest a GPU switch can legitimately take. Restarting the NVIDIA services
        /// and re-creating the GPU control on the way out of Eco account for most of it.
        /// </summary>
        private static readonly TimeSpan GpuSwitchCeiling = TimeSpan.FromSeconds(45);

        private readonly System.Windows.Threading.DispatcherTimer _gpuSwitchWatchdog =
            new() { Interval = GpuSwitchCeiling };

        /// <summary>
        /// Raises and clears the switching overlay, with a ceiling on how long it can stay.
        /// </summary>
        /// <remarks>
        /// The overlay covers the whole window, so a busy signal that never gets its
        /// matching clear leaves the app looking hung with no way out of it. The hardware
        /// path does clear it in a finally, but that runs across several worker threads
        /// and a service restart - any one of them failing to come back should cost a
        /// stale message, not a frozen window.
        /// </remarks>
        private void SetGpuSwitching(bool busy)
        {
            IsGpuSwitching = busy;
            _gpuSwitchWatchdog.Stop();
            if (busy) _gpuSwitchWatchdog.Start();
        }

        [ObservableProperty]
        private string _gpuSwitchingMessage = "Switching GPU mode";

        [ObservableProperty]
        private bool _isAsusServicesChanging;

        [ObservableProperty]
        private string _asusServicesMessage = "Updating ASUS services";

        [ObservableProperty]
        private string _asusServicesDetail = "Preparing the installed ASUS background services.";

        public MainViewModel(
            IDeviceStateService deviceStateService,
            IPerformanceService performanceService,
            IGpuService gpuService,
            IBatteryService batteryService,
            ISettingsSearchService searchService)
        {
            _deviceStateService = deviceStateService;
            _performanceService = performanceService;
            _gpuService = gpuService;
            _batteryService = batteryService;
            _searchService = searchService;

            _deviceStateService.TelemetryUpdated += (t) =>
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    Telemetry = t;
                    CurrentGpuStatus = t.GpuStatus;
                });
            };

            _performanceService.ModeLabelChanged += (l) =>
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    CurrentModeName = _performanceService.CurrentModeName;
                });
            };

            // ModeControl raises ModeChanged immediately before persisting the new mode.
            // Use the event value so the shell never displays the previous profile.
            _performanceService.ModeChanged += mode =>
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    CurrentModeName = mode switch
                    {
                        0 => "Balanced",
                        1 => "Turbo",
                        2 => "Silent",
                        _ => "Custom"
                    };
                });
            };

            _gpuService.GpuBusyChanged += (busy, message) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (!string.IsNullOrWhiteSpace(message)) GpuSwitchingMessage = message!;
                SetGpuSwitching(busy);
            });

            AsusService.TransitionChanged += (busy, message, detail) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                AsusServicesMessage = message;
                AsusServicesDetail = detail;
                IsAsusServicesChanging = busy;
            });

            _gpuSwitchWatchdog.Tick += (_, _) =>
            {
                _gpuSwitchWatchdog.Stop();
                if (!IsGpuSwitching) return;

                Logger.WriteLine("GPU switch overlay exceeded its ceiling; releasing the window");
                IsGpuSwitching = false;
            };

            int startupMode = AppConfig.Get("performance_" + Program.PerformanceKey());
            CurrentModeName = startupMode switch { 0 => "Balanced", 1 => "Turbo", 2 => "Silent", _ => _performanceService.CurrentModeName };
            ModelName = AppConfig.GetModel();
        }

        [RelayCommand]
        public void ToggleCommandPalette()
        {
            IsCommandPaletteOpen = !IsCommandPaletteOpen;
        }

        [RelayCommand]
        public void NavigateTo(string pageTag)
        {
            ActivePageTag = pageTag;
        }

        [RelayCommand]
        public void SetPerformanceMode(object? modeParam)
        {
            int mode = ToInt(modeParam, 0);
            _performanceService.SetMode(mode);
            CurrentModeName = _performanceService.CurrentModeName;
        }

        [RelayCommand]
        public void SetGpuMode(object? modeParam)
        {
            int mode = ToInt(modeParam, 0);
            _gpuService.SetGpuMode(mode);
        }

        private static int ToInt(object? param, int defaultValue = 0)
        {
            if (param == null) return defaultValue;
            if (param is int i) return i;
            if (int.TryParse(param.ToString(), out int parsed)) return parsed;
            return defaultValue;
        }
    }

    public partial class QuickPanelViewModel : ObservableObject
    {
        private readonly IPerformanceService _performanceService;
        private readonly IGpuService _gpuService;
        private readonly IDisplayService _displayService;
        private readonly IBatteryService _batteryService;
        private readonly ILightingService _lightingService;
        private readonly IDeviceStateService _deviceStateService;
        private readonly IInputDeviceService _inputDeviceService;

        /// <summary>
        /// Resolved on use rather than injected. Several tiles are backed by the Advanced
        /// and Automation view models, and constructing those probes the hardware; the
        /// quick panel must not pay for tiles nobody put on it.
        /// </summary>
        private readonly IServiceProvider _services;

        private readonly ThrottledHardwareWriter _panelBrightnessWriter;
        private bool _isReady;

        /// <summary>
        /// Hardware notifications can arrive while the flyout is animating. Normal-priority
        /// dispatcher work runs ahead of WPF's render pass and used to steal a page frame;
        /// background priority lets the compositor present first without making the live
        /// readouts perceptibly late.
        /// </summary>
        private static void QueueUiUpdate(Action update) =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                update, System.Windows.Threading.DispatcherPriority.Background);

        private AdvancedViewModel Advanced => _services.GetRequiredService<AdvancedViewModel>();
        private IProfileService Automation => _services.GetRequiredService<IProfileService>();

        [ObservableProperty]
        private HardwareTelemetry _telemetry = new();

        [ObservableProperty]
        private int _selectedPerformanceMode = 0;

        [ObservableProperty]
        private PerformancePlanItem? _quickPlan1;

        [ObservableProperty]
        private PerformancePlanItem? _quickPlan2;

        [ObservableProperty]
        private PerformancePlanItem? _quickPlan3;

        private bool _isRefreshingPerformancePlans;
        public ObservableCollection<PerformancePlanItem> PerformancePlans { get; } = new();
        public bool IsQuickPlan1Selected => QuickPlan1?.ModeIndex == SelectedPerformanceMode;
        public bool IsQuickPlan2Selected => QuickPlan2?.ModeIndex == SelectedPerformanceMode;
        public bool IsQuickPlan3Selected => QuickPlan3?.ModeIndex == SelectedPerformanceMode;

        [ObservableProperty]
        private int _selectedGpuMode = 0;

        [ObservableProperty]
        private int _refreshRate = 60;

        [ObservableProperty]
        private bool _isOverdrive = false;

        /// <summary>
        /// Overdrive is supported by the panel and not switched off in Advanced -
        /// the original <c>overdriveSetting</c> flag, which is what decides whether
        /// the maximum rate is advertised as "+ OD".
        /// </summary>
        [ObservableProperty]
        private bool _isOverdriveAvailable = false;

        [ObservableProperty]
        private bool _isMiniLed = false;

        [ObservableProperty]
        private bool _isAutoRefresh = false;

        [ObservableProperty]
        private bool _isHardwareOverlay = false;

        [ObservableProperty]
        private bool _overlayGameOnly = false;

        [ObservableProperty]
        private bool _isTouchpadEnabled = true;

        [ObservableProperty]
        private bool _isFullChargeOverride = false;

        [ObservableProperty]
        private int _chargeLimit = 80;

        [ObservableProperty]
        private int _keyboardBrightness = 2;

        [ObservableProperty]
        private int _panelBrightness = 50;

        /// <summary>GameVisual software dimming, separate from the backlight above.</summary>
        [ObservableProperty]
        private int _oledDimming = 100;

        private bool _applyingExternalBrightness;
        private readonly ThrottledHardwareWriter _oledDimmingWriter;

        public bool IsMiniLedSupported { get; }
        public bool IsOverdriveSupported { get; }
        public bool IsMuxSupported { get; }
        public bool IsEcoSupported { get; }
        public bool HasTouchScreen => _inputDeviceService.HasTouchScreen;
        public bool HasTouchpad => _inputDeviceService.HasTouchpad;

        private bool IsAtMaximumRate => !IsAutoRefresh && RefreshRate > Arsenal.Display.ScreenControl.MIN_RATE;

        // Some models only charge to 60-80 or 100, with nothing below 60. The quick
        // panel has to use the same bounds as the Battery page, otherwise the slider
        // shows a level like 40% that the firmware silently rounds up to 60%.
        public bool HasSteppedChargeLimit { get; } = AppConfig.IsChargeLimit6080();
        public int ChargeLimitMinimum => HasSteppedChargeLimit ? 60 : 40;
        public System.Windows.Media.DoubleCollection ChargeLimitTicks { get; }

        /// <summary>Panel software dimming, shown next to the backlight on OLED models.</summary>
        public bool IsOledPanel { get; } = AppConfig.IsOLED();

        public bool IsOledDimmingAvailable => IsOledPanel && _displayService.IsColorPipelineEnabled;

        /// <summary>Covers the panel while a GPU switch runs; it can take many seconds.</summary>
        [ObservableProperty]
        private bool _isGpuSwitching;

        [ObservableProperty]
        private string _gpuSwitchingMessage = "Switching GPU mode";

        [ObservableProperty]
        private bool _isAsusServicesChanging;

        [ObservableProperty]
        private string _asusServicesMessage = "Updating ASUS services";

        [ObservableProperty]
        private string _asusServicesDetail = "Preparing the installed ASUS background services.";

        public QuickPanelViewModel(
            IPerformanceService performanceService,
            IGpuService gpuService,
            IDisplayService displayService,
            IBatteryService batteryService,
            ILightingService lightingService,
            IDeviceStateService deviceStateService,
            IInputDeviceService inputDeviceService,
            IServiceProvider services)
        {
            _performanceService = performanceService;
            _gpuService = gpuService;
            _displayService = displayService;
            _batteryService = batteryService;
            _lightingService = lightingService;
            _deviceStateService = deviceStateService;
            _inputDeviceService = inputDeviceService;
            _services = services;
            _panelBrightnessWriter = new ThrottledHardwareWriter(_displayService.SetPanelBrightness);
            _oledDimmingWriter = new ThrottledHardwareWriter(_displayService.SetBrightness);

            if (HasSteppedChargeLimit)
            {
                var ticks = new System.Windows.Media.DoubleCollection();
                for (int value = 60; value <= 80; value++) ticks.Add(value);
                ticks.Add(100);
                ticks.Freeze();
                ChargeLimitTicks = ticks;
            }
            else
            {
                var ticks = new System.Windows.Media.DoubleCollection();
                ticks.Freeze();
                ChargeLimitTicks = ticks;
            }

            SelectedPerformanceMode = _performanceService.CurrentMode;
            SelectedGpuMode = _gpuService.CurrentGpuMode;
            RefreshRate = _displayService.CurrentRefreshRate;
            IsOverdrive = _displayService.IsOverdriveEnabled;
            IsOverdriveAvailable = _displayService.IsOverdriveAvailable;
            IsAutoRefresh = _displayService.IsAutoRefreshEnabled;
            IsHardwareOverlay = AppConfig.IsOverlay();
            OverlayGameOnly = AppConfig.IsOverlayGameOnly();
            IsTouchpadEnabled = _inputDeviceService.IsTouchpadEnabled;
            IsFullChargeOverride = _batteryService.IsFullChargeOverride;
            ChargeLimit = _batteryService.ChargeLimit;
            KeyboardBrightness = _lightingService.Brightness;
            PanelBrightness = _displayService.PanelBrightness;
            OledDimming = _displayService.Brightness;
            IsMiniLedSupported = _displayService.IsMiniLedSupported;
            IsOverdriveSupported = _displayService.IsOverdriveSupported;
            IsMuxSupported = _gpuService.IsMuxSupported;
            IsEcoSupported = _gpuService.IsEcoSupported;

            _deviceStateService.TelemetryUpdated += (t) =>
            {
                QueueUiUpdate(() =>
                {
                    Telemetry = t;
                });
            };

            _performanceService.ModeChanged += (m) =>
            {
                QueueUiUpdate(() => SelectedPerformanceMode = m);
            };

            _performanceService.ProfilesChanged += () => QueueUiUpdate(RefreshPerformancePlans);

            _gpuService.GpuModeChanged += (g) =>
            {
                QueueUiUpdate(() => SelectedGpuMode = g);
            };

            _gpuService.GpuBusyChanged += (busy, message) => QueueUiUpdate(() =>
            {
                if (!string.IsNullOrWhiteSpace(message)) GpuSwitchingMessage = message!;
                IsGpuSwitching = busy;
            });

            AsusService.TransitionChanged += (busy, message, detail) => QueueUiUpdate(() =>
            {
                AsusServicesMessage = message;
                AsusServicesDetail = detail;
                IsAsusServicesChanging = busy;
            });

            _displayService.DisplayStatusChanged += snapshot =>
            {
                QueueUiUpdate(() =>
                {
                    RefreshRate = snapshot.Frequency;
                    IsOverdrive = snapshot.Overdrive > 0;
                    IsOverdriveAvailable = snapshot.OverdriveSetting;
                    IsMiniLed = snapshot.Miniled1 > 0 || snapshot.Miniled2 > 0;
                    IsAutoRefresh = snapshot.ScreenAuto;
                });
            };
            _displayService.PanelBrightnessChanged += level => QueueUiUpdate(() =>
            {
                _applyingExternalBrightness = true;
                try { PanelBrightness = level; }
                finally { _applyingExternalBrightness = false; }
            });
            _displayService.ColorPipelineStateChanged += _ => QueueUiUpdate(() =>
                OnPropertyChanged(nameof(IsOledDimmingAvailable)));
            _batteryService.ChargeLimitChanged += value => QueueUiUpdate(() => ChargeLimit = value);
            _batteryService.FullChargeOverrideChanged += value => QueueUiUpdate(() => IsFullChargeOverride = value);
            _lightingService.BrightnessChanged += value => QueueUiUpdate(() => KeyboardBrightness = value);
            _inputDeviceService.TouchpadStateChanged += value => QueueUiUpdate(() => IsTouchpadEnabled = value);

            // Seeding the properties above must not write back to the hardware.
            _isReady = true;

            RefreshPerformancePlans();
            LoadTiles();
        }

        [RelayCommand]
        public void SelectPerformanceMode(object? modeParam)
        {
            int mode = ToInt(modeParam, 0);
            SelectedPerformanceMode = mode;
            _performanceService.SetMode(mode);
        }

        [RelayCommand]
        public void SelectQuickPerformancePlan(object? planParam)
        {
            if (planParam is PerformancePlanItem plan) SelectPerformanceMode(plan.ModeIndex);
        }

        [RelayCommand]
        public void OpenPerformanceShortcuts() => OpenDetail(QuickDetailPage.PerformanceShortcuts);

        [RelayCommand]
        public void ResetPerformanceShortcuts()
        {
            _isRefreshingPerformancePlans = true;
            try
            {
                QuickPlan1 = PerformancePlans.FirstOrDefault(plan => plan.ModeIndex == 2) ?? PerformancePlans.ElementAtOrDefault(0);
                QuickPlan2 = PerformancePlans.FirstOrDefault(plan => plan.ModeIndex == 0) ?? PerformancePlans.ElementAtOrDefault(1);
                QuickPlan3 = PerformancePlans.FirstOrDefault(plan => plan.ModeIndex == 1) ?? PerformancePlans.ElementAtOrDefault(2);
            }
            finally
            {
                _isRefreshingPerformancePlans = false;
            }
            SavePerformanceShortcuts();
            RaisePerformanceShortcutSelection();
        }

        private void RefreshPerformancePlans()
        {
            int[] preferred = ReadPerformanceShortcutIds();
            _isRefreshingPerformancePlans = true;
            try
            {
                PerformancePlans.Clear();
                foreach (PerformancePlanInfo plan in _performanceService.GetProfiles())
                    PerformancePlans.Add(new PerformancePlanItem(plan));

                List<PerformancePlanItem> choices = new();
                foreach (int id in preferred.Concat(new[] { 2, 0, 1 }).Concat(PerformancePlans.Select(plan => plan.ModeIndex)))
                {
                    PerformancePlanItem? plan = PerformancePlans.FirstOrDefault(item => item.ModeIndex == id);
                    if (plan is not null && choices.All(item => item.ModeIndex != id)) choices.Add(plan);
                    if (choices.Count == 3) break;
                }

                QuickPlan1 = choices.ElementAtOrDefault(0);
                QuickPlan2 = choices.ElementAtOrDefault(1);
                QuickPlan3 = choices.ElementAtOrDefault(2);
            }
            finally
            {
                _isRefreshingPerformancePlans = false;
            }
            SavePerformanceShortcuts();
            RaisePerformanceShortcutSelection();
        }

        private static int[] ReadPerformanceShortcutIds() => (AppConfig.GetString("quick_performance_plans") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, out int mode) ? mode : -1)
            .Where(mode => mode >= 0)
            .Take(3)
            .ToArray();

        private void SavePerformanceShortcuts()
        {
            int[] values = new[] { QuickPlan1?.ModeIndex ?? -1, QuickPlan2?.ModeIndex ?? -1, QuickPlan3?.ModeIndex ?? -1 };
            if (values.Any(value => value < 0)) return;
            AppConfig.Set("quick_performance_plans", string.Join(',', values));
        }

        partial void OnQuickPlan1Changed(PerformancePlanItem? oldValue, PerformancePlanItem? newValue) =>
            OnPerformanceShortcutChanged(1, oldValue, newValue);

        partial void OnQuickPlan2Changed(PerformancePlanItem? oldValue, PerformancePlanItem? newValue) =>
            OnPerformanceShortcutChanged(2, oldValue, newValue);

        partial void OnQuickPlan3Changed(PerformancePlanItem? oldValue, PerformancePlanItem? newValue) =>
            OnPerformanceShortcutChanged(3, oldValue, newValue);

        private void OnPerformanceShortcutChanged(int slot, PerformancePlanItem? oldValue, PerformancePlanItem? newValue)
        {
            if (_isRefreshingPerformancePlans || newValue is null) return;

            _isRefreshingPerformancePlans = true;
            try
            {
                if (slot != 1 && QuickPlan1?.ModeIndex == newValue.ModeIndex) QuickPlan1 = oldValue;
                if (slot != 2 && QuickPlan2?.ModeIndex == newValue.ModeIndex) QuickPlan2 = oldValue;
                if (slot != 3 && QuickPlan3?.ModeIndex == newValue.ModeIndex) QuickPlan3 = oldValue;
            }
            finally
            {
                _isRefreshingPerformancePlans = false;
            }
            SavePerformanceShortcuts();
            RaisePerformanceShortcutSelection();
        }

        private void RaisePerformanceShortcutSelection()
        {
            OnPropertyChanged(nameof(IsQuickPlan1Selected));
            OnPropertyChanged(nameof(IsQuickPlan2Selected));
            OnPropertyChanged(nameof(IsQuickPlan3Selected));
        }

        [RelayCommand]
        public void SelectGpuMode(object? modeParam)
        {
            int mode = ToInt(modeParam, 0);
            SelectedGpuMode = mode;
            _gpuService.SetGpuMode(mode);
        }

        [RelayCommand]
        public void ToggleRefreshRate()
        {
            // Two-state tile: the minimum rate, or the best the panel offers - which is
            // the overdrive variant wherever that exists.
            SetRefreshRate(RefreshRate <= Arsenal.Display.ScreenControl.MIN_RATE
                ? (IsOverdriveAvailable ? "max_od" : "max")
                : "min");
        }

        [RelayCommand]
        public void ToggleOverdrive()
        {
            IsOverdrive = !IsOverdrive;
            _displayService.SetOverdrive(IsOverdrive);
        }

        [RelayCommand]
        public void ToggleMiniLed()
        {
            if (!IsMiniLedSupported) return;
            _displayService.ToggleMiniLed();
            IsMiniLed = !IsMiniLed;
        }

        [RelayCommand]
        public void ToggleAutoRefresh()
        {
            IsAutoRefresh = !IsAutoRefresh;
            _displayService.SetAutoRefresh(IsAutoRefresh);
        }

        [RelayCommand]
        public void ToggleHardwareOverlay()
        {
            SetOverlayMode(IsHardwareOverlay ? 0 : OverlayGameOnly ? 2 : 1);
        }

        /// <summary>0 = off, 1 = always visible, 2 = visible in games only.</summary>
        private void SetOverlayMode(int mode)
        {
            mode = Math.Clamp(mode, 0, 2);
            bool wasEnabled = IsHardwareOverlay;
            bool enable = mode != 0;
            bool gameOnly = mode switch
            {
                1 => false,
                2 => true,
                _ => OverlayGameOnly
            };
            bool modeChanged = enable && gameOnly != OverlayGameOnly;

            AppConfig.Set("overlay", enable ? 1 : 0);
            if (mode != 0) AppConfig.Set("overlay_game_only", gameOnly ? 1 : 0);

            if (wasEnabled && (!enable || modeChanged)) Program.hardwareOverlay?.StopOverlay();
            if (enable && (!wasEnabled || modeChanged)) Program.hardwareOverlay?.StartOverlay();

            IsHardwareOverlay = enable;
            if (mode != 0) OverlayGameOnly = gameOnly;
            SyncDetailSelection();
            RefreshTiles();
        }

        [RelayCommand]
        public void ToggleFullCharge()
        {
            _batteryService.ToggleFullChargeOverride();
            IsFullChargeOverride = _batteryService.IsFullChargeOverride;
        }

        [RelayCommand]
        public void UpdateChargeLimit(object? limitParam)
        {
            int limit = ToInt(limitParam, 80);
            ChargeLimit = limit;
            _batteryService.SetChargeLimit(limit);
        }

        [RelayCommand]
        public void CycleKeyboardBrightness()
        {
            _lightingService.CycleBrightness();
            KeyboardBrightness = _lightingService.Brightness;
        }

        [RelayCommand]
        public void SetKeyboardBrightness(object? levelParam)
        {
            int level = Math.Clamp(ToInt(levelParam, KeyboardBrightness), 0, 3);
            _lightingService.SetBrightness(level);
            KeyboardBrightness = level;
        }

        /// <summary>
        /// Reaches the panel on every change, including the ones the slider produces
        /// mid-drag. The writer keeps the underlying WMI call off the UI thread and
        /// bounds how often it runs.
        /// </summary>
        partial void OnPanelBrightnessChanged(int value)
        {
            // A value that arrived from the hardware watcher is already applied.
            // Writing it back would fight the brightness keys mid-press.
            if (_isReady && !_applyingExternalBrightness) _panelBrightnessWriter.Push(Math.Clamp(value, 0, 100));
        }

        partial void OnOledDimmingChanged(int value)
        {
            if (_isReady && IsOledDimmingAvailable) _oledDimmingWriter.Push(Math.Clamp(value, 0, 100));
        }

        // ===== Tile state =====================================================
        // Each tile shows what it currently is, so the panel reads without having to
        // open anything.

        public string PerformanceModeName => Modes.GetName(SelectedPerformanceMode);

        public string GpuModeName => SelectedGpuMode switch
        {
            0 => "Eco · iGPU only",
            1 => "Standard · hybrid",
            2 => "Ultimate · MUX",
            _ => "Optimized · automatic"
        };

        public string DisplayModeName => $"{RefreshRate} Hz"
            + (RefreshRate > Arsenal.Display.ScreenControl.MIN_RATE && IsOverdrive ? " + OD" : string.Empty)
            + (IsAutoRefresh ? " · auto" : string.Empty);

        public string KeyboardBrightnessName => KeyboardBrightness switch
        {
            0 => "Off",
            1 => "Low",
            2 => "Medium",
            _ => "Maximum"
        };

        public string OverlayStateName => !IsHardwareOverlay
            ? "Off"
            : OverlayGameOnly ? "Games only" : "Always on";

        public string FullChargeStateName => IsFullChargeOverride ? "Charging to 100%" : $"Stops at {ChargeLimit}%";

        partial void OnSelectedPerformanceModeChanged(int value)
        {
            OnPropertyChanged(nameof(PerformanceModeName));
            RaisePerformanceShortcutSelection();
            SyncDetailSelection();
            RefreshTiles();
        }

        partial void OnSelectedGpuModeChanged(int value)
        {
            OnPropertyChanged(nameof(GpuModeName));
            SyncDetailSelection();
            RefreshTiles();
        }

        partial void OnRefreshRateChanged(int value)
        {
            OnPropertyChanged(nameof(DisplayModeName));
            SyncDetailSelection();
            RefreshTiles();
        }

        partial void OnIsOverdriveChanged(bool value)
        {
            OnPropertyChanged(nameof(DisplayModeName));
            SyncDetailSelection();
            RefreshTiles();
        }

        partial void OnIsAutoRefreshChanged(bool value)
        {
            OnPropertyChanged(nameof(DisplayModeName));
            SyncDetailSelection();
            RefreshTiles();
        }

        partial void OnIsMiniLedChanged(bool value) => RefreshTiles();

        partial void OnIsOverdriveAvailableChanged(bool value)
        {
            // The maximum-rate option is labelled with or without "+ OD", so an open
            // detail page has to be rebuilt when availability changes.
            if (DetailPage == QuickDetailPage.Display) OpenDetail(QuickDetailPage.Display);
            RefreshTiles();
        }

        partial void OnKeyboardBrightnessChanged(int value)
        {
            OnPropertyChanged(nameof(KeyboardBrightnessName));
            SyncDetailSelection();
            RefreshTiles();
        }

        partial void OnIsHardwareOverlayChanged(bool value)
        {
            OnPropertyChanged(nameof(OverlayStateName));
            RefreshTiles();
        }

        partial void OnOverlayGameOnlyChanged(bool value)
        {
            OnPropertyChanged(nameof(OverlayStateName));
            SyncDetailSelection();
            RefreshTiles();
        }

        partial void OnIsTouchpadEnabledChanged(bool value) => RefreshTiles();

        partial void OnIsFullChargeOverrideChanged(bool value)
        {
            OnPropertyChanged(nameof(FullChargeStateName));
            RefreshTiles();
        }

        partial void OnChargeLimitChanged(int value)
        {
            OnPropertyChanged(nameof(FullChargeStateName));
            SyncDetailSelection();
            RefreshTiles();
        }

        // ===== Tiles ==========================================================
        // Which tiles the grid shows is the user's choice, held as a list of catalogue
        // keys. Everything a tile needs - what it reads, what pressing it does, whether
        // this machine can offer it at all - is resolved from that key here, so adding a
        // tile is one catalogue entry and one arm of each switch below rather than a new
        // set of bindings in the window.

        public ObservableCollection<QuickTileSlot> Tiles { get; } = new();

        /// <summary>
        /// In edit mode a tile press opens the picker instead of changing the setting,
        /// and each tile grows a control to take it off the grid.
        /// </summary>
        [ObservableProperty]
        private bool _isEditingTiles;

        /// <summary>The slot the picker is choosing for; null while adding a new one.</summary>
        private QuickTileSlot? _slotBeingPicked;

        /// <summary>
        /// Tiles on screen at once: three rows of two. A page taller than this makes the
        /// flyout taller than the screen space a flyout should take, so the rest is paged
        /// rather than making the panel grow.
        /// </summary>
        public const int TilesPerPage = 6;

        /// <summary>
        /// Shrinking the visible page must not discard existing choices, so the original
        /// capacity remains available across compact pages.
        /// </summary>
        private const int MaxTiles = 20;

        public bool CanAddTile => Tiles.Count < MaxTiles;
        public bool CanRemoveTile => Tiles.Count > 1;

        /// <summary>Which page of tiles is showing. Zero-based.</summary>
        [ObservableProperty]
        private int _tilePage;

        public int TilePageCount => Math.Max(1, (Tiles.Count + TilesPerPage - 1) / TilesPerPage);
        public bool HasTilePages => TilePageCount > 1;
        public bool CanGoBackTilePage => TilePage > 0;
        public bool CanGoForwardTilePage => TilePage < TilePageCount - 1;

        /// <summary>One entry per page, for the centered dots above the grid.</summary>
        public ObservableCollection<QuickTilePage> TilePages { get; } = new();

        [RelayCommand]
        public void PreviousTilePage() => TilePage = Math.Max(0, TilePage - 1);

        [RelayCommand]
        public void NextTilePage() => TilePage = Math.Min(TilePageCount - 1, TilePage + 1);

        [RelayCommand]
        public void GoToTilePage(object? indexParam) =>
            TilePage = Math.Clamp(ToInt(indexParam, 0), 0, TilePageCount - 1);

        partial void OnTilePageChanged(int value) => RaiseTilePageDependents();

        private void RaiseTilePageDependents()
        {
            int pages = TilePageCount;
            if (TilePage > pages - 1) { TilePage = pages - 1; return; }

            while (TilePages.Count > pages) TilePages.RemoveAt(TilePages.Count - 1);
            while (TilePages.Count < pages) TilePages.Add(new QuickTilePage(TilePages.Count));
            for (int i = 0; i < TilePages.Count; i++) TilePages[i].IsCurrent = i == TilePage;

            OnPropertyChanged(nameof(TilePageCount));
            OnPropertyChanged(nameof(HasTilePages));
            OnPropertyChanged(nameof(CanGoBackTilePage));
            OnPropertyChanged(nameof(CanGoForwardTilePage));
        }

        partial void OnIsEditingTilesChanged(bool value)
        {
            // Leaving edit mode with the picker open would strand it on a page that
            // cannot be reached any other way.
            if (!value && DetailPage == QuickDetailPage.TilePicker) CloseDetail();
        }

        /// <summary>
        /// Moves a tile to another position, which is what dragging one does. The list is
        /// flat across pages, so dragging onto the last slot of a page and on again
        /// carries the tile to the next one.
        /// </summary>
        public void MoveTile(QuickTileSlot slot, int toIndex)
        {
            int from = Tiles.IndexOf(slot);
            if (from < 0) return;

            int to = Math.Clamp(toIndex, 0, Tiles.Count - 1);
            if (to == from) return;

            Tiles.Move(from, to);
        }

        /// <summary>Persists the order after a drag has finished.</summary>
        public void CommitTileOrder() => SaveTiles();

        private void LoadTiles()
        {
            string saved = AppConfig.GetString("quick_tiles") ?? string.Empty;
            List<string> keys = saved
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(MigrateTileKey)
                .Where(IsTileAvailable)
                .Distinct(StringComparer.Ordinal)
                .Take(MaxTiles)
                .ToList();

            // Nothing saved, or a saved layout this machine can no longer offer any of.
            if (keys.Count == 0) keys = QuickTileCatalog.Defaults.Where(IsTileAvailable).ToList();

            Tiles.Clear();
            foreach (string key in keys)
            {
                if (QuickTileCatalog.Find(key) is { } definition) Tiles.Add(CreateSlot(definition));
            }

            RaiseTileCountDependents();
            RefreshTiles();
        }

        private string MigrateTileKey(string key) => key switch
        {
            // The two old overlay controls now share one three-state tile. Preserve the
            // first position occupied by either key and let Distinct remove the second.
            "overlay_gaming" => "overlay",

            // A non-touch laptop could previously persist a useless touchscreen tile.
            // Put the usable touchpad control in that same slot when one is present.
            "touchscreen" when !HasTouchScreen && HasTouchpad => "touchpad",
            _ => key
        };

        private QuickTileSlot CreateSlot(QuickTileDefinition definition) =>
            new(definition, definition.Detail == QuickDetailPage.None ? null : OpenTileMenuCommand);

        private void SaveTiles()
        {
            AppConfig.Set("quick_tiles", string.Join(",", Tiles.Select(tile => tile.Key)));
            RaiseTileCountDependents();
        }

        private void RaiseTileCountDependents()
        {
            OnPropertyChanged(nameof(CanAddTile));
            OnPropertyChanged(nameof(CanRemoveTile));
            RaiseTilePageDependents();
        }

        /// <summary>Re-reads every tile on the grid. Cheap: only what is on it is asked.</summary>
        private void RefreshTiles()
        {
            foreach (QuickTileSlot tile in Tiles)
            {
                tile.State = TileState(tile.Key);
                tile.IsChecked = TileIsOn(tile.Key);
            }
        }

        [RelayCommand]
        public void ToggleTileEditing() => IsEditingTiles = !IsEditingTiles;

        /// <summary>The tile body. Changes the setting, or picks a new tile while editing.</summary>
        [RelayCommand]
        public void ActivateTile(object? slotParam)
        {
            if (slotParam is not QuickTileSlot slot) return;

            if (IsEditingTiles)
            {
                _slotBeingPicked = slot;
                OpenDetail(QuickDetailPage.TilePicker);
                return;
            }

            TileActivate(slot.Key);
        }

        /// <summary>The chevron. Opens the tile's own list of states.</summary>
        [RelayCommand]
        public void OpenTileMenu(object? slotParam)
        {
            if (slotParam is not QuickTileSlot slot) return;
            if (IsEditingTiles) { ActivateTile(slot); return; }
            if (slot.Definition.Detail != QuickDetailPage.None) OpenDetail(slot.Definition.Detail);
        }

        [RelayCommand]
        public void RemoveTile(object? slotParam)
        {
            if (slotParam is not QuickTileSlot slot || !CanRemoveTile) return;
            Tiles.Remove(slot);
            if (ReferenceEquals(_slotBeingPicked, slot)) _slotBeingPicked = null;
            SaveTiles();
        }

        /// <summary>Adds a slot by opening the picker with no slot to replace.</summary>
        [RelayCommand]
        public void AddTile()
        {
            if (!CanAddTile) return;
            IsEditingTiles = true;
            _slotBeingPicked = null;
            OpenDetail(QuickDetailPage.TilePicker);
        }

        [RelayCommand]
        public void ResetTiles()
        {
            // Removed rather than blanked, so the grid goes back to having no saved
            // preference at all and follows the defaults if those ever change.
            AppConfig.Remove("quick_tiles");
            _slotBeingPicked = null;
            LoadTiles();
            if (DetailPage == QuickDetailPage.TilePicker) CloseDetail();
        }

        /// <summary>Puts the chosen tile into the slot being edited, or on the end.</summary>
        private void ApplyPickedTile(string? key)
        {
            if (QuickTileCatalog.Find(key) is not { } definition) return;

            // The same tile twice would give two controls that always agree, and pressing
            // either would look like a bug in the other. Moving it is the useful reading.
            QuickTileSlot? duplicate = Tiles.FirstOrDefault(
                tile => string.Equals(tile.Key, definition.Key, StringComparison.Ordinal));

            if (_slotBeingPicked is { } target)
            {
                int index = Tiles.IndexOf(target);
                if (index < 0) return;
                if (duplicate is not null && !ReferenceEquals(duplicate, target)) Tiles.Remove(duplicate);
                index = Tiles.IndexOf(target);
                Tiles[index] = CreateSlot(definition);
            }
            else
            {
                if (duplicate is not null) Tiles.Remove(duplicate);
                if (!CanAddTile) return;
                Tiles.Add(CreateSlot(definition));
            }

            _slotBeingPicked = null;
            SaveTiles();
            RefreshTiles();
            CloseDetail();
        }

        /// <summary>Whether this machine can offer the tile at all.</summary>
        private bool IsTileAvailable(string key) => key switch
        {
            "gpu" => _gpuService.HasDedicatedGpu || IsEcoSupported || IsMuxSupported,
            "kill_gpu_apps" or "restart_nv" => _gpuService.HasDedicatedGpu,
            "xgm" => _gpuService.IsXgmConnected,
            "auto_tdp" or "fps_limit" => AppConfig.IsAlly(),
            "overdrive" => IsOverdriveSupported,
            "miniled" => IsMiniLedSupported,
            "resolution" => _displayService.IsResolutionToggleSupported,
            "hdr" => _displayService.IsHdrControlSupported,
            "touchscreen" => HasTouchScreen,
            "touchpad" => HasTouchpad,
            "aura" => _lightingService.HasAuraEffects,
            "matrix" => _lightingService.HasAnimeMatrix,
            "number_pad" => AppConfig.IsNumberPad(),
            _ => QuickTileCatalog.Find(key) is not null
        };

        /// <summary>The line under the tile's label: what it is set to right now.</summary>
        private string TileState(string key) => key switch
        {
            "performance" => PerformanceModeName,
            "gpu" => GpuModeName,
            "kill_gpu_apps" => "Close them now",
            "restart_nv" => "Restart them now",
            "xgm" => _gpuService.IsXgmConnected ? "Connected" : "Disconnected",
            "auto_tdp" => OnOff(Advanced.AutoTdpEnabled),
            "fps_limit" => Advanced.FpsLimit > 0 ? $"{Advanced.FpsLimit} fps" : "Off",
            "refresh" => DisplayModeName,
            "overdrive" => OnOff(IsOverdrive),
            "auto_refresh" => OnOff(IsAutoRefresh),
            "miniled" => IsMiniLed ? "Multi-zone" : "Single zone",
            "visual" => VisualControl.GetVisualModes().TryGetValue((SplendidCommand)_displayService.CurrentVisualProfile, out string? visual) ? visual : "Default",
            "gamut" => VisualControl.GetGamutModes().TryGetValue((SplendidGamut)_displayService.CurrentGamut, out string? gamut) ? gamut.Replace("Gamut: ", string.Empty) : "Native",
            "resolution" => "Switch mode",
            "hdr" => "Hand back to Windows",
            "touchscreen" => "Toggle",
            "touchpad" => OnOff(IsTouchpadEnabled),
            "full_charge" => FullChargeStateName,
            "charge_limit" => $"{ChargeLimit}%",
            "battery_report" => "Generate",
            "keyboard" => KeyboardBrightnessName,
            "aura" => Arsenal.USB.Aura.GetModes().TryGetValue((Arsenal.USB.AuraMode)_lightingService.CurrentMode, out string? aura) ? aura : "Static",
            "matrix" => MatrixBrightnessName(_lightingService.MatrixBrightness),
            "overlay" => OverlayStateName,
            "auto_switch" => OnOff(Automation.IsAutoSwitchEnabled),
            "fn_lock" => AppConfig.Is("fn_lock") ? "F1-F12" : "Media keys",
            "status_leds" => OnOff(AppConfig.IsNotFalse("status_led")),
            "number_pad" => OnOff(Arsenal.Input.NumberPad.Get() == 1),
            "clamshell" => OnOff(AppConfig.Is("clamshell")),
            "boot_sound" => OnOff(AppConfig.Is("boot_sound")),
            "aspm" => OnOff(AppConfig.IsNotFalse("aspm")),
            "standby_network" => OnOff(AppConfig.IsNotFalse("standby_networking")),
            "always_on_top" => OnOff(AppConfig.Is("topmost")),
            "power_options" => "Open",
            _ => string.Empty
        };

        /// <summary>Whether the tile draws itself lit.</summary>
        private bool TileIsOn(string key) => key switch
        {
            // Preserved from the fixed grid: the tile lights for the state that is not
            // the everyday one, so a glance at the panel finds what is out of the ordinary.
            "performance" => SelectedPerformanceMode == 1,
            "gpu" => SelectedGpuMode == 0,
            "refresh" => RefreshRate <= Arsenal.Display.ScreenControl.MIN_RATE,
            "keyboard" => KeyboardBrightness > 0,

            "xgm" => _gpuService.IsXgmConnected,
            "auto_tdp" => Advanced.AutoTdpEnabled,
            "fps_limit" => Advanced.FpsLimit > 0,
            "overdrive" => IsOverdrive,
            "auto_refresh" => IsAutoRefresh,
            "miniled" => IsMiniLed,
            "touchpad" => IsTouchpadEnabled,
            "full_charge" => IsFullChargeOverride,
            "charge_limit" => ChargeLimit < 100,
            "matrix" => _lightingService.MatrixBrightness > 0,
            "overlay" => IsHardwareOverlay,
            "auto_switch" => Automation.IsAutoSwitchEnabled,
            "fn_lock" => AppConfig.Is("fn_lock"),
            "status_leds" => AppConfig.IsNotFalse("status_led"),
            "number_pad" => Arsenal.Input.NumberPad.Get() == 1,
            "clamshell" => AppConfig.Is("clamshell"),
            "boot_sound" => AppConfig.Is("boot_sound"),
            "aspm" => AppConfig.IsNotFalse("aspm"),
            "standby_network" => AppConfig.IsNotFalse("standby_networking"),
            "always_on_top" => AppConfig.Is("topmost"),
            _ => false
        };

        /// <summary>What pressing the tile body does.</summary>
        private void TileActivate(string key)
        {
            switch (key)
            {
                case "performance": CyclePerformanceMode(); break;
                case "gpu": CycleGpuMode(); break;
                case "kill_gpu_apps": _gpuService.KillGpuApps(); break;
                case "restart_nv": _gpuService.RestartNvServices(); break;
                case "xgm": _gpuService.ToggleXgm(); break;
                case "auto_tdp": Advanced.ToggleAutoTdp(); break;
                case "fps_limit": Advanced.CycleFpsLimit(); break;

                case "refresh": ToggleRefreshRate(); break;
                case "overdrive": ToggleOverdrive(); break;
                case "auto_refresh": ToggleAutoRefresh(); break;
                case "miniled": ToggleMiniLed(); break;
                case "visual": OpenDetail(QuickDetailPage.Visual); break;
                case "gamut": OpenDetail(QuickDetailPage.Gamut); break;
                case "resolution": _displayService.ToggleResolution(); break;
                case "hdr": _displayService.ToggleHdrControl(); break;
                case "touchscreen": _displayService.ToggleTouchScreen(); break;
                case "touchpad": _inputDeviceService.ToggleTouchpad(); break;

                case "full_charge": ToggleFullCharge(); break;
                case "charge_limit": OpenDetail(QuickDetailPage.ChargeLimit); break;
                case "battery_report": _batteryService.GenerateBatteryReport(); break;

                case "keyboard": CycleKeyboardBrightness(); break;
                case "aura": OpenDetail(QuickDetailPage.Aura); break;
                case "matrix": CycleMatrixBrightness(); break;

                case "overlay": ToggleHardwareOverlay(); break;
                case "auto_switch": Automation.IsAutoSwitchEnabled = !Automation.IsAutoSwitchEnabled; break;
                case "fn_lock": Advanced.ToggleFnLock(); break;
                case "status_leds": Advanced.StatusLedEnabled = !Advanced.StatusLedEnabled; break;
                case "number_pad": Advanced.NumberPadEnabled = !Advanced.NumberPadEnabled; break;
                case "clamshell": SetClamshell(!AppConfig.Is("clamshell")); break;
                case "boot_sound": Advanced.BootSoundEnabled = !Advanced.BootSoundEnabled; break;
                case "aspm": Advanced.AspmEnabled = !Advanced.AspmEnabled; break;
                case "standby_network": Advanced.StandbyNetworkingEnabled = !Advanced.StandbyNetworkingEnabled; break;
                case "always_on_top": Advanced.AlwaysOnTop = !Advanced.AlwaysOnTop; break;
                case "power_options": Advanced.OpenPowerPlanSettings(); break;
            }

            // The hardware events cover the settings that raise one; the rest - the
            // Advanced toggles and the one-shot actions - are only known here.
            RefreshTiles();
        }

        private void CycleMatrixBrightness()
        {
            _lightingService.SetMatrixBrightness((_lightingService.MatrixBrightness + 1) % 4);
        }

        private static void SetClamshell(bool enabled)
        {
            AppConfig.Set("clamshell", enabled ? 1 : 0);
            if (enabled) Program.clamshellControl?.ToggleLidAction();
            else ClamshellModeControl.DisableClamshellMode();
        }

        private static string OnOff(bool value) => value ? "On" : "Off";

        private static string MatrixBrightnessName(int level) => level switch
        {
            0 => "Off",
            1 => "Low",
            2 => "Medium",
            _ => "Maximum"
        };

        // ===== Detail pages ===================================================

        [ObservableProperty]
        private QuickDetailPage _detailPage = QuickDetailPage.None;

        [ObservableProperty]
        private string _detailTitle = string.Empty;

        public ObservableCollection<QuickOptionItem> DetailOptions { get; } = new();

        public bool IsDetailOpen => DetailPage != QuickDetailPage.None;

        public bool IsPerformanceShortcutEditor => DetailPage == QuickDetailPage.PerformanceShortcuts;
        public bool IsStandardDetailPage => DetailPage != QuickDetailPage.PerformanceShortcuts;

        partial void OnDetailPageChanged(QuickDetailPage value)
        {
            OnPropertyChanged(nameof(IsDetailOpen));
            OnPropertyChanged(nameof(IsPerformanceShortcutEditor));
            OnPropertyChanged(nameof(IsStandardDetailPage));
        }

        [RelayCommand]
        public void OpenDetail(object? pageParam)
        {
            if (!Enum.TryParse(pageParam?.ToString(), out QuickDetailPage page)) return;

            DetailOptions.Clear();
            switch (page)
            {
                case QuickDetailPage.Performance:
                    DetailTitle = "Performance mode";
                    foreach (PerformancePlanInfo plan in _performanceService.GetProfiles())
                    {
                        string detail = plan.ModeIndex switch
                        {
                            2 => "Quietest fans, lowest power",
                            0 => "Default fan curve and limits",
                            1 => "Highest limits, loudest fans",
                            _ => "Adjust power limits, clocks, thermals, and fan curves for the selected plan."
                        };
                        DetailOptions.Add(new QuickOptionItem(page, plan.ModeIndex, plan.Name, detail));
                    }
                    break;

                case QuickDetailPage.PerformanceShortcuts:
                    DetailTitle = "Choose performance shortcuts";
                    break;

                case QuickDetailPage.Gpu:
                    DetailTitle = "GPU mode";
                    if (IsEcoSupported)
                        DetailOptions.Add(new QuickOptionItem(page, 0, "Eco", "Integrated graphics only"));
                    DetailOptions.Add(new QuickOptionItem(page, 1, "Standard", "Hybrid, apps pick a GPU"));
                    if (IsMuxSupported)
                        DetailOptions.Add(new QuickOptionItem(page, 2, "Ultimate", "Dedicated GPU drives the panel · needs a restart"));
                    DetailOptions.Add(new QuickOptionItem(page, 3, "Optimized", "Switches with the power source"));
                    break;

                case QuickDetailPage.Display:
                    DetailTitle = "Refresh rate";
                    int min = Arsenal.Display.ScreenControl.MIN_RATE;
                    int max = _displayService.MaxRefreshRate;
                    DetailOptions.Add(new QuickOptionItem(page, min, $"{min} Hz", "Longest battery life", "min"));
                    DetailOptions.Add(new QuickOptionItem(page, max, $"{max} Hz", "Smoothest motion", "max"));
                    // Offered as its own choice only where the panel has overdrive.
                    if (IsOverdriveAvailable)
                        DetailOptions.Add(new QuickOptionItem(page, max, $"{max} Hz + OD", "Fastest pixel response", "max_od"));
                    break;

                case QuickDetailPage.Keyboard:
                    DetailTitle = "Keyboard backlight";
                    DetailOptions.Add(new QuickOptionItem(page, 0, "Off"));
                    DetailOptions.Add(new QuickOptionItem(page, 1, "Low"));
                    DetailOptions.Add(new QuickOptionItem(page, 2, "Medium"));
                    DetailOptions.Add(new QuickOptionItem(page, 3, "Maximum"));
                    break;

                case QuickDetailPage.Aura:
                    DetailTitle = "Aura effect";
                    foreach (var mode in Arsenal.USB.Aura.GetModes())
                        DetailOptions.Add(new QuickOptionItem(page, (int)mode.Key, mode.Value));
                    break;

                case QuickDetailPage.Visual:
                    DetailTitle = "Colour profile";
                    foreach (var visual in VisualControl.GetVisualModes())
                        DetailOptions.Add(new QuickOptionItem(page, (int)visual.Key, visual.Value));
                    break;

                case QuickDetailPage.Gamut:
                    DetailTitle = "Colour gamut";
                    // The desktop prefixes these with "Gamut: " because its row has no
                    // heading; this page has one, so the prefix would only repeat it.
                    foreach (var gamut in VisualControl.GetGamutModes())
                        DetailOptions.Add(new QuickOptionItem(page, (int)gamut.Key, gamut.Value.Replace("Gamut: ", string.Empty)));
                    break;

                case QuickDetailPage.Matrix:
                    DetailTitle = "AniMe Matrix";
                    DetailOptions.Add(new QuickOptionItem(page, 0, "Off"));
                    DetailOptions.Add(new QuickOptionItem(page, 1, "Low"));
                    DetailOptions.Add(new QuickOptionItem(page, 2, "Medium"));
                    DetailOptions.Add(new QuickOptionItem(page, 3, "Maximum"));
                    break;

                case QuickDetailPage.ChargeLimit:
                    DetailTitle = "Charge limit";
                    // The same three stops the Battery page offers as presets. The free
                    // range lives on the slider at the bottom of this panel.
                    if (!HasSteppedChargeLimit)
                        DetailOptions.Add(new QuickOptionItem(page, 60, "60%", "Best for a machine that lives on mains"));
                    else
                        DetailOptions.Add(new QuickOptionItem(page, 60, "60%", "Lowest this model accepts"));
                    DetailOptions.Add(new QuickOptionItem(page, 80, "80%", "Recommended for everyday use"));
                    DetailOptions.Add(new QuickOptionItem(page, 100, "100%", "Full charge, for travelling"));
                    break;

                case QuickDetailPage.Overlay:
                    DetailTitle = "Hardware overlay";
                    DetailOptions.Add(new QuickOptionItem(page, 0, "Off", "Hide the hardware readout"));
                    DetailOptions.Add(new QuickOptionItem(page, 1, "Always on", "Show the readout on the desktop and in applications"));
                    DetailOptions.Add(new QuickOptionItem(page, 2, "Games only", "Show the readout only while a game is active"));
                    break;

                case QuickDetailPage.TilePicker:
                    DetailTitle = _slotBeingPicked is null ? "Add a tile" : "Change this tile";
                    foreach (QuickTileDefinition tile in QuickTileCatalog.All)
                    {
                        if (!IsTileAvailable(tile.Key)) continue;

                        int tileIndex = -1;
                        for (int i = 0; i < Tiles.Count; i++)
                        {
                            if (!string.Equals(Tiles[i].Key, tile.Key, StringComparison.Ordinal)) continue;
                            tileIndex = i;
                            break;
                        }

                        string? placement = tileIndex >= 0
                            ? $"Added · Page {(tileIndex / TilesPerPage) + 1}"
                            : null;
                        DetailOptions.Add(new QuickOptionItem(page, 0, tile.Label,
                            QuickTileCatalog.GroupName(tile.Group) + " · " + tile.Summary,
                            tile.Key, tile.Icon, placement));
                    }
                    break;

                default:
                    return;
            }

            DetailPage = page;
            SyncDetailSelection();
        }

        [RelayCommand]
        public void CloseDetail()
        {
            DetailPage = QuickDetailPage.None;
            DetailOptions.Clear();
        }

        /// <summary>
        /// Puts the panel back to its resting state as it closes: the plain tile grid,
        /// with no detail page and not in edit mode.
        /// </summary>
        /// <remarks>
        /// Edit mode is a transient state, not a setting. Left on, the panel reopened
        /// still outlined and with every tile showing a remove control, which reads as a
        /// mode it had got stuck in rather than one the last visit had left running.
        /// </remarks>
        public void ReturnToRest()
        {
            IsEditingTiles = false;
            _slotBeingPicked = null;
            CloseDetail();
        }

        [RelayCommand]
        public void SelectDetailOption(object? optionParam)
        {
            if (optionParam is not QuickOptionItem option) return;

            switch (option.Page)
            {
                case QuickDetailPage.Performance: SelectPerformanceMode(option.Value); break;
                case QuickDetailPage.Gpu: SelectGpuMode(option.Value); break;
                case QuickDetailPage.Display: SetRefreshRate(option.Key ?? option.Value.ToString()); break;
                case QuickDetailPage.Keyboard: SetKeyboardBrightness(option.Value); break;
                case QuickDetailPage.Aura: _lightingService.SetMode(option.Value); break;
                case QuickDetailPage.Visual:
                    _displayService.SetVisualProfile(option.Value);
                    OnPropertyChanged(nameof(IsOledDimmingAvailable));
                    break;
                case QuickDetailPage.Gamut:
                    if (_displayService.IsColorPipelineEnabled) _displayService.SetGamut(option.Value);
                    break;
                case QuickDetailPage.Matrix: _lightingService.SetMatrixBrightness(option.Value); break;
                case QuickDetailPage.ChargeLimit: UpdateChargeLimit(option.Value); break;
                case QuickDetailPage.Overlay: SetOverlayMode(option.Value); break;

                // Picking a tile replaces the grid rather than changing a setting, and
                // closes the page on its own, so there is nothing left to re-tick.
                case QuickDetailPage.TilePicker: ApplyPickedTile(option.Key); return;
            }

            SyncDetailSelection();
            RefreshTiles();
        }

        [RelayCommand]
        public void SetRefreshRate(object? hzParam)
        {
            // See DisplayViewModel.SetRefreshRate - MAX_REFRESH lets the panel's live
            // maximum be resolved at write time.
            string mode = hzParam?.ToString() ?? string.Empty;
            int minRate = Arsenal.Display.ScreenControl.MIN_RATE;
            bool withOverdrive = mode.Equals("max_od", StringComparison.OrdinalIgnoreCase);
            bool maximum = withOverdrive
                || mode.StartsWith("max", StringComparison.OrdinalIgnoreCase)
                || ToInt(hzParam, minRate) > minRate;

            _displayService.SetRefreshRate(
                maximum ? Arsenal.Display.ScreenControl.MAX_REFRESH : minRate,
                withOverdrive || (maximum && !IsOverdriveAvailable));

            IsAutoRefresh = false;
            RefreshRate = maximum ? _displayService.MaxRefreshRate : minRate;
            IsOverdrive = withOverdrive;
        }

        /// <summary>
        /// Tapping a tile body steps to the next state, the way the Windows quick
        /// settings tiles do; the chevron is there when you want to pick one directly.
        /// </summary>
        [RelayCommand]
        public void CyclePerformanceMode()
        {
            // Silent -> Balanced -> Turbo -> Silent.
            int next = SelectedPerformanceMode switch { 2 => 0, 0 => 1, _ => 2 };
            SelectPerformanceMode(next);
        }

        [RelayCommand]
        public void CycleGpuMode()
        {
            var order = new List<int>();
            if (IsEcoSupported) order.Add(0);
            order.Add(1);
            if (IsMuxSupported) order.Add(2);
            order.Add(3);

            int index = order.IndexOf(SelectedGpuMode);
            SelectGpuMode(order[(index + 1) % order.Count]);
        }

        private void SyncDetailSelection()
        {
            foreach (var option in DetailOptions)
            {
                option.IsSelected = option.Page switch
                {
                    QuickDetailPage.Performance => option.Value == SelectedPerformanceMode,
                    QuickDetailPage.Gpu => option.Value == SelectedGpuMode,
                    // With no overdrive choice available the plain maximum stays lit
                    // whatever the overdrive register reads.
                    QuickDetailPage.Display => option.Key switch
                    {
                        "max_od" => IsOverdriveAvailable && IsAtMaximumRate && IsOverdrive,
                        "max" => IsAtMaximumRate && (!IsOverdriveAvailable || !IsOverdrive),
                        _ => !IsAutoRefresh && RefreshRate > 0 && RefreshRate <= Arsenal.Display.ScreenControl.MIN_RATE,
                    },
                    QuickDetailPage.Keyboard => option.Value == KeyboardBrightness,
                    QuickDetailPage.Aura => option.Value == _lightingService.CurrentMode,
                    QuickDetailPage.Visual => option.Value == _displayService.CurrentVisualProfile,
                    QuickDetailPage.Gamut => option.Value == _displayService.CurrentGamut,
                    QuickDetailPage.Matrix => option.Value == _lightingService.MatrixBrightness,
                    QuickDetailPage.ChargeLimit => option.Value == ChargeLimit,
                    QuickDetailPage.Overlay => option.Value == (!IsHardwareOverlay ? 0 : OverlayGameOnly ? 2 : 1),

                    // The picker lists what a slot could become, so nothing there is the
                    // current state of anything.
                    _ => false
                };
            }
        }

        [RelayCommand]
        public void SetPanelBrightness(object? valueParam)
        {
            int value = Math.Clamp(ToInt(valueParam, PanelBrightness), 0, 100);
            PanelBrightness = value;
            _panelBrightnessWriter.Push(value);
        }

        private static int ToInt(object? param, int defaultValue = 0)
        {
            if (param == null) return defaultValue;
            if (param is int i) return i;
            if (int.TryParse(param.ToString(), out int parsed)) return parsed;
            return defaultValue;
        }
    }
}
