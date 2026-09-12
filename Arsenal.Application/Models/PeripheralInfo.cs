using CommunityToolkit.Mvvm.ComponentModel;

namespace Arsenal.Application.Models
{
    /// <summary>Where an ASUS package has got to on its way onto this machine.</summary>
    public enum DriverDownloadState
    {
        /// <summary>Nothing fetched yet.</summary>
        Idle,
        /// <summary>Bytes are arriving.</summary>
        Downloading,
        /// <summary>The installer is on disk and can be run.</summary>
        Ready,
        /// <summary>The transfer stopped short. The URL is still good; the attempt was not.</summary>
        Failed
    }

    /// <summary>How an ASUS package compares to what is installed on this machine.</summary>
    public enum UpdateState
    {
        /// <summary>No installed counterpart could be identified.</summary>
        Unknown,
        /// <summary>Installed at the same version ASUS publishes, or newer.</summary>
        UpToDate,
        /// <summary>The installed driver is newer than the package ASUS publishes.</summary>
        Newer,
        /// <summary>Installed, but ASUS publishes a higher version.</summary>
        Outdated,
        /// <summary>Not installed.</summary>
        NotInstalled
    }

    /// <summary>
    /// Observable only for the download members below. Everything else is filled in
    /// once, when the scan builds the list, and never changes afterwards.
    /// </summary>
    public partial class UpdateInfo : ObservableObject
    {
        public bool IsUpdateAvailable { get; set; }
        public string CurrentVersion { get; set; } = string.Empty;
        public string LatestVersion { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string ReleaseNotes { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        public string PublishedAt { get; set; } = string.Empty;

        /// <summary>
        /// Signed package size for an Arsenal application update, in bytes. Distinct
        /// from <see cref="FileSize"/>, which is ASUS's already-worded driver size:
        /// this one has to drive a progress bar, so it has to be a number.
        /// </summary>
        public long PackageBytes { get; set; }

        /// <summary>
        /// Release notes as separate lines. <see cref="ReleaseNotes"/> keeps the single
        /// pre-joined block the About page shows; the update card renders a list.
        /// </summary>
        public List<string> ReleaseNoteLines { get; set; } = new();

        /// <summary>Hardware IDs supplied by ASUS for matching this package.</summary>
        public List<string> HardwareIds { get; set; } = new();

        /// <summary>
        /// Download size, already worded by ASUS ("4.35 MB", "1.01 GB"). Taken as given
        /// rather than reformatted from a byte count, because a byte count is not what
        /// the endpoint returns.
        /// </summary>
        public string FileSize { get; set; } = string.Empty;

        public bool HasFileSize => !string.IsNullOrWhiteSpace(FileSize);

        /// <summary>
        /// ASUS's own SHA-256 for the package. Present on every entry the endpoint
        /// returns, and checked after downloading because the file is then offered to
        /// the shell to run.
        /// </summary>
        public string Sha256 { get; set; } = string.Empty;

        /// <summary>Alternative vendor packages for hardware this laptop does not have.</summary>
        public bool IsHidden { get; set; }

        /// <summary>Version found on this machine, empty when nothing matched.</summary>
        public string InstalledVersion { get; set; } = string.Empty;

        public UpdateState State { get; set; } = UpdateState.Unknown;

        public string InstalledVersionText => string.IsNullOrWhiteSpace(InstalledVersion)
            ? State == UpdateState.NotInstalled ? "Not detected" : "Unknown"
            : InstalledVersion;

        public string StateText => State switch
        {
            UpdateState.UpToDate => string.IsNullOrEmpty(InstalledVersion) ? "Up to date" : $"Up to date · {InstalledVersion}",
            UpdateState.Newer => $"Newer than ASUS · {InstalledVersion}",
            UpdateState.Outdated => $"Update available · {InstalledVersion} → {LatestVersion}",
            UpdateState.NotInstalled => "Not installed",
            _ => "Installed version unknown"
        };

        /// <summary>Short label used by the coloured status badge.</summary>
        public string StateLabel => State switch
        {
            UpdateState.UpToDate => "Up to date",
            UpdateState.Newer => "Newer than ASUS",
            UpdateState.Outdated => "Update available",
            UpdateState.NotInstalled => "Optional",
            _ => "Needs review"
        };

        public string DownloadActionText => State switch
        {
            UpdateState.Outdated => "Get update",
            UpdateState.Newer => "ASUS file",
            _ => "Download"
        };

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsIdle))]
        [NotifyPropertyChangedFor(nameof(IsDownloading))]
        [NotifyPropertyChangedFor(nameof(IsDownloaded))]
        [NotifyPropertyChangedFor(nameof(HasFailed))]
        private DriverDownloadState _downloadState = DriverDownloadState.Idle;

        /// <summary>Percent complete, or -1 while the size is still unknown.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsProgressKnown))]
        [NotifyPropertyChangedFor(nameof(DownloadProgressText))]
        [NotifyPropertyChangedFor(nameof(DownloadFraction))]
        private int _downloadProgress = -1;

