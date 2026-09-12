using Arsenal.Helpers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Arsenal.Application.Models;
using Arsenal.Application.Services.Contracts;
using Arsenal.Display;
using Arsenal.UI.Services;
using System.Collections.ObjectModel;
using Wpf.Ui.Controls;
using MediaColor = System.Windows.Media.Color;

namespace Arsenal.UI.ViewModels
{
    public sealed record HomeControlDefinition(string Key, string Label, string Summary, SymbolRegular Icon);

    public sealed class HomeControlSlot
    {
        public HomeControlSlot(HomeControlDefinition definition) => Definition = definition;
        public HomeControlDefinition Definition { get; }
        public string Key => Definition.Key;
        public string Label => Definition.Label;
        public string Summary => Definition.Summary;
        public SymbolRegular Icon => Definition.Icon;
    }

    public partial class HomeControlChoice : ObservableObject
    {
        public HomeControlChoice(HomeControlDefinition definition) => Definition = definition;
        public HomeControlDefinition Definition { get; }
        public string Key => Definition.Key;
        public string Label => Definition.Label;
        public string Summary => Definition.Summary;
        public SymbolRegular Icon => Definition.Icon;

        [ObservableProperty]
        private bool _isAdded;
    }

    public partial class HomeViewModel : ObservableObject
    {
        private readonly IDeviceStateService _deviceStateService;
        private readonly IPerformanceService _performanceService;
        private readonly IGpuService _gpuService;
        private readonly IBatteryService _batteryService;
        private readonly IDisplayService _displayService;
        private readonly ILightingService _lightingService;
        private readonly IInputDeviceService _inputDeviceService;
        private bool _isReady;

        private static readonly IReadOnlyList<HomeControlDefinition> ControlCatalog = new[]
        {
            new HomeControlDefinition("performance", AppStrings.Get("HomePerformanceMode"), AppStrings.Get("FeatureSilentBalancedAndTurboBIOS"), SymbolRegular.Gauge24),
            new HomeControlDefinition("gpu", AppStrings.Get("DisplayGPUMode"), AppStrings.Get("FeatureEcoStandardUltimateAndOptimized"), SymbolRegular.DeveloperBoard24),
            new HomeControlDefinition("charge_limit", AppStrings.Get("BatteryChargeLimit2"), AppStrings.Get("FeatureProtectTheBatteryByChoosing"), SymbolRegular.BatteryCharge24),
            new HomeControlDefinition("refresh", AppStrings.Get("DisplayRefreshRate"), AppStrings.Get("FeatureSwitchBetweenThePanelS"), SymbolRegular.Desktop24),
            new HomeControlDefinition("auto_refresh", AppStrings.Get("HomeAutomaticRefresh"), AppStrings.Get("FeatureLetRefreshRateFollowThe"), SymbolRegular.Sparkle24),
            new HomeControlDefinition("keyboard", AppStrings.Get("HomeKeyboardLight"), AppStrings.Get("FeatureChooseTheKeyboardBacklightLevel"), SymbolRegular.Keyboard24),
            new HomeControlDefinition("touchpad", AppStrings.Get("HomeTouchpad"), AppStrings.Get("FeatureEnableOrDisableTheBuilt"), SymbolRegular.CursorClick24),
            new HomeControlDefinition("full_charge", AppStrings.Get("HomeFullCharge"), AppStrings.Get("FeatureTemporarilyChargeTo100Beyond"), SymbolRegular.BatteryCharge24),
        };

        public ObservableCollection<HomeControlSlot> HomeControls { get; } = new();
        public ObservableCollection<HomeControlChoice> HomeControlChoices { get; } = new();

        [ObservableProperty]
        private bool _isEditingControls;

        public bool CanRemoveHomeControl => HomeControls.Count > 1;

        [ObservableProperty]
        private HardwareTelemetry _telemetry = new();

        [ObservableProperty]
        private int _currentPerformanceMode = 0;

        [ObservableProperty]
        private int _currentGpuMode = 0;

        [ObservableProperty]
        private int _batteryPercent = 80;

        [ObservableProperty]
        private float _batteryDischarge = 0;

        [ObservableProperty]
        private int _refreshRate = 60;

        [ObservableProperty]
        private string _deviceName = AppStrings.Get("FeatureASUSDevice");

        [ObservableProperty]
        private string _modelNumber = string.Empty;

        /// <summary>
        /// The picture of this exact laptop, once <see cref="DeviceImageService"/> has
        /// found and decoded one. Null until then, and on machines ASUS shipped no
        /// render for it stays null - Home is laid out to do without it.
        /// </summary>
        [ObservableProperty]
        private System.Windows.Media.ImageSource _deviceImage;

        public bool HasDeviceImage => DeviceImage is not null;

        partial void OnDeviceImageChanged(System.Windows.Media.ImageSource value)
            => OnPropertyChanged(nameof(HasDeviceImage));

        [ObservableProperty]
        private int _chargeLimit = 80;

        [ObservableProperty]
        private int _keyboardBrightness;

        [ObservableProperty]
        private bool _isTouchpadEnabled = true;

        [ObservableProperty]
        private bool _isFullChargeOverride;

        [ObservableProperty]
        private bool _isAutoRefresh;

        public int MaximumRefreshRate => _displayService.MaxRefreshRate;
        public int MinimumRefreshRate => Arsenal.Display.ScreenControl.MIN_RATE;
        public bool IsAtMinimumRefreshRate => !IsAutoRefresh && RefreshRate <= MinimumRefreshRate;
        public bool IsAtMaximumRefreshRate => !IsAutoRefresh && RefreshRate > MinimumRefreshRate;

        partial void OnRefreshRateChanged(int value)
        {
            OnPropertyChanged(nameof(IsAtMinimumRefreshRate));
            OnPropertyChanged(nameof(IsAtMaximumRefreshRate));
        }

        /// <summary>
        /// Home carries the charge limit as well as the two mode switches, so the three
        /// things worth changing without leaving the first page are all on it. The bounds
        /// come from the same model rule the Battery page uses - see
        /// <see cref="BatteryViewModel.HasSteppedChargeLimit"/> - because a slider that
        /// offers a level the firmware rounds away is worse than one that does not offer it.
        /// </summary>
        public bool HasSteppedChargeLimit { get; } = AppConfig.IsChargeLimit6080();

        public int ChargeLimitMinimum => HasSteppedChargeLimit ? 60 : 40;

        public System.Windows.Media.DoubleCollection ChargeLimitTicks { get; }

        public System.Windows.Media.DoubleCollection ChargeLimitMarks { get; }

        public string ChargeLimitNote => HasSteppedChargeLimit
            ? AppStrings.Get("FeatureChargingStopsAtTheSet")
            : AppStrings.Get("FeatureChargingStopsAtTheSet2");

        /// <summary>
        /// What the selected GPU mode means, under the row's own label - the same shape
        /// the other two rows use rather than a mode name repeated back.
        /// </summary>
        public string GpuModeDescription => CurrentGpuMode switch
        {
            0 => AppStrings.Get("FeatureEcoTheDedicatedGPUIs"),
            1 => AppStrings.Get("FeatureStandardHybridApplicationsChoose"),
            2 => AppStrings.Get("FeatureUltimateTheDedicatedGPUDrives"),
            _ => AppStrings.Get("FeatureOptimizedFollowsThePowerSource")
        };

        partial void OnCurrentGpuModeChanged(int value) => OnPropertyChanged(nameof(GpuModeDescription));

