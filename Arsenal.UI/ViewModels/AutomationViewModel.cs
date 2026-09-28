using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Arsenal.Application.Services;
using Arsenal.Application.Services.Contracts;
using Arsenal.Helpers;
using Arsenal.UI.Services;
using Arsenal.UI.Services.Remote;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace Arsenal.UI.ViewModels
{
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
}