        /// <summary>The same figure as 0-1, which is what the progress hairline takes.</summary>
        public double DownloadFraction => IsProgressKnown ? DownloadProgress / 100d : 0d;

        /// <summary>Where the finished installer landed, empty until it has.</summary>
        [ObservableProperty]
        private string _downloadedPath = string.Empty;

        public bool IsIdle => DownloadState == DriverDownloadState.Idle;
        public bool IsDownloading => DownloadState == DriverDownloadState.Downloading;
        public bool IsDownloaded => DownloadState == DriverDownloadState.Ready;
        public bool HasFailed => DownloadState == DriverDownloadState.Failed;

        /// <summary>
        /// False while the server has not said how large the file is, which is what
        /// puts the progress bar into its indeterminate state rather than showing a
        /// filled bar that is not measuring anything.
        /// </summary>
        public bool IsProgressKnown => DownloadProgress >= 0;

        /// <summary>
        /// An ellipsis rather than a figure until the server has declared a length, so
        /// the chip never shows a confident 0%.
        /// </summary>
        public string DownloadProgressText => IsProgressKnown ? $"{DownloadProgress}%" : "…";

        /// <summary>Sorts what needs attention to the top of the list.</summary>
        public int SortRank => State switch
        {
            UpdateState.Outdated => 0,
            UpdateState.NotInstalled => 1,
            UpdateState.Unknown => 2,
            UpdateState.Newer => 3,
            _ => 3
        };
    }

    public class PeripheralDeviceModel
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string DeviceType { get; set; } = "Mouse"; // Mouse, Keyboard, Headset, Dock
        public int BatteryPercentage { get; set; } = -1;
        public bool IsCharging { get; set; } = false;
        public bool IsConnected { get; set; } = true;
        public int CurrentDpi { get; set; } = 800;
        public int PollingRate { get; set; } = 1000;
        public List<int> DpiProfiles { get; set; } = new();

        /// <summary>
        /// What this sensor will take. Mice differ by an order of magnitude - a TUF M3
        /// stops at 2000 while an Aimpoint reaches 36000 - so a fixed track either
        /// pretends a cheap sensor goes further than it does or truncates an expensive
        /// one. The service clamps writes to the same bounds, so a track drawn from
        /// anything else is drawing a lie.
        /// </summary>
        public int MinDpi { get; set; } = 100;
        public int MaxDpi { get; set; } = 2000;

        /// <summary>The sensor's own DPI granularity, usually 50 or 100.</summary>
        public int DpiStep { get; set; } = 50;

        /// <summary>
        /// The rates this mouse actually reports at, in Hz. Not a uniform step - the
        /// series doubles - so the slider takes them as explicit stops rather than
        /// letting the thumb rest on a rate the device would silently round away.
        /// </summary>
        public List<int> PollingRates { get; set; } = new();
        public int SleepMinutes { get; set; } = 3;
        public int LowBatteryWarningPercent { get; set; } = 20;
        public bool IsMouse => DeviceType == "Mouse";
        public bool IsKeyboard => DeviceType == "Keyboard";
        public bool HasBattery { get; set; }
        public bool HasKeyboardLighting { get; set; }
        public bool HasKeyboardPower { get; set; }
        public int LowBatteryWarningMaximum { get; set; } = 50;
        public int LowBatteryWarningStep { get; set; } = 10;
        public string BatteryText => HasBattery && BatteryPercentage >= 0
            ? IsCharging ? $"{BatteryPercentage}% · charging" : $"{BatteryPercentage}%"
            : "Wired";
        public string SummaryText => IsMouse
            ? $"Polling {PollingRate} Hz"
            : HasBattery ? "Wireless ASUS keyboard" : "ASUS keyboard";

        public List<PeripheralOptionModel> LightingModes { get; set; } = new();
        public int LightingMode { get; set; }
        public int LightingBrightness { get; set; } = 100;
        public int MaxLightingBrightness { get; set; } = 100;
        public int LightingSpeed { get; set; } = 1;
        public int PrimaryColorArgb { get; set; } = unchecked((int)0xFFFF0000);
        public int SecondaryColorArgb { get; set; } = unchecked((int)0xFF000000);
        public bool HasProfiles { get; set; }
        public int Profile { get; set; }
        public int ProfileCount { get; set; }
        public bool HasKeyboardOled { get; set; }
        public bool KeyboardOledEnabled { get; set; }
        public int KeyboardOledBrightness { get; set; } = 100;
        public int KeyboardOledMode { get; set; }
        public int KeyboardOledAnimationCount { get; set; }
        public bool KeyboardOledClock { get; set; }
    }

    public class PeripheralOptionModel
    {
        public int Value { get; set; }
        public string Label { get; set; } = string.Empty;
    }
}
