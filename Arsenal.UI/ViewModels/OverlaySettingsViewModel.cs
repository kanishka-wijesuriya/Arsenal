using Arsenal.Application.Models;
using Arsenal.Overlay;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Media;
using MediaColor = System.Windows.Media.Color;

namespace Arsenal.UI.ViewModels
{
    /// <summary>
    /// How the hardware overlay looks: G-Helper's overlay settings window.
    /// </summary>
    /// <remarks>
    /// Each of the four modes keeps its own set of blocks, so choosing a mode here
    /// shows that mode's blocks, and clicking the overlay with the hotkey held steps
    /// through the same modes. Every change is redrawn on the overlay at once.
    /// </remarks>
    public partial class OverlaySettingsViewModel : ObservableObject
    {
        private bool _loading;

        public IReadOnlyList<PeripheralOptionModel> ModeOptions { get; } = new[]
        {
            new PeripheralOptionModel { Value = 0, Label = "Default" },
            new PeripheralOptionModel { Value = 1, Label = "Light" },
            new PeripheralOptionModel { Value = 2, Label = "Full" },
            new PeripheralOptionModel { Value = 3, Label = "Complete" },
        };

        public IReadOnlyList<PeripheralOptionModel> BatteryOptions { get; } = new[]
        {
            new PeripheralOptionModel { Value = 1, Label = "Always" },
            new PeripheralOptionModel { Value = -1, Label = "On battery" },
            new PeripheralOptionModel { Value = 0, Label = "Never" },
        };

        [ObservableProperty] private int _mode;
        [ObservableProperty] private bool _showFps;
        [ObservableProperty] private bool _showTemperatures;
        [ObservableProperty] private bool _showFans;
        [ObservableProperty] private bool _showChart;
        [ObservableProperty] private bool _showPower;
        [ObservableProperty] private bool _showLoad;
        [ObservableProperty] private bool _showRam;
        [ObservableProperty] private bool _showLabels;
        [ObservableProperty] private int _battery;
        [ObservableProperty] private int _size = 100;
        [ObservableProperty] private int _opacity = 50;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CpuColorBrush))]
        private MediaColor _cpuColor;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(GpuColorBrush))]
        private MediaColor _gpuColor;

        public SolidColorBrush CpuColorBrush => new(CpuColor);
        public SolidColorBrush GpuColorBrush => new(GpuColor);

        public OverlaySettingsViewModel()
        {
            Load();
        }

        private void Load()
        {
            _loading = true;
            try
            {
                Mode = Math.Clamp(AppConfig.Get("overlay_mode", 0), 0, ModeOptions.Count - 1);
                LoadBlocks();
                Size = Math.Clamp(AppConfig.Get("overlay_scale_percent", 100), 35, 300);
                Opacity = (int)Math.Round(Math.Clamp(AppConfig.Get("overlay_alpha", 128), 0, 255) * 100 / 255.0);
                CpuColor = ToMedia(HardwareOverlay.CpuColor);
                GpuColor = ToMedia(HardwareOverlay.GpuColor);
            }
            finally
            {
                _loading = false;
            }
        }

        private void LoadBlocks()
        {
            ShowFps = HardwareOverlay.BlockShown("overlay_show_fps", Mode);
            ShowTemperatures = HardwareOverlay.BlockShown("overlay_show_temp", Mode);
            ShowFans = HardwareOverlay.BlockShown("overlay_show_fans", Mode);
            ShowChart = HardwareOverlay.BlockShown("overlay_show_chart", Mode);
            ShowPower = HardwareOverlay.BlockShown("overlay_show_power", Mode);
            ShowLoad = HardwareOverlay.BlockShown("overlay_show_usage", Mode);
            ShowRam = HardwareOverlay.BlockShown("overlay_show_ram", Mode);
            ShowLabels = HardwareOverlay.BlockShown("overlay_names", Mode);
            Battery = HardwareOverlay.BatteryState(Mode);
        }

        partial void OnModeChanged(int value)
        {
            if (_loading) return;
            AppConfig.Set("overlay_mode", value);
            _loading = true;
            try { LoadBlocks(); }
            finally { _loading = false; }
            Refresh();
        }

        partial void OnShowFpsChanged(bool value) => SetBlock("overlay_show_fps", value);
        partial void OnShowTemperaturesChanged(bool value) => SetBlock("overlay_show_temp", value);
        partial void OnShowFansChanged(bool value) => SetBlock("overlay_show_fans", value);
        partial void OnShowChartChanged(bool value) => SetBlock("overlay_show_chart", value);
        partial void OnShowPowerChanged(bool value) => SetBlock("overlay_show_power", value);
        partial void OnShowLoadChanged(bool value) => SetBlock("overlay_show_usage", value);
        partial void OnShowRamChanged(bool value) => SetBlock("overlay_show_ram", value);
        partial void OnShowLabelsChanged(bool value) => SetBlock("overlay_names", value);

        private void SetBlock(string key, bool shown)
        {
            if (_loading) return;
            AppConfig.Set(HardwareOverlay.ModeKey(key, Mode), shown ? 1 : 0);
            Refresh();
        }

        partial void OnBatteryChanged(int value)
        {
            if (_loading) return;
            AppConfig.Set(HardwareOverlay.ModeKey("overlay_show_battery", Mode), value);
            Refresh();
        }

        partial void OnSizeChanged(int value)
        {
            if (_loading) return;
            AppConfig.Set("overlay_scale_percent", value);
            Refresh();
        }

        partial void OnOpacityChanged(int value)
        {
            if (_loading) return;
            AppConfig.Set("overlay_alpha", (int)Math.Round(Math.Clamp(value, 0, 100) * 255 / 100.0));
            Refresh();
        }

        partial void OnCpuColorChanged(MediaColor value) => SetColor("overlay_color_cpu", value);
        partial void OnGpuColorChanged(MediaColor value) => SetColor("overlay_color_gpu", value);

        private void SetColor(string key, MediaColor color)
        {
            if (_loading) return;
            AppConfig.Set(key, $"{color.R:X2}{color.G:X2}{color.B:X2}");
            Refresh();
        }

        /// <summary>Puts every mode's blocks, the size, opacity and colours back to G-Helper's defaults.</summary>
        [RelayCommand]
        private void Reset()
        {
            string[] keys = { "overlay_show_fps", "overlay_show_temp", "overlay_show_fans", "overlay_show_chart",
                "overlay_show_power", "overlay_show_usage", "overlay_show_ram", "overlay_names", "overlay_show_battery" };
            for (int mode = 0; mode < ModeOptions.Count; mode++)
                foreach (string key in keys) AppConfig.Remove(HardwareOverlay.ModeKey(key, mode));

            AppConfig.Remove("overlay_scale_percent");
            AppConfig.Remove("overlay_alpha");
            AppConfig.Remove("overlay_color_cpu");
            AppConfig.Remove("overlay_color_gpu");

            Load();
            Refresh();
        }

        private static void Refresh() => Program.hardwareOverlay?.RefreshSettings();

        private static MediaColor ToMedia(System.Drawing.Color color) => MediaColor.FromRgb(color.R, color.G, color.B);
    }
}