        public HomeViewModel(
            IDeviceStateService deviceStateService,
            IPerformanceService performanceService,
            IGpuService gpuService,
            IBatteryService batteryService,
            IDisplayService displayService,
            ILightingService lightingService,
            IInputDeviceService inputDeviceService)
        {
            if (HasSteppedChargeLimit)
            {
                var ticks = new System.Windows.Media.DoubleCollection();
                for (int value = 60; value <= 80; value++) ticks.Add(value);
                ticks.Add(100);
                ticks.Freeze();
                ChargeLimitTicks = ticks;
                var marks = new System.Windows.Media.DoubleCollection { 60, 80, 100 };
                marks.Freeze();
                ChargeLimitMarks = marks;
            }
            else
            {
                var ticks = new System.Windows.Media.DoubleCollection();
                ticks.Freeze();
                ChargeLimitTicks = ticks;
                var marks = new System.Windows.Media.DoubleCollection { 40, 100 };
                marks.Freeze();
                ChargeLimitMarks = marks;
            }

            _deviceStateService = deviceStateService;
            _performanceService = performanceService;
            _gpuService = gpuService;
            _batteryService = batteryService;
            _displayService = displayService;
            _lightingService = lightingService;
            _inputDeviceService = inputDeviceService;

            CurrentPerformanceMode = AppConfig.Get("performance_" + Program.PerformanceKey());
            CurrentGpuMode = _gpuService.CurrentGpuMode;
            BatteryPercent = _batteryService.BatteryPercent;
            ChargeLimit = _batteryService.ChargeLimit;
            RefreshRate = _displayService.CurrentRefreshRate;
            KeyboardBrightness = _lightingService.Brightness;
            IsTouchpadEnabled = _inputDeviceService.IsTouchpadEnabled;
            IsFullChargeOverride = _batteryService.IsFullChargeOverride;
            IsAutoRefresh = _displayService.IsAutoRefreshEnabled;
            DeviceName = AppConfig.GetModelDisplayName();
            ModelNumber = AppConfig.GetModelShort();

            // Decoded off the UI thread and dropped in when it is ready. Home paints
            // immediately either way; a multi-megabyte PNG is not worth a stalled
            // first frame, and on most machines the cached copy arrives within one.
            _ = LoadDeviceImageAsync();

            _deviceStateService.TelemetryUpdated += (t) =>
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    Telemetry = t;
                    BatteryPercent = t.BatteryPercentage;
                    BatteryDischarge = t.BatteryDischargeRate;
                });
            };

            _performanceService.ModeChanged += mode => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                CurrentPerformanceMode = mode;
            });
            _gpuService.GpuModeChanged += mode => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => CurrentGpuMode = mode);
            _batteryService.ChargeLimitChanged += limit => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => ChargeLimit = limit);
            _batteryService.FullChargeOverrideChanged += value => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => IsFullChargeOverride = value);
            _lightingService.BrightnessChanged += value => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => KeyboardBrightness = value);
            _inputDeviceService.TouchpadStateChanged += value => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => IsTouchpadEnabled = value);
            _displayService.DisplayStatusChanged += snapshot => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                RefreshRate = snapshot.Frequency;
                IsAutoRefresh = snapshot.ScreenAuto;
                OnPropertyChanged(nameof(MaximumRefreshRate));
            });

            LoadHomeControls();
            _isReady = true;
        }

        /// <summary>
        /// Awaited from the constructor without blocking it. The continuation lands
        /// back on the UI thread because that is where the view model is built, so
        /// the assignment needs no dispatcher hop of its own.
        /// </summary>
        private async Task LoadDeviceImageAsync()
        {
            System.Windows.Media.ImageSource image = await DeviceImageService.GetAsync();
            if (image is not null) DeviceImage = image;
        }

        private bool IsHomeControlAvailable(HomeControlDefinition definition) => definition.Key switch
        {
            "touchpad" => _inputDeviceService.HasTouchpad,
            _ => true
        };

        private void LoadHomeControls()
        {
            List<string> keys = (AppConfig.GetString("home_controls") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (keys.Count == 0) keys = new List<string> { "performance", "gpu", "charge_limit" };

            HomeControls.Clear();
            foreach (string key in keys)
            {
                HomeControlDefinition? definition = ControlCatalog.FirstOrDefault(item => item.Key == key);
                if (definition is not null && IsHomeControlAvailable(definition))
                    HomeControls.Add(new HomeControlSlot(definition));
            }
            if (HomeControls.Count == 0)
                HomeControls.Add(new HomeControlSlot(ControlCatalog[0]));

            HomeControlChoices.Clear();
            foreach (HomeControlDefinition definition in ControlCatalog.Where(IsHomeControlAvailable))
                HomeControlChoices.Add(new HomeControlChoice(definition));
            RefreshHomeControlChoices();
        }

        private void SaveHomeControls()
        {
            AppConfig.Set("home_controls", string.Join(',', HomeControls.Select(item => item.Key)));
            OnPropertyChanged(nameof(CanRemoveHomeControl));
            RefreshHomeControlChoices();
        }

        private void RefreshHomeControlChoices()
        {
            var added = HomeControls.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
            foreach (HomeControlChoice choice in HomeControlChoices) choice.IsAdded = added.Contains(choice.Key);
        }

        [RelayCommand]
        public void ToggleControlEditing() => IsEditingControls = !IsEditingControls;

        [RelayCommand]
        public void AddHomeControl(object? keyParam)
        {
            string key = keyParam?.ToString() ?? string.Empty;
            if (HomeControls.Any(item => item.Key == key)) return;
            HomeControlDefinition? definition = ControlCatalog.FirstOrDefault(item => item.Key == key);
            if (definition is null || !IsHomeControlAvailable(definition)) return;
            HomeControls.Add(new HomeControlSlot(definition));
            SaveHomeControls();
        }

        [RelayCommand]
        public void RemoveHomeControl(object? slotParam)
        {
            if (slotParam is not HomeControlSlot slot || !CanRemoveHomeControl) return;
            HomeControls.Remove(slot);
            SaveHomeControls();
        }

        [RelayCommand]
        public void MoveHomeControlUp(object? slotParam) => MoveHomeControl(slotParam, -1);

        [RelayCommand]
        public void MoveHomeControlDown(object? slotParam) => MoveHomeControl(slotParam, 1);

        private void MoveHomeControl(object? slotParam, int delta)
        {
            if (slotParam is not HomeControlSlot slot) return;
            int from = HomeControls.IndexOf(slot);
            int to = Math.Clamp(from + delta, 0, HomeControls.Count - 1);
            if (from < 0 || from == to) return;
            HomeControls.Move(from, to);
            SaveHomeControls();
        }

        [RelayCommand]
        public void ResetHomeControls()
        {
            AppConfig.Remove("home_controls");
            LoadHomeControls();
        }

        [RelayCommand]
        public void SelectPerformanceMode(object? modeParam)
        {
            int mode = ToInt(modeParam, 0);
            CurrentPerformanceMode = mode;
            _performanceService.SetMode(mode);
        }

        [RelayCommand]
        public void SelectGpuMode(object? modeParam)
        {
            int mode = ToInt(modeParam, 0);
            CurrentGpuMode = mode;
            _gpuService.SetGpuMode(mode);
        }

        /// <summary>
        /// Writes the limit to the battery firmware. Called when the drag ends rather
        /// than on every value the slider passes through, the way the Battery page does
        /// it: each write goes to the embedded controller.
        /// </summary>
        [RelayCommand]
        public void SetChargeLimit(object? limitParam)
        {
            int limit = ToInt(limitParam, 80);
            ChargeLimit = limit;
            _batteryService.SetChargeLimit(limit);
        }

        [RelayCommand]
        public void SelectRefreshRate(object? hzParam)
        {
            int requested = ToInt(hzParam, MinimumRefreshRate);
            bool maximum = requested > MinimumRefreshRate;
            _displayService.SetRefreshRate(maximum ? Arsenal.Display.ScreenControl.MAX_REFRESH : MinimumRefreshRate);
            RefreshRate = maximum ? MaximumRefreshRate : MinimumRefreshRate;
            IsAutoRefresh = false;
        }

        [RelayCommand]
        public void SelectKeyboardBrightness(object? levelParam)
        {
            int level = Math.Clamp(ToInt(levelParam, KeyboardBrightness), 0, 3);
            KeyboardBrightness = level;
            _lightingService.SetBrightness(level);
        }

        [RelayCommand]
        public void ToggleTouchpad()
        {
            _inputDeviceService.ToggleTouchpad();
            IsTouchpadEnabled = _inputDeviceService.IsTouchpadEnabled;
        }

        [RelayCommand]
        public void ToggleFullCharge()
        {
            _batteryService.ToggleFullChargeOverride();
            IsFullChargeOverride = _batteryService.IsFullChargeOverride;
        }

        partial void OnIsAutoRefreshChanged(bool value)
        {
            OnPropertyChanged(nameof(IsAtMinimumRefreshRate));
            OnPropertyChanged(nameof(IsAtMaximumRefreshRate));
            if (_isReady) _displayService.SetAutoRefresh(value);
        }

        private static int ToInt(object? param, int defaultValue = 0)
        {
            if (param == null) return defaultValue;
            if (param is int i) return i;
            if (int.TryParse(param.ToString(), out int parsed)) return parsed;
            return defaultValue;
        }
    }

    public partial class PerformanceViewModel : ObservableObject
    {
        private readonly IPerformanceService _performanceService;
        private readonly ICoolingService _coolingService;

        [ObservableProperty]
        private int _currentMode = 1;

        [ObservableProperty]
        private int _spl = 45;

        [ObservableProperty]
        private int _sppt = 65;

        [ObservableProperty]
        private int _fppt = 80;

        [ObservableProperty]
        private int _cpuTempLimit = 95;

        [ObservableProperty]
        private int _cpuUndervolt = 0;

        [ObservableProperty]
        private int _igpuUndervolt = 0;

        [ObservableProperty]
        private bool _applyUndervolt = false;

        [ObservableProperty]
        private int _gpuCoreOffset = 0;

        [ObservableProperty]
        private int _gpuMemoryOffset = 0;

        [ObservableProperty]
        private int _gpuBoost = 25;

        [ObservableProperty]
        private int _gpuTempTarget = 87;

        [ObservableProperty]
        private int _gpuPowerTarget = 140;

        [ObservableProperty]
        private int _gpuClockLimit = 0;

        /// <summary>
        /// The top of the track is the writer's "no cap" rather than a 3000 MHz ceiling,
        /// so it reads as Default - taken from the range so a card with a different
        /// ceiling still labels its own top step.
        /// </summary>
        public string GpuClockLimitText => GpuClockLimit >= GpuClockLimitRange.Maximum
            ? AppStrings.Get("Default")
            : $"{GpuClockLimit} MHz";

        /// <summary>
        /// The TGP as a wattage rather than as the offset the firmware register takes.
        /// The stored value is the offset - that is what the writer sends - but an
        /// offset is not a number anyone recognises on a GPU, and a track labelled
        /// 0-55 W under a card running at 125 W is the same mismatch this whole pass is
        /// about. The range is shifted to match, so track, scale and readout agree.
        /// </summary>
        public int GpuPowerTotal
        {
            get => GpuPowerBaseWatts + GpuPowerTarget;
            set => GpuPowerTarget = value - GpuPowerBaseWatts;
        }

        public ControlRange GpuPowerTotalRange => new(
            GpuPowerBaseWatts + GpuPowerOffsetRange.Minimum,
            GpuPowerBaseWatts + GpuPowerOffsetRange.Maximum);

        [ObservableProperty]
        private int _fanHysteresisUp = 0;

        [ObservableProperty]
        private int _fanHysteresisDown = 0;

        [ObservableProperty]
        private bool _applyFans = true;

        [ObservableProperty]
        private bool _applyPower = true;

        [ObservableProperty]
        private int _cpuBoost = 0;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SelectedFanCurve))]
        private int _selectedFanTab = 0; // 0=CPU, 1=GPU, 2=Mid

        /// <summary>The curve the editor is showing, chosen by the segmented control.</summary>
        public FanCurveModel? SelectedFanCurve => SelectedFanTab switch
        {
            1 => GpuFanCurve,
            2 => MidFanCurve,
            _ => CpuFanCurve
        };

        /// <summary>
        /// Whether this chassis has a third fan. Models without one still answer the
        /// curve read, so the choice has to come from the model list rather than from
        /// whether a curve came back.
        /// </summary>
        public bool HasMidFan { get; } = AppConfig.Is("mid_fan");

        [RelayCommand]
        public void SelectFanTab(object? tabParam)
        {
            SelectedFanTab = tabParam is int value ? value
                : int.TryParse(tabParam?.ToString(), out int parsed) ? parsed : 0;
        }

        [ObservableProperty]
        private FanCurveModel _cpuFanCurve;

        [ObservableProperty]
        private FanCurveModel _gpuFanCurve;

        [ObservableProperty]
        private FanCurveModel _midFanCurve;

        [ObservableProperty]
        private string _calibrationStatus = "";

        [ObservableProperty]
        private bool _isCalibrating = false;

        /// <summary>Undervolt offsets this CPU will actually accept.</summary>
        public bool IsUndervoltSupported { get; }

        /// <summary>The integrated-GPU offset, supported on fewer parts still.</summary>
        public bool IsIgpuUndervoltSupported { get; }

        /// <summary>The row goes away entirely when neither offset applies.</summary>
        public bool IsUndervoltRowVisible => IsUndervoltSupported || IsIgpuUndervoltSupported;

        /// <summary>Custom fan curves need the ACPI performance-mode interface.</summary>
        public bool IsCustomFansSupported { get; }

        /// <summary>Clock and power targets are meaningless without a discrete GPU.</summary>
        public bool IsDedicatedGpuPresent { get; }

        /// <summary>
        /// What this machine will actually accept, for the sliders to draw against.
        /// Captured once: none of them change while the application is running, and a
        /// binding that re-read them would re-run the nvidia-smi probe behind
        /// <see cref="IGpuService.GpuPowerOffsetRange"/>.
        /// </summary>
        public ControlRange PowerLimitRange { get; }
        public ControlRange CpuTempRange { get; }
        public ControlRange CpuUndervoltRange { get; }
        public ControlRange IgpuUndervoltRange { get; }
        public ControlRange FanHysteresisRange { get; }
        public ControlRange GpuCoreOffsetRange { get; }
        public ControlRange GpuMemoryOffsetRange { get; }
        public ControlRange GpuClockLimitRange { get; }
        public ControlRange GpuBoostRange { get; }
        public ControlRange GpuTempRange { get; }
        public ControlRange GpuPowerOffsetRange { get; }

        /// <summary>The TGP the offset sits on top of, so the readout can show a total.</summary>
        public int GpuPowerBaseWatts { get; }

        /// <summary>Hides the TGP row on machines whose firmware has no base to offset.</summary>
        public bool IsGpuPowerAdjustable { get; }

        public PerformanceViewModel(IPerformanceService performanceService, ICoolingService coolingService, IGpuService gpuService)
        {
            _performanceService = performanceService;
            _coolingService = coolingService;

            IsUndervoltSupported = _performanceService.IsUndervoltSupported;
            IsIgpuUndervoltSupported = _performanceService.IsIgpuUndervoltSupported;
            IsCustomFansSupported = _coolingService.CustomFansSupported;
            IsDedicatedGpuPresent = gpuService.HasDedicatedGpu;

            PowerLimitRange = _performanceService.PowerLimitRange;
            CpuTempRange = _performanceService.CpuTempRange;
            CpuUndervoltRange = _performanceService.CpuUndervoltRange;
            IgpuUndervoltRange = _performanceService.IgpuUndervoltRange;
            FanHysteresisRange = _performanceService.FanHysteresisRange;

            GpuCoreOffsetRange = gpuService.GpuCoreOffsetRange;
            GpuMemoryOffsetRange = gpuService.GpuMemoryOffsetRange;
            GpuClockLimitRange = gpuService.GpuClockLimitRange;
            GpuBoostRange = gpuService.GpuBoostRange;
            GpuTempRange = gpuService.GpuTempRange;

            // Alone among these, the TGP range costs an nvidia-smi call to answer, so
            // it is asked for only where a row will draw it.
            IsGpuPowerAdjustable = IsDedicatedGpuPresent && gpuService.IsGpuPowerAdjustable;
            GpuPowerOffsetRange = IsGpuPowerAdjustable ? gpuService.GpuPowerOffsetRange : default;
            GpuPowerBaseWatts = IsGpuPowerAdjustable ? gpuService.GpuPowerBaseWatts : 0;

            LoadCurrentProfile();

            _coolingService.CalibrationStatusChanged += (s) =>
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    CalibrationStatus = s;
                    IsCalibrating = true;
                });
            };

            _coolingService.CalibrationCompleted += () =>
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    IsCalibrating = false;
                });
            };

            _performanceService.ModeChanged += _ => System.Windows.Application.Current?.Dispatcher.BeginInvoke(LoadCurrentProfile);
        }

        public void LoadCurrentProfile()
        {
            var profile = _performanceService.GetCurrentProfile();
            CurrentMode = profile.ModeIndex;

            // Clamped on the way in, not left to the slider to coerce on the way out.
            // A stored value above the track - a 150 W limit under a 140 W ceiling, a
            // 25 W boost on a part that tops out at 20 - is one the writer was already
            // dropping, so showing it unchanged claims a setting that is not in force.
            // Bringing it onto the track makes the readout, the track and the next
            // write agree, and the defaults are clamped too because they were picked
            // for a mid-range chassis rather than for this one.
            Spl = PowerLimitRange.Clamp(profile.Spl > 0 ? profile.Spl : 45);
            Sppt = PowerLimitRange.Clamp(profile.Sppt > 0 ? profile.Sppt : 65);
            Fppt = PowerLimitRange.Clamp(profile.Fppt > 0 ? profile.Fppt : 80);
            CpuTempLimit = CpuTempRange.Clamp(profile.CpuTempLimit > 0 ? profile.CpuTempLimit : CpuTempRange.Maximum);
            CpuUndervolt = CpuUndervoltRange.Clamp(profile.CpuUndervolt);
            IgpuUndervolt = IgpuUndervoltRange.Clamp(profile.IgpuUndervolt);
            ApplyUndervolt = profile.ApplyUndervolt;
            GpuCoreOffset = GpuCoreOffsetRange.Clamp(profile.GpuCoreOffset);
            GpuMemoryOffset = GpuMemoryOffsetRange.Clamp(profile.GpuMemoryOffset);
            GpuBoost = GpuBoostRange.Clamp(profile.GpuBoost >= 0 ? profile.GpuBoost : GpuBoostRange.Maximum);
            GpuTempTarget = GpuTempRange.Clamp(profile.GpuTempTarget > 0 ? profile.GpuTempTarget : GpuTempRange.Maximum);

            // Left alone where there is no base TGP to offset: with no row to draw it,
            // clamping to a collapsed range would only rewrite a stored value nothing
            // is reading.
            GpuPowerTarget = IsGpuPowerAdjustable
                ? GpuPowerOffsetRange.Clamp(profile.GpuPowerTarget >= 0 ? profile.GpuPowerTarget : GpuPowerOffsetRange.Maximum)
                : profile.GpuPowerTarget;

            // Below the minimum is the writer's "no cap", not a value to be raised into
            // range - the top of the track means the same thing and reads as "Default".
            GpuClockLimit = profile.GpuClockLimit < GpuClockLimitRange.Minimum
                ? GpuClockLimitRange.Maximum
                : GpuClockLimitRange.Clamp(profile.GpuClockLimit);
            FanHysteresisUp = FanHysteresisRange.Clamp(profile.FanHysteresisUp);
            FanHysteresisDown = FanHysteresisRange.Clamp(profile.FanHysteresisDown);
            ApplyFans = profile.ApplyFans;
            ApplyPower = profile.ApplyPower;
            CpuBoost = profile.CpuBoost >= 0 ? profile.CpuBoost : 0;

            CpuFanCurve = _coolingService.GetFanCurve(0, CurrentMode);
            GpuFanCurve = _coolingService.GetFanCurve(1, CurrentMode);
            MidFanCurve = _coolingService.GetFanCurve(2, CurrentMode);

            // The editor shows one of the three, so it has to be told they were replaced.
            OnPropertyChanged(nameof(SelectedFanCurve));
        }

        partial void OnGpuClockLimitChanged(int value) => OnPropertyChanged(nameof(GpuClockLimitText));

        partial void OnGpuPowerTargetChanged(int value) => OnPropertyChanged(nameof(GpuPowerTotal));

        [RelayCommand]
        public void SelectProfileMode(object? modeParam)
        {
            int mode = modeParam is int value ? value : int.TryParse(modeParam?.ToString(), out int parsed) ? parsed : 1;
            _performanceService.SetMode(mode);
            LoadCurrentProfile();
        }

        [RelayCommand]
        public void ApplySettings()
        {
            var profile = new PerformanceProfile
            {
                ModeIndex = CurrentMode,
                Spl = Spl,
                Sppt = Sppt,
                Fppt = Fppt,
                CpuTempLimit = CpuTempLimit,
                CpuUndervolt = CpuUndervolt,
                IgpuUndervolt = IgpuUndervolt,
                ApplyUndervolt = ApplyUndervolt,
                GpuCoreOffset = GpuCoreOffset,
                GpuMemoryOffset = GpuMemoryOffset,
                GpuBoost = GpuBoost,
                GpuTempTarget = GpuTempTarget,
                GpuPowerTarget = GpuPowerTarget,
                GpuClockLimit = GpuClockLimit,
                FanHysteresisUp = FanHysteresisUp,
                FanHysteresisDown = FanHysteresisDown,
                ApplyFans = ApplyFans,
                ApplyPower = ApplyPower,
                CpuBoost = CpuBoost
            };

            _performanceService.SaveProfile(profile);
            _coolingService.SaveFanCurve(0, CurrentMode, CpuFanCurve);
            _coolingService.SaveFanCurve(1, CurrentMode, GpuFanCurve);
            _coolingService.SaveFanCurve(2, CurrentMode, MidFanCurve);
            _coolingService.ApplyFanCurves(CurrentMode);
        }

        [RelayCommand]
        public void ResetSettings()
        {
            _performanceService.ResetProfile(CurrentMode);
            _coolingService.ResetFanCurves(CurrentMode);
            LoadCurrentProfile();
        }

        [RelayCommand]
        public void StartFanCalibration()
        {
            _coolingService.StartCalibration();
        }
    }

    public partial class DisplayViewModel : ObservableObject
    {
        private readonly IDisplayService _displayService;
        private readonly IGpuService _gpuService;
        private readonly ThrottledHardwareWriter _brightnessWriter;
        private readonly ThrottledHardwareWriter _panelBrightnessWriter;
        private bool _applyingExternalBrightness;
        private bool _isReady;

        [ObservableProperty]
        private int _currentRefreshRate = 60;

        private bool IsAtMaximumRate => !IsAutoRefresh && CurrentRefreshRate > MinRefreshRate;

        public bool IsLowRefreshRate => !IsAutoRefresh && CurrentRefreshRate > 0 && CurrentRefreshRate <= MinRefreshRate;

        /// <summary>
        /// The plain maximum. When overdrive cannot be chosen there is only one
        /// maximum option, so it stays lit whatever the overdrive register reads -
        /// splitting on overdrive in that case left every button unlit.
        /// </summary>
        public bool IsMaximumRefreshRate => IsAtMaximumRate && (!IsOverdriveAvailable || !IsOverdrive);

        public bool IsMaximumRefreshRateWithOverdrive => IsOverdriveAvailable && IsAtMaximumRate && IsOverdrive;

        /// <summary>
        /// A single-rate panel gets no refresh selector at all, matching the original
        /// <c>maxFrequency > MIN_RATE</c> visibility test.
        /// </summary>
        public bool IsRefreshRateSupported => MaxRefreshRate > MinRefreshRate;

        public string MinRefreshRateLabel => $"{MinRefreshRate} Hz";

        public string MaxRefreshRateLabel => $"{MaxRefreshRate} Hz";

        /// <summary>Shown as its own choice wherever overdrive is available.</summary>
        public string MaxRefreshRateWithOverdriveLabel => $"{MaxRefreshRate} Hz + OD";

        [ObservableProperty]
        private int _minRefreshRate = ScreenControl.MIN_RATE;

        [ObservableProperty]
        private int _maxRefreshRate = 60;

        [ObservableProperty]
        private bool _isOverdrive = false;

        [ObservableProperty]
        private bool _isOverdriveAvailable = false;

        [ObservableProperty]
        private bool _isAutoRefresh = false;

        [ObservableProperty]
        private bool _isMiniLed = false;

        [ObservableProperty]
        private bool _isHdr = false;

        /// <summary>
        /// GameVisual software dimming, not the backlight. Writing it re-runs the
        /// Splendid engine, which is why it nudges colour - hence its own labelled row.
        /// </summary>
        [ObservableProperty]
        private int _brightness = 100;

        /// <summary>The actual display backlight, via WMI.</summary>
        [ObservableProperty]
        private int _panelBrightness = 50;

        /// <summary>OLED dimming only exists on OLED panels, as in the original.</summary>
        public bool IsOledPanel { get; } = AppConfig.IsOLED();

        public bool IsColorPipelineEnabled => SelectedSplendidProfile != (int)SplendidCommand.Disabled;

        public bool IsOledDimmingAvailable => IsOledPanel && IsColorPipelineEnabled;

        [ObservableProperty]
        private int _colorTemperature = 50;

        [ObservableProperty]
        private int _selectedSplendidProfile = 0;

        [ObservableProperty]
        private int _selectedGamut = 50;

        [ObservableProperty]
        private int _currentGpuMode = 0;

        /// <summary>
        /// What the selected GPU mode means, under the row's own label. Deliberately the
        /// same four sentences <see cref="HomeViewModel.GpuModeDescription"/> uses: it is
        /// one control in two places, and a second wording would be a second thing to
        /// translate and keep true.
        /// </summary>
        public string GpuModeDescription => CurrentGpuMode switch
        {
            0 => AppStrings.Get("FeatureEcoTheDedicatedGPUIs"),
            1 => AppStrings.Get("FeatureStandardHybridApplicationsChoose"),
            2 => AppStrings.Get("FeatureUltimateTheDedicatedGPUDrives"),
            _ => AppStrings.Get("FeatureOptimizedFollowsThePowerSource")
        };

        partial void OnCurrentGpuModeChanged(int value) => OnPropertyChanged(nameof(GpuModeDescription));

        [ObservableProperty]
        private bool _isInstallingProfiles;

        [ObservableProperty]
        private bool _hasColorProfiles;

        [ObservableProperty]
        private bool _canInstallColorProfiles;

        [ObservableProperty]
        private string _colorProfileStatus = AppStrings.Get("FeatureCheckingInstalledProfiles");

        public bool IsMiniLedSupported { get; }
        public bool IsOverdriveSupported { get; }
        public bool IsMuxSupported { get; }
        public bool IsEcoSupported { get; }

        /// <summary>
        /// An XG Mobile dock is a removable box, so this is checked live rather than
        /// cached like the built-in capabilities. The original only ever shows the
        /// toggle while a dock is actually attached.
        /// </summary>
        [ObservableProperty]
        private bool _isXgmConnected;

        /// <summary>FHD/UHD switching exists on dual-mode panels only (<c>fhd >= 0</c>).</summary>
        [ObservableProperty]
        private bool _isResolutionToggleSupported;

        /// <summary>Handing HDR back to Windows needs HDR to be on and the control to answer.</summary>
        [ObservableProperty]
        private bool _isHdrControlSupported;

        /// <summary>The row goes away entirely when neither of its two buttons applies.</summary>
        public bool IsPanelModeRowVisible => IsResolutionToggleSupported || IsHdrControlSupported;

        partial void OnIsResolutionToggleSupportedChanged(bool value) => OnPropertyChanged(nameof(IsPanelModeRowVisible));
        partial void OnIsHdrControlSupportedChanged(bool value) => OnPropertyChanged(nameof(IsPanelModeRowVisible));

        public DisplayViewModel(IDisplayService displayService, IGpuService gpuService)
        {
            _displayService = displayService;
            _gpuService = gpuService;
            _brightnessWriter = new ThrottledHardwareWriter(_displayService.SetBrightness);
            _panelBrightnessWriter = new ThrottledHardwareWriter(_displayService.SetPanelBrightness);
            PanelBrightness = _displayService.PanelBrightness;
            CurrentRefreshRate = _displayService.CurrentRefreshRate;
            MinRefreshRate = ScreenControl.MIN_RATE;
            MaxRefreshRate = _displayService.MaxRefreshRate;
            IsOverdrive = _displayService.IsOverdriveEnabled;
            IsOverdriveAvailable = _displayService.IsOverdriveAvailable;
            IsResolutionToggleSupported = _displayService.IsResolutionToggleSupported;
            IsHdrControlSupported = _displayService.IsHdrControlSupported;
            IsAutoRefresh = _displayService.IsAutoRefreshEnabled;
            IsHdr = _displayService.IsHdrEnabled;
            Brightness = _displayService.Brightness;
            SelectedSplendidProfile = _displayService.CurrentVisualProfile;
            ColorTemperature = _displayService.ColorTemperature;
            SelectedGamut = _displayService.CurrentGamut;
            CurrentGpuMode = _gpuService.CurrentGpuMode;
            IsMiniLedSupported = _displayService.IsMiniLedSupported;
            IsOverdriveSupported = _displayService.IsOverdriveSupported;
            IsMuxSupported = _gpuService.IsMuxSupported;
            IsEcoSupported = _gpuService.IsEcoSupported;
            IsXgmConnected = _gpuService.IsXgmConnected;
            RefreshColorProfileState();
            _gpuService.GpuModeChanged += mode => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                CurrentGpuMode = mode;
                // A dock can be plugged in or pulled out at any point, and a GPU mode
                // change is when the original re-runs its XG Mobile visibility check.
                IsXgmConnected = _gpuService.IsXgmConnected;
            });
            _displayService.PanelBrightnessChanged += level => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                _applyingExternalBrightness = true;
                try { PanelBrightness = level; }
                finally { _applyingExternalBrightness = false; }
            });
            _displayService.ColorPipelineStateChanged += _ => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                SelectedSplendidProfile = _displayService.CurrentVisualProfile;
            });
            _displayService.DisplayStatusChanged += snapshot => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                CurrentRefreshRate = snapshot.Frequency;
                MaxRefreshRate = snapshot.MaxFrequency;
                IsOverdrive = snapshot.Overdrive > 0;
                IsOverdriveAvailable = snapshot.OverdriveSetting;
                IsAutoRefresh = snapshot.ScreenAuto;
                IsMiniLed = IsMiniLedSupported && (snapshot.Miniled1 > 0 || snapshot.Miniled2 > 0);
                IsHdr = snapshot.Hdr;
                IsResolutionToggleSupported = snapshot.Fhd >= 0;
                IsHdrControlSupported = snapshot.Hdr && snapshot.HdrControl >= 0;
            });
            _isReady = true;
        }

        [RelayCommand]
        public void SetRefreshRate(object? hzParam)
        {
            // MAX_REFRESH is a sentinel resolved against the live panel at write time,
            // so a stale cached maximum can never send the display to a mode it does
            // not have.
            string mode = hzParam?.ToString() ?? string.Empty;
            bool withOverdrive = mode.Equals("max_od", StringComparison.OrdinalIgnoreCase);
            bool maximum = withOverdrive
                || mode.StartsWith("max", StringComparison.OrdinalIgnoreCase)
                || ToInt(hzParam, MinRefreshRate) > MinRefreshRate;

            int hz = maximum ? ScreenControl.MAX_REFRESH : ScreenControl.MIN_RATE;

            // Overdrive is only ever on for the explicit "+ OD" choice. Where the panel
            // offers no overdrive there is a single maximum option and the write is a
            // harmless no-op.
            bool overdrive = withOverdrive || (maximum && !IsOverdriveAvailable);

            // Optimistic state only. InitScreen fires a snapshot right after the
            // write and that is what settles both values.
            IsAutoRefresh = false;
            CurrentRefreshRate = maximum ? MaxRefreshRate : MinRefreshRate;
            IsOverdrive = withOverdrive;
            _displayService.SetRefreshRate(hz, overdrive);
        }

        [RelayCommand]
        public void ToggleAutoRefresh()
        {
            _displayService.SetAutoRefresh(IsAutoRefresh);
        }

        [RelayCommand]
        public void ToggleMiniLed()
        {
            if (!IsMiniLedSupported) return;
            _displayService.ToggleMiniLed();
            IsMiniLed = !IsMiniLed;
        }

        /// <summary>
        /// Every change to <see cref="Brightness"/> reaches the panel, including the
        /// ones produced while the slider is still being dragged. The writer bounds how
        /// often that actually hits the hardware.
        /// </summary>
        partial void OnBrightnessChanged(int value)
        {
            if (_isReady && IsOledDimmingAvailable) _brightnessWriter.Push(value);
        }

        partial void OnSelectedSplendidProfileChanged(int value)
        {
            OnPropertyChanged(nameof(IsColorPipelineEnabled));
            OnPropertyChanged(nameof(IsOledDimmingAvailable));
        }

        partial void OnPanelBrightnessChanged(int value)
        {
            // A value that arrived from the hardware watcher is already applied.
            // Writing it back would fight the brightness keys mid-press.
            if (_isReady && !_applyingExternalBrightness) _panelBrightnessWriter.Push(Math.Clamp(value, 0, 100));
        }

        [RelayCommand]
        public void SetBrightnessLevel(object? valueParam)
        {
            if (!IsOledDimmingAvailable) return;
            int value = ToInt(valueParam, 100);
            Brightness = value;

            // Pushed explicitly as well: if the value is unchanged the property setter
            // raises nothing, and a caller asking for a specific level still expects
            // it to be written.
            _brightnessWriter.Push(value);
        }

        [RelayCommand]
        public void SetSplendid(object? profileParam)
        {
            int profileId = ToInt(profileParam, 0);
            SelectedSplendidProfile = profileId;
            _displayService.SetVisualProfile(profileId);
        }

        [RelayCommand]
        public void SetColorTemperatureValue(object? valueParam)
        {
            if (!IsColorPipelineEnabled) return;
            ColorTemperature = Math.Clamp(ToInt(valueParam, ColorTemperature), 0, 100);
            _displayService.SetColorTemperature(ColorTemperature);
        }

        [RelayCommand]
        public void SetGamut(object? gamutParam)
        {
            if (!IsColorPipelineEnabled) return;
            SelectedGamut = ToInt(gamutParam, SelectedGamut);
            _displayService.SetGamut(SelectedGamut);
        }

        [RelayCommand]
        public void SetGpuMode(object? modeParam)
        {
            int mode = ToInt(modeParam, 0);
            CurrentGpuMode = mode;
            _gpuService.SetGpuMode(mode);
        }

        [RelayCommand]
        public void StopGpuApps() => _gpuService.KillGpuApps();

        [RelayCommand]
        public void ToggleXgm() => _gpuService.ToggleXgm();

        [RelayCommand]
        public void ToggleResolution() => _displayService.ToggleResolution();

        [RelayCommand]
        public void ToggleHdrControl() => _displayService.ToggleHdrControl();

        partial void OnIsAutoRefreshChanged(bool value)
        {
            // Automatic mode owns the rate, so neither fixed choice reads as active
            // while it is on - the original gives buttonScreenAuto the same priority.
            NotifyRefreshRateSelection();
            if (_isReady) _displayService.SetAutoRefresh(value);
        }

        partial void OnCurrentRefreshRateChanged(int value) => NotifyRefreshRateSelection();

        partial void OnMinRefreshRateChanged(int value)
        {
            OnPropertyChanged(nameof(MinRefreshRateLabel));
            OnPropertyChanged(nameof(IsRefreshRateSupported));
            NotifyRefreshRateSelection();
        }

        partial void OnMaxRefreshRateChanged(int value)
        {
            OnPropertyChanged(nameof(MaxRefreshRateLabel));
            OnPropertyChanged(nameof(MaxRefreshRateWithOverdriveLabel));
            OnPropertyChanged(nameof(IsRefreshRateSupported));
        }

        partial void OnIsOverdriveAvailableChanged(bool value) => NotifyRefreshRateSelection();
        partial void OnIsOverdriveChanged(bool value) => NotifyRefreshRateSelection();

        private void NotifyRefreshRateSelection()
        {
            OnPropertyChanged(nameof(IsLowRefreshRate));
            OnPropertyChanged(nameof(IsMaximumRefreshRate));
            OnPropertyChanged(nameof(IsMaximumRefreshRateWithOverdrive));
        }

        partial void OnIsMiniLedChanged(bool value)
        {
            if (!_isReady) return;
            bool actual = _displayService.MiniLedMode > 0;
            if (actual != value) _displayService.ToggleMiniLed();
        }

        [RelayCommand]
        public async Task InstallColorProfiles()
        {
            IsInstallingProfiles = true;
            ColorProfileStatus = AppStrings.Get("FeatureInstallingASUSColorProfiles");
            try
            {
                bool installed = await _displayService.InstallColorProfilesAsync();
                RefreshColorProfileState();
                if (!installed) ColorProfileStatus = AppStrings.Get("FeatureInstallationWasCancelledOrNo");
            }
            finally { IsInstallingProfiles = false; }
        }

        private void RefreshColorProfileState()
        {
            HasColorProfiles = _displayService.HasColorProfiles;
            CanInstallColorProfiles = _displayService.CanInstallColorProfiles;
            ColorProfileStatus = HasColorProfiles
                ? AppStrings.Get("FeatureASUSColorProfilesAreInstalled")
                : CanInstallColorProfiles
                    ? AppStrings.Get("FeatureAMatchingASUSProfilePackage")
                    : AppStrings.Get("FeatureNoMatchingASUSProfilePackage");
        }

        private static int ToInt(object? param, int defaultValue = 0)
        {
            if (param == null) return defaultValue;
            if (param is int i) return i;
            if (int.TryParse(param.ToString(), out int parsed)) return parsed;
            return defaultValue;
        }
    }

    public partial class BatteryViewModel : ObservableObject
    {
        private readonly IBatteryService _batteryService;
        private readonly IDeviceStateService _deviceStateService;

        [ObservableProperty]
        private int _chargeLimit = 80;

        [ObservableProperty]
        private bool _isFullChargeOverride = false;

        [ObservableProperty]
        private int _batteryPercent = 80;

        [ObservableProperty]
        private float _dischargeRate = 0;

        [ObservableProperty]
        private string _batteryHealth = AppStrings.Get("FeatureReading");

        /// <summary>Capacity figures behind the health percentage.</summary>
        [ObservableProperty]
        private string _batteryHealthDetail = string.Empty;

        // ===== Power flow ======================================================
        // The rate is signed: positive is power going into the battery, negative is
        // power coming out. One tile reports whichever is happening, because a figure
        // labelled "discharge" while the machine is plugged in and filling up is just
        // wrong.

        public bool IsCharging => DischargeRate > 0;

        public string PowerFlowLabel => DischargeRate switch
        {
            > 0 => AppStrings.Get("Charging"),
            < 0 => AppStrings.Get("Discharging"),
            _ => AppStrings.Get("FeatureBatteryPower")
        };

        public string PowerFlowValue => $"{Math.Abs(DischargeRate):F1} W";

        public SymbolRegular PowerFlowIcon => IsCharging ? SymbolRegular.BatteryCharge24 : SymbolRegular.Flash24;

        /// <summary>
        /// The last draw seen while running on battery, in watts.
        /// </summary>
        /// <remarks>
        /// Kept so the runtime estimate still has something to divide by once the charger
        /// goes in. Both halves of the analysis are worth reading at the same time, and
        /// the discharge rate is unobservable at exactly the moment you are most likely
        /// to be asking how long a charge will last.
        /// </remarks>
        private float _lastDrawWatts;

        partial void OnDischargeRateChanged(float value)
        {
            if (value < 0) _lastDrawWatts = -value;
            RaisePowerFlowDependents();
        }

        partial void OnBatteryPercentChanged(int value) => RaisePowerFlowDependents();

        private void RaisePowerFlowDependents()
        {
            OnPropertyChanged(nameof(IsCharging));
            OnPropertyChanged(nameof(PowerFlowLabel));
            OnPropertyChanged(nameof(PowerFlowValue));
            OnPropertyChanged(nameof(PowerFlowIcon));
            OnPropertyChanged(nameof(TimeToFullText));
            OnPropertyChanged(nameof(TimeToEmptyText));
        }

        /// <summary>Watt-hours in the battery now, or null if it will not say.</summary>
        private static decimal? StoredWh =>
            HardwareControl.chargeCapacity > 0 ? HardwareControl.chargeCapacity / 1000 : null;

        /// <summary>Watt-hours the battery holds when full, allowing for wear.</summary>
        private static decimal? CapacityWh =>
            HardwareControl.fullCapacity > 0 ? HardwareControl.fullCapacity / 1000 : null;

        /// <summary>
        /// How long until charging stops. Measured against the level the limit will
        /// actually stop at, not 100%, so a machine capped at 80% is not reported as
        /// still having an hour to go once it has finished.
        /// </summary>
        public string TimeToFullText
        {
            get
            {
                if (StoredWh is not { } stored || CapacityWh is not { } capacity) return AppStrings.Get("FeatureChargeTimeUnavailable");

                decimal target = capacity * EffectiveChargeLimit / 100;
                decimal missing = target - stored;
                if (missing <= 0) return AppStrings.Get("FeatureChargedToTheLimit");
                if (DischargeRate <= 0) return $"{FormatWh(missing)} below the limit";

                return AppStrings.Get("FeatureFullIn") + FormatHours((double)(missing / (decimal)DischargeRate));
            }
        }

        /// <summary>How long the charge lasts at the draw being measured, or the last one.</summary>
        public string TimeToEmptyText
        {
            get
            {
                if (StoredWh is not { } stored) return string.Empty;

                float draw = DischargeRate < 0 ? -DischargeRate : _lastDrawWatts;
                if (draw <= 0) return AppStrings.Get("FeatureRuntimeMeasuredOnceOnBattery");

                string estimate = FormatHours((double)(stored / (decimal)draw));
                return DischargeRate < 0
                    ? estimate + " of use left"
                    : estimate + $" of use at {draw:F1} W";
            }
        }

        private static string FormatWh(decimal wattHours) => $"{wattHours:F1} Wh";

        private static string FormatHours(double hours)
        {
            if (double.IsNaN(hours) || double.IsInfinity(hours) || hours <= 0) return "moments";
            if (hours >= 48) return "over 2 days";

            int whole = (int)hours;
            int minutes = (int)Math.Round((hours - whole) * 60);
            if (minutes == 60) { whole++; minutes = 0; }

            if (whole == 0) return $"{minutes} min";
            return minutes == 0 ? $"{whole} h" : $"{whole} h {minutes} min";
        }

        /// <summary>
        /// Some models only accept 60-80 and 100 rather than a free range; the backend
        /// rounds anything else to one of those. Surfacing the same rule here means the
        /// slider offers what the hardware will honour instead of a value it will
        /// silently change.
        /// </summary>
        public bool HasSteppedChargeLimit { get; } = AppConfig.IsChargeLimit6080();

        public int ChargeLimitMinimum => HasSteppedChargeLimit ? 60 : 40;

        /// <summary>
        /// Positions the thumb may take. Snapping to the nearest listed value keeps the
        /// gap between 80 and 100 crossable in both directions; coercing after the fact
        /// would strand the thumb at 100.
        /// </summary>
        public System.Windows.Media.DoubleCollection ChargeLimitTicks { get; }

        /// <summary>Values labelled on the scale under the track.</summary>
        public System.Windows.Media.DoubleCollection ChargeLimitMarks { get; }

        /// <summary>
        /// The level charging will actually stop at right now, accounting for a
        /// one-time full charge overriding the saved limit.
        /// </summary>
        public int EffectiveChargeLimit => IsFullChargeOverride ? 100 : ChargeLimit;

        public string ChargeStopsAtText => EffectiveChargeLimit >= 100
            ? AppStrings.Get("FeatureChargingToFull")
            : $"Charging stops at {EffectiveChargeLimit}%";

        partial void OnChargeLimitChanged(int value) => RaiseChargeLimitDependents();
        partial void OnIsFullChargeOverrideChanged(bool value) => RaiseChargeLimitDependents();

        private void RaiseChargeLimitDependents()
        {
            OnPropertyChanged(nameof(EffectiveChargeLimit));
            OnPropertyChanged(nameof(ChargeStopsAtText));
            RaisePowerFlowDependents();
        }

        public string ChargeLimitNote => HasSteppedChargeLimit
            ? AppStrings.Get("FeatureThisModelChargesToAny")
            : AppStrings.Get("FeatureAnyLevelBetween40And");

        public BatteryViewModel(IBatteryService batteryService, IDeviceStateService deviceStateService)
        {
            if (HasSteppedChargeLimit)
            {
                var ticks = new System.Windows.Media.DoubleCollection();
                for (int value = 60; value <= 80; value++) ticks.Add(value);
                ticks.Add(100);
                ticks.Freeze();
                ChargeLimitTicks = ticks;
                var marks = new System.Windows.Media.DoubleCollection { 60, 80, 100 };
                marks.Freeze();
                ChargeLimitMarks = marks;
            }
            else
            {
                var ticks = new System.Windows.Media.DoubleCollection();
                ticks.Freeze();
                ChargeLimitTicks = ticks;
                var marks = new System.Windows.Media.DoubleCollection { 40, 100 };
                marks.Freeze();
                ChargeLimitMarks = marks;
            }

            _batteryService = batteryService;
            _deviceStateService = deviceStateService;

            ChargeLimit = _batteryService.ChargeLimit;
            IsFullChargeOverride = _batteryService.IsFullChargeOverride;
            BatteryPercent = _batteryService.BatteryPercent;
            DischargeRate = _batteryService.DischargeRateWatts;

            _deviceStateService.TelemetryUpdated += (t) =>
            {
                // Nothing ever asked the hardware layer for a health figure, so
                // batteryHealth sat at its -1 sentinel and the tile read "Reading…"
                // forever. The original refreshes it on demand, throttled.
                RequestBatteryHealthRefresh();

                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    BatteryPercent = t.BatteryPercentage;
                    DischargeRate = t.BatteryDischargeRate;
                    UpdateBatteryHealthText();
                });
            };
            _batteryService.ChargeLimitChanged += limit => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => ChargeLimit = limit);
            _batteryService.FullChargeOverrideChanged += enabled => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => IsFullChargeOverride = enabled);
        }

        /// <summary>
        /// Reporting the limit to Windows writes under HKLM, which needs elevation. The
        /// firmware limit itself applies either way, so this is only about the battery
        /// icon's smart-charging indicator.
        /// </summary>
        public bool NeedsElevation { get; } = !Arsenal.Helpers.ProcessHelper.IsUserAdministrator();

        [RelayCommand]
        public void RestartAsAdministrator() => Arsenal.Helpers.ProcessHelper.RunAsAdmin();

        private long _lastHealthRefresh;
        private int _healthRefreshRunning;

        /// <summary>
        /// Design and full-charge capacity come from WMI, which is slow, and wear does
        /// not move minute to minute - so this re-reads at most every 15 minutes, the
        /// same interval the original uses, and always off the UI thread.
        /// </summary>
        private void RequestBatteryHealthRefresh()
        {
            long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            if (_lastHealthRefresh != 0 && Math.Abs(now - _lastHealthRefresh) <= 15 * 60_000) return;
            if (Interlocked.Exchange(ref _healthRefreshRunning, 1) == 1) return;

            _lastHealthRefresh = now;
            Task.Run(() =>
            {
                try { HardwareControl.RefreshBatteryHealth(); }
                catch (Exception ex) { Logger.WriteLine("Battery health: " + ex.Message); }
                finally { Interlocked.Exchange(ref _healthRefreshRunning, 0); }

                System.Windows.Application.Current?.Dispatcher.BeginInvoke(UpdateBatteryHealthText);
            });
        }

        private void UpdateBatteryHealthText()
        {
            if (HardwareControl.batteryHealth > 0)
            {
                BatteryHealth = $"{HardwareControl.batteryHealth:F0}%";
                BatteryHealthDetail = HardwareControl.fullCapacity > 0 && HardwareControl.designCapacity > 0
                    ? $"{HardwareControl.fullCapacity / 1000:F1} of {HardwareControl.designCapacity / 1000:F1} Wh"
                    : AppStrings.Get("FeatureFullChargeVsOriginalCapacity");
            }
            else
            {
                BatteryHealth = AppStrings.Get("FeatureUnavailable");
                BatteryHealthDetail = AppStrings.Get("FeatureThisBatteryDoesNotReport");
            }
        }

        [RelayCommand]
        public void SetLimit(object? limitParam)
        {
            int limit = ToInt(limitParam, 80);
            ChargeLimit = limit;
            _batteryService.SetChargeLimit(limit);
        }

        [RelayCommand]
        public void ToggleFullCharge()
        {
            _batteryService.ToggleFullChargeOverride();
            IsFullChargeOverride = _batteryService.IsFullChargeOverride;
        }

        [RelayCommand]
        public void GenerateReport()
        {
            _batteryService.GenerateBatteryReport();
        }

        private static int ToInt(object? param, int defaultValue = 80)
        {
            if (param == null) return defaultValue;
            if (param is int i) return i;
            if (int.TryParse(param.ToString(), out int parsed)) return parsed;
            return defaultValue;
        }
    }

    public partial class SelectableIntOption : ObservableObject
    {
        public int Value { get; }
        public string Name { get; }
        [ObservableProperty] private bool _isSelected;

        public SelectableIntOption(int value, string name, bool isSelected = false)
        {
            Value = value;
            Name = name;
            IsSelected = isSelected;
        }
    }

    public partial class LightingViewModel : ObservableObject
    {
        private readonly ILightingService _lightingService;
        private bool _isReady;

        [ObservableProperty]
        private int _brightness = 2;

        [ObservableProperty]
        private int _selectedMode = 0; // Static, Breathing, Color Cycle, Rainbow, etc.

        [ObservableProperty]
        private byte _red = 255;

        [ObservableProperty]
        private byte _green = 0;

        [ObservableProperty]
        private byte _blue = 128;

        [ObservableProperty]
        private MediaColor _selectedColor = MediaColor.FromRgb(255, 0, 128);

        public string HexColor => $"#{SelectedColor.R:X2}{SelectedColor.G:X2}{SelectedColor.B:X2}";
        public string RgbColor => $"RGB {SelectedColor.R}, {SelectedColor.G}, {SelectedColor.B}";
        public System.Windows.Media.SolidColorBrush ColorPreviewBrush => new(SelectedColor);

        [ObservableProperty]
        private int _speed = 1; // 0=Slow, 1=Normal, 2=Fast

        [ObservableProperty]
        private bool _awake = true;

        [ObservableProperty]
        private bool _boot = true;

        [ObservableProperty]
        private bool _sleep = false;

        [ObservableProperty]
        private bool _shutdown = false;

        [ObservableProperty] private bool _hasAnimeMatrix;
        [ObservableProperty] private bool _hasSlash;
        [ObservableProperty] private bool _hasMatrixOrSlash;
        [ObservableProperty] private bool _hasKeyboardColor = true;
        [ObservableProperty] private bool _hasAuraEffects = true;
        [ObservableProperty] private int _matrixBrightness;
        [ObservableProperty] private int _matrixMode;
        [ObservableProperty] private bool _matrixDisableOnBattery;
        [ObservableProperty] private bool _matrixDisableWithLid;
        [ObservableProperty] private bool _matrixFlip;
        [ObservableProperty] private string _matrixText = "Hello!";
        [ObservableProperty] private string _matrixText2 = string.Empty;
        [ObservableProperty] private int _matrixTextFont;
        [ObservableProperty] private int _matrixTextFont2;
        [ObservableProperty] private int _matrixTextSize = 15;
        [ObservableProperty] private int _matrixTextSize2 = 15;
        [ObservableProperty] private bool _matrixTextRunning;
        [ObservableProperty] private string _matrixTimeFormat = "HH:mm";
        [ObservableProperty] private string _matrixDateFormat = "yy.MM.dd";
        [ObservableProperty] private bool _matrixClockBattery;
        [ObservableProperty] private int _matrixAudioMode;
        [ObservableProperty] private string _lightingDeviceTitle = AppStrings.Get("AnimeMatrix");
        [ObservableProperty] private int _slashInterval;
        [ObservableProperty] private bool _slashBootAnimation;
        [ObservableProperty] private bool _slashSleepAnimation;
        [ObservableProperty] private int _slashSleepPattern;
        [ObservableProperty] private bool _slashLowBatteryAlert;
        [ObservableProperty] private bool _slashBatteryIndicator;
        [ObservableProperty] private bool _slashPowerSaving;
        [ObservableProperty] private int _slashDimLevel = 20;


        // ------------------------------------------------------------------
        // Keyboard preview
        // ------------------------------------------------------------------

        /// <summary>The <c>AuraBacklightType</c> the preview draws for.</summary>
        [ObservableProperty] private int _backlightZoneType;

        /// <summary>The second Aura colour, which Breathe fades to.</summary>
        [ObservableProperty] private MediaColor _secondaryColor = MediaColor.FromRgb(0, 0, 0);

        [ObservableProperty] private bool _previewNumpad;
        [ObservableProperty] private bool _previewIso;

        /// <summary>The chassis the layout was matched to, empty when nothing matched.</summary>
        [ObservableProperty] private string _previewChassis = string.Empty;

        /// <summary>
        /// The whole shape handed to the preview: what the chassis table says, with the
        /// two things the user can correct applied over the top.
        /// </summary>
        [ObservableProperty] private Arsenal.Peripherals.Keyboard.LaptopKeyboardOptions _previewLayout;

        private Arsenal.Peripherals.Keyboard.LaptopKeyboardOptions _chassisLayout;

        /// <summary>
        /// Shown under the preview so it is clear what is being drawn and why, since
        /// the picture cannot say on its own whether it is mirroring the keyboard or
        /// reproducing it.
        /// </summary>
        public string PreviewCaption => BacklightZoneType switch
        {
            (int)Arsenal.USB.AuraBacklightType.PerKey =>
                AppStrings.Get("LightingPreviewPerKey"),
            (int)Arsenal.USB.AuraBacklightType.MultiZone =>
                AppStrings.Get("LightingPreviewFourZone"),
            _ => AppStrings.Get("LightingPreviewSingleZone"),
        };

        /// <summary>
        /// Says where the drawn shape came from - the chassis it was matched to, or
        /// that nothing matched and this is a generic shape. Without it the preview
        /// silently implies a precision the table may not have for this model.
        /// </summary>
        public string PreviewShapeSource => PreviewChassis.Length > 0
            ? AppStrings.Format("LightingPreviewMatched", PreviewChassis)
            : AppStrings.Get("LightingPreviewUnmatched");

        partial void OnBacklightZoneTypeChanged(int value) => OnPropertyChanged(nameof(PreviewCaption));

        partial void OnPreviewChassisChanged(string value) => OnPropertyChanged(nameof(PreviewShapeSource));

        private void LoadPreviewLayout()
        {
            BacklightZoneType = _lightingService.BacklightZoneType;

            // The chassis table decides the arrangement, the hardware decides the
            // region and the light bar, and a saved answer is the user's own
            // correction of the two the table can be wrong about.
            var match = Arsenal.Peripherals.Keyboard.LaptopKeyboardLayout.Detect();
            _chassisLayout = match.Options with { Lightbar = _lightingService.HasLightbar };
            PreviewChassis = match.ChassisName;
            PreviewNumpad = AppConfig.Get("keyboard_numpad", _chassisLayout.Numpad ? 1 : 0) == 1;
            PreviewIso = AppConfig.Get("keyboard_iso", _chassisLayout.Iso ? 1 : 0) == 1;
            ApplyPreviewLayout();
            SecondaryColor = FromArgbInt(AppConfig.Get("aura_color2", 0));
        }

        private void ApplyPreviewLayout()
            => PreviewLayout = _chassisLayout with { Numpad = PreviewNumpad, Iso = PreviewIso };

        private static MediaColor FromArgbInt(int value) => MediaColor.FromRgb(
            (byte)((value >> 16) & 0xFF), (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF));

        partial void OnPreviewNumpadChanged(bool value)
        {
            ApplyPreviewLayout();
            if (_isReady) AppConfig.Set("keyboard_numpad", value ? 1 : 0);
        }

        partial void OnPreviewIsoChanged(bool value)
        {
            ApplyPreviewLayout();
            if (_isReady) AppConfig.Set("keyboard_iso", value ? 1 : 0);
        }
        public ObservableCollection<SelectableIntOption> AuraModes { get; } = new();
        public ObservableCollection<SelectableIntOption> MatrixModes { get; } = new();
        public IReadOnlyList<int> SlashDimLevels { get; } = new[] { 10, 20, 30, 40, 50, 100 };
        public IReadOnlyList<string> MatrixFonts { get; } = Arsenal.AnimeMatrix.AniMatrixControl.TextFonts
            .Select(name => string.IsNullOrEmpty(name) ? "System default" : name).ToArray();

        public LightingViewModel(ILightingService lightingService)
        {
            _lightingService = lightingService;
            Brightness = _lightingService.Brightness;
            SelectedMode = _lightingService.CurrentMode;
            int savedColor = AppConfig.Get("aura_color", 0xFF0080);
            SelectedColor = MediaColor.FromRgb(
                (byte)((savedColor >> 16) & 0xFF),
                (byte)((savedColor >> 8) & 0xFF),
                (byte)(savedColor & 0xFF));
            Speed = Math.Clamp(AppConfig.Get("aura_speed", 1), 0, 2);
            Awake = AppConfig.IsNotFalse("keyboard_awake");
            Boot = AppConfig.IsNotFalse("keyboard_boot");
            Sleep = AppConfig.IsNotFalse("keyboard_sleep");
            Shutdown = AppConfig.IsNotFalse("keyboard_shutdown");
            HasAnimeMatrix = _lightingService.HasAnimeMatrix;
            HasSlash = _lightingService.HasSlash;
            HasMatrixOrSlash = HasAnimeMatrix || HasSlash;

            // A white-only backlight takes a colour and throws it away. Showing the
            // picker there is the same lie as showing a MUX switch on a machine
            // without one, and the legacy client hid it for exactly this reason.
            HasKeyboardColor = _lightingService.HasKeyboardColor;
            HasAuraEffects = _lightingService.HasAuraEffects;
            MatrixBrightness = _lightingService.MatrixBrightness;
            MatrixMode = _lightingService.MatrixMode;
            foreach (var pair in Arsenal.USB.Aura.GetModes())
                AuraModes.Add(new SelectableIntOption((int)pair.Key, pair.Value, (int)pair.Key == SelectedMode));

            if (HasSlash)
            {
                LightingDeviceTitle = AppStrings.Get("FeatureSlashLighting");
                foreach (var pair in Arsenal.AnimeMatrix.SlashDevice.Modes)
                    MatrixModes.Add(new SelectableIntOption((int)pair.Key, pair.Value, (int)pair.Key == MatrixMode));
                LoadSlashSettings();
            }
            else
            {
                string[] matrixNames = { AppStrings.Get("FeatureBanner"), AppStrings.Get("AuraZoneLogo"), AppStrings.Get("MatrixPicture"), AppStrings.Get("MatrixClock"), AppStrings.Get("MatrixAudio"), AppStrings.Get("MatrixText") };
                for (int i = 0; i < matrixNames.Length; i++)
                    MatrixModes.Add(new SelectableIntOption(i, matrixNames[i], i == MatrixMode));
            }
            MatrixDisableOnBattery = AppConfig.Is("matrix_auto");
            MatrixDisableWithLid = AppConfig.Is("matrix_lid");
            MatrixFlip = AppConfig.Is("matrix_flip");
            MatrixText = AppConfig.GetString("matrix_text", "Hello!");
            MatrixText2 = AppConfig.GetString("matrix_text2", string.Empty);
            MatrixTextFont = Math.Clamp(AppConfig.Get("matrix_text_font", 0), 0, MatrixFonts.Count - 1);
            MatrixTextFont2 = Math.Clamp(AppConfig.Get("matrix_text2_font", 0), 0, MatrixFonts.Count - 1);
            MatrixTextSize = Math.Clamp(AppConfig.Get("matrix_text_size", 15), 8, 36);
            MatrixTextSize2 = Math.Clamp(AppConfig.Get("matrix_text2_size", 15), 8, 36);
            MatrixTextRunning = AppConfig.Is("matrix_text_running");
            MatrixTimeFormat = AppConfig.GetString("matrix_time", "HH:mm");
            MatrixDateFormat = AppConfig.GetString("matrix_date", "yy.MM.dd");
            MatrixClockBattery = AppConfig.Is("matrix_clock_battery");
            MatrixAudioMode = Math.Clamp(AppConfig.Get("matrix_audio_mode", 0), 0, 1);
            _lightingService.BrightnessChanged += level => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Brightness = level);
            _lightingService.ModeChanged += mode => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                SelectedMode = mode;
                UpdateSelection(AuraModes, mode);
            });
            LoadPreviewLayout();
            _isReady = true;
        }

        [RelayCommand]
        public void SetBrightnessLevel(object? levelParam)
        {
            int level = ToInt(levelParam, 2);
            Brightness = level;
            _lightingService.SetBrightness(level);
        }

        [RelayCommand]
        public void SetAuraMode(object? modeParam)
        {
            int mode = ToInt(modeParam, 0);
            SelectedMode = mode;
            UpdateSelection(AuraModes, mode);
            _lightingService.SetMode(mode);
        }

        [RelayCommand]
        public void ApplyColor()
        {
            _lightingService.SetColor(SelectedColor.R, SelectedColor.G, SelectedColor.B);
            _lightingService.SetSpeed(Speed);
        }

        [RelayCommand]
        public void SetPresetColor(object? colorParam)
        {
            if (colorParam is string value && System.Windows.Media.ColorConverter.ConvertFromString(value) is MediaColor color)
                SelectedColor = color;
        }

        [RelayCommand]
        public void SetSpeed(object? speedParam) => Speed = Math.Clamp(ToInt(speedParam, 1), 0, 2);

        partial void OnSelectedColorChanged(MediaColor value)
        {
            Red = value.R;
            Green = value.G;
            Blue = value.B;
            OnPropertyChanged(nameof(HexColor));
            OnPropertyChanged(nameof(RgbColor));
            OnPropertyChanged(nameof(ColorPreviewBrush));
        }

        partial void OnAwakeChanged(bool value)
        {
            if (_isReady) _lightingService.SetAwake(value);
        }

        partial void OnBootChanged(bool value)
        {
            if (_isReady) _lightingService.SetBoot(value);
        }

        partial void OnSleepChanged(bool value)
        {
            if (_isReady) _lightingService.SetSleep(value);
        }

        partial void OnShutdownChanged(bool value)
        {
            if (_isReady) _lightingService.SetShutdown(value);
        }

        partial void OnMatrixDisableOnBatteryChanged(bool value)
        {
            if (_isReady) _lightingService.SetMatrixPowerPolicy(value, MatrixDisableWithLid);
        }

        partial void OnMatrixDisableWithLidChanged(bool value)
        {
            if (_isReady) _lightingService.SetMatrixPowerPolicy(MatrixDisableOnBattery, value);
        }

        partial void OnMatrixFlipChanged(bool value)
        {
            if (!_isReady) return;
            AppConfig.Set("matrix_flip", value ? 1 : 0);
            Program.matrixControl?.deviceMatrix?.PresentClock();
        }

        [RelayCommand]
        public void UpdatePowerStates()
        {
            _lightingService.SetAwake(Awake);
            _lightingService.SetBoot(Boot);
            _lightingService.SetSleep(Sleep);
            _lightingService.SetShutdown(Shutdown);
        }

        [RelayCommand]
        public void SetMatrixBrightnessLevel(object? valueParam)
        {
            MatrixBrightness = Math.Clamp(ToInt(valueParam, MatrixBrightness), 0, 3);
            _lightingService.SetMatrixBrightness(MatrixBrightness);
        }

        [RelayCommand]
        public void SetMatrixRunningMode(object? valueParam)
        {
            MatrixMode = ToInt(valueParam, MatrixMode);
            UpdateSelection(MatrixModes, MatrixMode);
            _lightingService.SetMatrixMode(MatrixMode);
        }

        [RelayCommand]
        public void ChooseMatrixPicture()
        {
            Program.matrixControl?.OpenMatrixPicture();
            MatrixMode = (int)Arsenal.AnimeMatrix.MatrixMode.Picture;
            UpdateSelection(MatrixModes, MatrixMode);
        }

        [RelayCommand]
        public void ApplyMatrixText()
        {
            AppConfig.Set("matrix_text", MatrixText);
            AppConfig.Set("matrix_text2", MatrixText2);
            AppConfig.Set("matrix_text_font", MatrixTextFont);
            AppConfig.Set("matrix_text2_font", MatrixTextFont2);
            AppConfig.Set("matrix_text_size", Math.Clamp(MatrixTextSize, 8, 36));
            AppConfig.Set("matrix_text2_size", Math.Clamp(MatrixTextSize2, 8, 36));
            AppConfig.Set("matrix_text_running", MatrixTextRunning ? 1 : 0);
            ActivateMatrixMode(Arsenal.AnimeMatrix.MatrixMode.Text, () => Program.matrixControl?.SetMatrixText());
        }

        [RelayCommand]
        public void ApplyMatrixClock()
        {
            try { _ = DateTime.Now.ToString(MatrixTimeFormat); if (MatrixTimeFormat.Length > 0) AppConfig.Set("matrix_time", MatrixTimeFormat); } catch { }
            try { _ = DateTime.Now.ToString(MatrixDateFormat); if (MatrixDateFormat.Length > 0) AppConfig.Set("matrix_date", MatrixDateFormat); } catch { }
            AppConfig.Set("matrix_clock_battery", MatrixClockBattery ? 1 : 0);
            ActivateMatrixMode(Arsenal.AnimeMatrix.MatrixMode.Clock, () =>
            {
                Program.matrixControl?.SetMatrixClock();
                Program.matrixControl?.deviceMatrix?.PresentClock();
            });
        }

        [RelayCommand]
        public void ApplyMatrixAudio()
        {
            AppConfig.Set("matrix_audio_mode", Math.Clamp(MatrixAudioMode, 0, 1));
            ActivateMatrixMode(Arsenal.AnimeMatrix.MatrixMode.Audio, () => Program.matrixControl?.SetDevice());
        }

        private void ActivateMatrixMode(Arsenal.AnimeMatrix.MatrixMode mode, Action refresh)
        {
            int value = (int)mode;
            if (MatrixMode == value) refresh();
            else
            {
                MatrixMode = value;
                UpdateSelection(MatrixModes, MatrixMode);
                _lightingService.SetMatrixMode(MatrixMode);
            }
        }

        [RelayCommand]
        public void UpdateMatrixPowerPolicy() => _lightingService.SetMatrixPowerPolicy(MatrixDisableOnBattery, MatrixDisableWithLid);

        private void LoadSlashSettings()
        {
            SlashInterval = Math.Clamp(AppConfig.Get("matrix_interval", 0), 0, 5);
            SlashDimLevel = AppConfig.Get("slash_dim", 20);
            var slash = Program.matrixControl?.deviceSlash;
            if (slash == null) return;
            try
            {
                SlashBootAnimation = slash.GetFlag(0xA0);
                byte[]? sleep = slash.GetRecord(0xA1);
                SlashSleepAnimation = sleep is not null && sleep[8] == 0x01;
                SlashSleepPattern = sleep is not null && sleep[6] != 0x00 ? 1 : 0;
                SlashLowBatteryAlert = slash.GetFlag(0xA2);
                SlashBatteryIndicator = slash.GetFlag(0xA3);
                SlashPowerSaving = slash.GetFlag(0xA8);
            }
            catch { }
        }

        partial void OnSlashIntervalChanged(int value)
        {
            if (!_isReady) return;
            AppConfig.Set("matrix_interval", Math.Clamp(value, 0, 5));
            Program.matrixControl?.SetDevice();
        }

        partial void OnSlashBootAnimationChanged(bool value) => WithSlash(s => { s.SetFlag(0xA0, value); s.SetFlag(0xA4, value); });
        partial void OnSlashSleepAnimationChanged(bool value) => WithSlash(s => s.SetFlag(0xA1, value));
        partial void OnSlashLowBatteryAlertChanged(bool value) => WithSlash(s => s.SetFlag(0xA2, value));
        partial void OnSlashBatteryIndicatorChanged(bool value) => WithSlash(s => { s.SetBatteryAnimation(value); s.SetFlag(0xA5, value); });
        partial void OnSlashPowerSavingChanged(bool value) => WithSlash(s => { s.SetPowerSaving(value, SlashDimLevel); s.SetFlag(0xA8, value); });

        partial void OnSlashSleepPatternChanged(int value)
        {
            WithSlash(s => s.SetRecordByte(0xA1, 6, value == 0 ? (byte)0x00 : Arsenal.AnimeMatrix.SlashDevice.GetModeCode((Arsenal.AnimeMatrix.SlashMode)MatrixMode)));
        }

        partial void OnSlashDimLevelChanged(int value)
        {
            if (!_isReady) return;
            AppConfig.Set("slash_dim", value);
            WithSlash(s => s.SetPowerSaving(SlashPowerSaving, value));
        }

        private void WithSlash(Action<Arsenal.AnimeMatrix.SlashDevice> action)
        {
            if (!_isReady || Program.matrixControl?.deviceSlash is not { } slash) return;
            try { action(slash); } catch { }
        }

        private static void UpdateSelection(IEnumerable<SelectableIntOption> options, int selected)
        {
            foreach (var option in options) option.IsSelected = option.Value == selected;
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
