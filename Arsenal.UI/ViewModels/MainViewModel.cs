using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Arsenal.Application.Models;
using Arsenal.Application.Services.Contracts;
using Arsenal.Mode;
using Arsenal.Display;
using Arsenal.Helpers;
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

        /// <summary>
        /// Whether this instance is running with administrator rights.
        /// </summary>
        /// <remarks>
        /// Asked once and never again. A process cannot gain or lose its elevation while
        /// it is running - restarting as administrator is a new process - so there is
        /// nothing to watch for and nothing that can change this while the window is
        /// open.
        /// </remarks>
        public bool IsElevated { get; } = ProcessHelper.IsUserAdministrator();

        public string ElevationLabel => IsElevated ? "ADMIN" : "STANDARD";

        /// <summary>
        /// What being on one side of this actually costs, rather than the word again.
        /// </summary>
        /// <remarks>
        /// Nearly everything Arsenal does works either way, because the ACPI calls it
        /// makes go through a driver that does not care. The things that do care are
        /// machine-wide Windows settings and the scheduled tasks, which is a short and
        /// worth naming list - otherwise the pill reads as a warning about nothing.
        /// </remarks>
        public string ElevationDetail => IsElevated
            ? "Running as administrator. Machine-wide settings, the startup task and Windows smart charging can all be written."
            : "Running as a standard user. Everything on this machine's hardware works; reporting the charge limit to Windows and scheduling Arsenal at sign-in need administrator rights.";

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
}
