using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Arsenal.Application.Models;
using Arsenal.Application.Services.Contracts;
using Arsenal.UI.Services;
using System.Collections.ObjectModel;
using Wpf.Ui.Controls;

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
            new HomeControlDefinition("performance", "Performance mode", "Silent, Balanced and Turbo BIOS and Windows power modes", SymbolRegular.Gauge24),
            new HomeControlDefinition("gpu", "GPU mode", "Eco, Standard, Ultimate and Optimized graphics modes", SymbolRegular.DeveloperBoard24),
            new HomeControlDefinition("charge_limit", "Charge limit", "Protect the battery by choosing where charging stops", SymbolRegular.BatteryCharge24),
            new HomeControlDefinition("refresh", "Refresh rate", "Switch between the refresh rates this panel offers, overdrive included", SymbolRegular.Desktop24),
            new HomeControlDefinition("auto_refresh", "Automatic refresh", "Let refresh rate follow the current power source", SymbolRegular.Sparkle24),
            new HomeControlDefinition("keyboard", "Keyboard light", "Choose the keyboard backlight level", SymbolRegular.Keyboard24),
            new HomeControlDefinition("touchpad", "Touchpad", "Enable or disable the built-in touchpad", SymbolRegular.CursorClick24),
            new HomeControlDefinition("full_charge", "Full charge", "Temporarily charge to 100% beyond the normal limit", SymbolRegular.BatteryCharge24),
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
        private int _refreshRate = 60;

        [ObservableProperty]
        private string _deviceName = "ASUS device";

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

        /// <summary>
        /// Held, and refreshed from the display snapshot, rather than read from the
        /// service on each notification.
        /// </summary>
        /// <remarks>
        /// <see cref="IDisplayService.MaxRefreshRate"/> asks the panel again every time
        /// it is read, and it cannot answer while Windows is not driving that panel -
        /// so the Home row could offer a different top rate from the one the Display
        /// page was showing for the same machine. The Display page reads
        /// <c>snapshot.MaxFrequency</c>, which carries the panel's real maximum across
        /// the panel being off, and this now follows the same value. The service is
        /// still the seed, for the window opened before the first snapshot arrives.
        /// </remarks>
        [ObservableProperty]
        private int _maximumRefreshRate;

        public int MinimumRefreshRate => Arsenal.Display.ScreenControl.MIN_RATE;

        /// <summary>
        /// Written the same way the Display page writes them, so one machine does not
        /// describe its own panel differently on two pages.
        /// </summary>
        public string MinimumRefreshRateLabel => $"{MinimumRefreshRate} Hz";

        public string MaximumRefreshRateLabel => $"{MaximumRefreshRate} Hz";

        /// <summary>Offered only where the panel has overdrive, as on the Display page.</summary>
        public string MaximumRefreshRateWithOverdriveLabel => $"{MaximumRefreshRate} Hz + OD";

        partial void OnMaximumRefreshRateChanged(int value)
        {
            OnPropertyChanged(nameof(MaximumRefreshRateLabel));
            OnPropertyChanged(nameof(MaximumRefreshRateWithOverdriveLabel));
            OnPropertyChanged(nameof(IsRefreshRateSupported));
        }

        /// <summary>
        /// Whether this panel has overdrive at all, which is what decides between two
        /// rate choices and three.
        /// </summary>
        [ObservableProperty]
        private bool _isOverdriveAvailable;

        [ObservableProperty]
        private bool _isOverdrive;

        partial void OnIsOverdriveAvailableChanged(bool value) => NotifyRefreshRateSelection();

        partial void OnIsOverdriveChanged(bool value) => NotifyRefreshRateSelection();

        /// <summary>A single-rate panel has nothing to choose between.</summary>
        public bool IsRefreshRateSupported => MaximumRefreshRate > MinimumRefreshRate;

        /// <remarks>
        /// The three selection rules below are the Display page's, property for
        /// property. Home offering a different set of chips - or lighting a different
        /// one of them - for the same panel state is the bug this mirrors away.
        /// </remarks>
        private bool IsAtMaximumRate => !IsAutoRefresh && RefreshRate > MinimumRefreshRate;

        public bool IsAtMinimumRefreshRate =>
            !IsAutoRefresh && RefreshRate > 0 && RefreshRate <= MinimumRefreshRate;

        /// <summary>
        /// The plain maximum. Where overdrive cannot be chosen there is only one
        /// maximum chip, so it stays lit whatever the overdrive register reads.
        /// </summary>
        public bool IsAtMaximumRefreshRate =>
            IsAtMaximumRate && (!IsOverdriveAvailable || !IsOverdrive);

        public bool IsAtMaximumRefreshRateWithOverdrive =>
            IsOverdriveAvailable && IsAtMaximumRate && IsOverdrive;

        private void NotifyRefreshRateSelection()
        {
            OnPropertyChanged(nameof(IsAtMinimumRefreshRate));
            OnPropertyChanged(nameof(IsAtMaximumRefreshRate));
            OnPropertyChanged(nameof(IsAtMaximumRefreshRateWithOverdrive));
        }

        /// <summary>
        /// The row writes to the built-in ASUS panel. While Windows is not driving that
        /// panel the write is a no-op, so the buttons are greyed and the row says why
        /// rather than offering two rates that change nothing.
        /// </summary>
        [ObservableProperty]
        private bool _isInternalPanelActive = true;

        public string RefreshRateNote => IsInternalPanelActive
            ? "Choose the panel rate without leaving Home."
            : "The built-in panel is off, so there is nothing to apply this to.";

        partial void OnIsInternalPanelActiveChanged(bool value) => OnPropertyChanged(nameof(RefreshRateNote));

        partial void OnRefreshRateChanged(int value) => NotifyRefreshRateSelection();

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
            ? "Charging stops at the set level. This model allows 60-80%, or 100%."
            : "Charging stops at the set level, protecting the battery on mains power.";

        /// <summary>
        /// The Home charge tile uses the same limit presentation as the first tile on
        /// the Battery page, including a temporary one-time charge to 100%.
        /// </summary>
        public int EffectiveChargeLimit => IsFullChargeOverride ? 100 : ChargeLimit;

        public string ChargeStopsAtText
            => BatteryPresentation.ChargeStopsAtText(BatteryPercent, EffectiveChargeLimit);

        partial void OnChargeLimitChanged(int value) => RaiseChargeLimitDependents();
        partial void OnIsFullChargeOverrideChanged(bool value) => RaiseChargeLimitDependents();
        partial void OnBatteryPercentChanged(int value) => OnPropertyChanged(nameof(ChargeStopsAtText));

        private void RaiseChargeLimitDependents()
        {
            OnPropertyChanged(nameof(EffectiveChargeLimit));
            OnPropertyChanged(nameof(ChargeStopsAtText));
        }

        /// <summary>
        /// What the selected GPU mode means, under the row's own label - the same shape
        /// the other two rows use rather than a mode name repeated back.
        /// </summary>
        public string GpuModeDescription => CurrentGpuMode switch
        {
            0 => "Eco · the dedicated GPU is powered down for the longest battery life",
            1 => "Standard · hybrid, applications choose which GPU to run on",
            2 => "Ultimate · the dedicated GPU drives the display, and switching restarts Windows",
            _ => "Optimized · follows the power source, Eco on battery and Standard on mains"
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
            MaximumRefreshRate = _displayService.MaxRefreshRate;
            IsOverdrive = _displayService.IsOverdriveEnabled;
            IsOverdriveAvailable = _displayService.IsOverdriveAvailable;
            IsInternalPanelActive = _displayService.IsInternalPanelActive;
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
                IsInternalPanelActive = snapshot.ScreenEnabled;
                MaximumRefreshRate = snapshot.MaxFrequency;
                IsOverdrive = snapshot.Overdrive > 0;
                IsOverdriveAvailable = snapshot.OverdriveSetting;
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

        /// <summary>
        /// Accepts the Display page's vocabulary - <c>min</c>, <c>max</c>, <c>max_od</c>
        /// - and writes the same way it does, so the same chip means the same thing on
        /// both pages. A bare number is still understood, for a saved layout written
        /// before the chips carried names.
        /// </summary>
        [RelayCommand]
        public void SelectRefreshRate(object? hzParam)
        {
            if (!IsInternalPanelActive) return;

            string mode = hzParam?.ToString() ?? string.Empty;
            bool withOverdrive = mode.Equals("max_od", StringComparison.OrdinalIgnoreCase);
            bool maximum = withOverdrive
                || mode.StartsWith("max", StringComparison.OrdinalIgnoreCase)
                || ToInt(hzParam, MinimumRefreshRate) > MinimumRefreshRate;

            // MAX_REFRESH is a sentinel resolved against the live panel at write time,
            // so a stale cached maximum cannot send the display to a mode it lacks.
            int hz = maximum ? Arsenal.Display.ScreenControl.MAX_REFRESH : Arsenal.Display.ScreenControl.MIN_RATE;

            // Overdrive is on only for the explicit "+ OD" chip. Where the panel has
            // none there is a single maximum chip and the write is a harmless no-op.
            bool overdrive = withOverdrive || (maximum && !IsOverdriveAvailable);

            // Optimistic only: the snapshot that follows the write settles both values.
            IsAutoRefresh = false;
            RefreshRate = maximum ? MaximumRefreshRate : MinimumRefreshRate;
            IsOverdrive = withOverdrive;
            _displayService.SetRefreshRate(hz, overdrive);
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
            NotifyRefreshRateSelection();
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
}
