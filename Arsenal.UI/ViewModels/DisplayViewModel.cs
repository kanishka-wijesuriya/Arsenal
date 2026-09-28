using Arsenal.Helpers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Arsenal.Application.Models;
using Arsenal.Application.Services.Contracts;
using Arsenal.Display;
using Arsenal.UI.Services;
using System.Collections.ObjectModel;
using Wpf.Ui.Controls;

namespace Arsenal.UI.ViewModels
{
    /// <summary>
    /// One row of the read-only connected-displays list.
    /// </summary>
    public class ConnectedDisplayItem
    {
        public ConnectedDisplayItem(ActiveDisplayInfo display)
        {
            Name = display.IsInternal ? $"{display.Name} · built-in" : display.Name;

            // The maximum is only worth saying when the display is not already at it.
            Detail = display.MaxRefreshRate > display.RefreshRate
                ? $"{display.Width} × {display.Height} · up to {display.MaxRefreshRate} Hz"
                : $"{display.Width} × {display.Height}";

            if (display.IsPrimary) Detail += " · primary";

            RefreshRate = $"{display.RefreshRate} Hz";
        }

        public string Name { get; }
        public string Detail { get; }
        public string RefreshRate { get; }
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

        /// <summary>
        /// The refresh rate and overdrive writes go to the built-in ASUS panel and
        /// nowhere else. While that panel is off - lid shut on a dock, or "second screen
        /// only" - SetScreen returns without doing anything, so the buttons are greyed
        /// rather than left looking live.
        /// </summary>
        [ObservableProperty]
        private bool _isInternalPanelActive = true;

        public string RefreshRateDescription => IsInternalPanelActive
            ? "Applies to the built-in panel. External monitors keep their own rate."
            : "The built-in panel is off, so there is nothing to apply this to. Open the lid or turn the laptop display back on in Windows.";

        /// <summary>
        /// Read-only: what Windows is driving right now, so the page answers "what is my
        /// external monitor running at" without sending the user to Windows settings.
        /// </summary>
        public ObservableCollection<ConnectedDisplayItem> ConnectedDisplays { get; } = new();

        public bool HasConnectedDisplays => ConnectedDisplays.Count > 0;

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

        /// <summary>
        /// The backlight belongs to the built-in panel, so it goes with it. An external
        /// monitor keeps its own brightness and is not Arsenal's to change.
        /// </summary>
        public bool IsPanelBrightnessAvailable => IsInternalPanelActive;

        public string PanelBrightnessDescription => IsInternalPanelActive
            ? "The display backlight, the same level the brightness keys change."
            : "The built-in panel is off, so there is no backlight to change. An external monitor keeps its own brightness.";

        public bool IsOledDimmingAvailable => IsOledPanel && IsColorPipelineEnabled && IsInternalPanelActive;

        public string OledDimmingDescription => !IsInternalPanelActive
            ? "The built-in panel is off, so there is nothing to dim. Open the lid or turn the laptop display back on in Windows."
            : "Software dimming applied by the ASUS GameVisual engine. It is unavailable while the visual profile is Disabled, because both controls share the same color pipeline.";

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
            0 => "Eco · the dedicated GPU is powered down for the longest battery life",
            1 => "Standard · hybrid, applications choose which GPU to run on",
            2 => "Ultimate · the dedicated GPU drives the display, and switching restarts Windows",
            _ => "Optimized · follows the power source, Eco on battery and Standard on mains"
        };

        partial void OnCurrentGpuModeChanged(int value) => OnPropertyChanged(nameof(GpuModeDescription));

        [ObservableProperty]
        private bool _isInstallingProfiles;

        [ObservableProperty]
        private bool _hasColorProfiles;

        [ObservableProperty]
        private bool _canInstallColorProfiles;

        [ObservableProperty]
        private string _colorProfileStatus = "Checking installed profiles…";

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
            IsInternalPanelActive = _displayService.IsInternalPanelActive;
            RefreshConnectedDisplays();
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
                IsInternalPanelActive = snapshot.ScreenEnabled;
                // InitScreen fires this on every display topology change, which is
                // exactly when the attached monitors and their rates have moved.
                RefreshConnectedDisplays();
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
            OnPropertyChanged(nameof(OledDimmingDescription));
        }

        partial void OnPanelBrightnessChanged(int value)
        {
            // A value that arrived from the hardware watcher is already applied.
            // Writing it back would fight the brightness keys mid-press. With the panel
            // off there is nothing behind the write either.
            if (_isReady && !_applyingExternalBrightness && IsPanelBrightnessAvailable)
                _panelBrightnessWriter.Push(Math.Clamp(value, 0, 100));
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
        partial void OnIsInternalPanelActiveChanged(bool value)
        {
            OnPropertyChanged(nameof(RefreshRateDescription));
            OnPropertyChanged(nameof(IsPanelBrightnessAvailable));
            OnPropertyChanged(nameof(PanelBrightnessDescription));
            OnPropertyChanged(nameof(IsOledDimmingAvailable));
            OnPropertyChanged(nameof(OledDimmingDescription));
        }

        private void RefreshConnectedDisplays()
        {
            ConnectedDisplays.Clear();
            foreach (var display in _displayService.GetConnectedDisplays())
                ConnectedDisplays.Add(new ConnectedDisplayItem(display));

            OnPropertyChanged(nameof(HasConnectedDisplays));
        }

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
            ColorProfileStatus = "Installing ASUS color profiles…";
            try
            {
                bool installed = await _displayService.InstallColorProfilesAsync();
                RefreshColorProfileState();
                if (!installed) ColorProfileStatus = "Installation was cancelled or no matching profile was found.";
            }
            finally { IsInstallingProfiles = false; }
        }

        private void RefreshColorProfileState()
        {
            HasColorProfiles = _displayService.HasColorProfiles;
            CanInstallColorProfiles = _displayService.CanInstallColorProfiles;
            ColorProfileStatus = HasColorProfiles
                ? "ASUS color profiles are installed and ready."
                : CanInstallColorProfiles
                    ? "A matching ASUS profile package is available for this display."
                    : "No matching ASUS profile package is available for this device.";
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
