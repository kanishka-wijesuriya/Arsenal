using Arsenal.Application.Models;
using Arsenal.Application.Services.Contracts;
using Arsenal.AutoUpdate;
using Arsenal.Helpers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace Arsenal.UI.ViewModels
{
    /// <summary>What the update card is currently showing.</summary>
    public enum UpdateOverlayState
    {
        /// <summary>A release was found; the notes and the two buttons are on screen.</summary>
        Available,

        /// <summary>The package is being fetched and verified, with a live bar.</summary>
        Downloading,

        /// <summary>The download, verification or hand-off did not complete.</summary>
        Failed
    }

    /// <summary>
    /// The in-app update card that replaced the system message box.
    ///
    /// It owns the whole interaction: the release notes, the download with its byte
    /// progress, and the failure state. On success the installer replaces this
    /// executable and ends the process, so there is deliberately no completed state -
    /// the last thing this view model ever shows is the bar at 100%.
    /// </summary>
    public partial class UpdateOverlayViewModel : ObservableObject
    {
        private readonly IUpdateService _updateService;
        private CancellationTokenSource? _cancellation;
        private ReleaseUpdate? _presentedRelease;

        public UpdateOverlayViewModel(IUpdateService updateService)
        {
            _updateService = updateService;
        }

        /// <summary>Raised when the card should close: Later, or a dismissed failure.</summary>
        public event Action? Dismissed;

        [ObservableProperty]
        private UpdateOverlayState _state = UpdateOverlayState.Available;

        [ObservableProperty]
        private string _headline = string.Empty;

        [ObservableProperty]
        private string _versionLine = string.Empty;

        [ObservableProperty]
        private string _sizeText = string.Empty;

        [ObservableProperty]
        private ObservableCollection<string> _notes = new();

        /// <summary>0 to 1, for the progress line.</summary>
        [ObservableProperty]
        private double _progress;

        [ObservableProperty]
        private string _percentText = "0%";

        /// <summary>"4.2 MB of 11.3 MB", the caption under the bar.</summary>
        [ObservableProperty]
        private string _progressText = string.Empty;

        [ObservableProperty]
        private string _statusText = string.Empty;

        [ObservableProperty]
        private string _errorText = string.Empty;

        public bool IsAvailable => State == UpdateOverlayState.Available;
        public bool IsDownloading => State == UpdateOverlayState.Downloading;
        public bool HasFailed => State == UpdateOverlayState.Failed;

        partial void OnStateChanged(UpdateOverlayState value)
        {
            OnPropertyChanged(nameof(IsAvailable));
            OnPropertyChanged(nameof(IsDownloading));
            OnPropertyChanged(nameof(HasFailed));
        }

        /// <summary>Fills the card from a release and returns it to its opening state.</summary>
        public void Present(ReleaseUpdate release)
        {
            _presentedRelease = release;
            Headline = string.IsNullOrWhiteSpace(release.Title) ? $"Arsenal {release.Version}" : release.Title;
            VersionLine = AppStrings.Format(
                "UpdateVersionLine",
                ReleaseVersion.CurrentDisplayString(),
                ReleaseVersion.DisplayString(release.Version));
            SizeText = FormatSize(release.PackageBytes);
            TotalBytes = release.PackageBytes;

            Notes.Clear();
            foreach (string note in release.Notes.Take(8)) Notes.Add(note);
            if (Notes.Count == 0) Notes.Add(AppStrings.Get("UpdateGenericNote"));

            ResetProgress();
            ErrorText = string.Empty;
            StatusText = string.Empty;
            State = UpdateOverlayState.Available;
        }

        /// <summary>
        /// Fills the card from the update check the About page already ran, which
        /// carries the same signed details in the shape the service exposes.
        /// </summary>
        public void Present(UpdateInfo info)
        {
            // About has already run IUpdateService.CheckForUpdatesAsync, so the service
            // owns the corresponding signed release. Startup supplies that release
            // directly through the other Present overload instead.
            _presentedRelease = null;
            Headline = string.IsNullOrWhiteSpace(info.Title) ? $"Arsenal {info.LatestVersion}" : info.Title;
            VersionLine = AppStrings.Format("UpdateVersionLine", info.CurrentVersion, info.LatestVersion);
            SizeText = FormatSize(info.PackageBytes);
            TotalBytes = info.PackageBytes;

            Notes.Clear();
            foreach (string note in info.ReleaseNoteLines.Take(8)) Notes.Add(note);
            if (Notes.Count == 0) Notes.Add(AppStrings.Get("UpdateGenericNote"));

            ResetProgress();
            ErrorText = string.Empty;
            StatusText = string.Empty;
            State = UpdateOverlayState.Available;
        }

        /// <summary>Signed package size, used to word the bar's caption.</summary>
        public long TotalBytes { get; private set; }

        [RelayCommand]
        public async Task Install()
        {
            if (IsDownloading) return;

            State = UpdateOverlayState.Downloading;
            ErrorText = string.Empty;
            StatusText = AppStrings.Get("UpdateDownloading");
            ResetProgress();

            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();

            // Constructed here, on the UI thread, so its callbacks come back to the
            // dispatcher rather than to the socket's thread.
            var progress = new Progress<long>(OnBytesReceived);

            try
            {
                bool launched = _presentedRelease is null
                    ? await _updateService.DownloadAndInstallUpdateAsync(progress, _cancellation.Token)
                    : await _updateService.DownloadAndInstallUpdateAsync(_presentedRelease, progress, _cancellation.Token);

                // Only reached when the hand-off failed: a successful install ends the
                // process from inside the call above.
                if (!launched) Fail(AppStrings.Get("UpdateInstallFailed"));
            }
            catch (OperationCanceledException)
            {
                ResetProgress();
                StatusText = string.Empty;
                State = UpdateOverlayState.Available;
            }
            catch (Exception exception)
            {
                Logger.WriteLine("Update overlay install: " + exception.Message);
                Fail(AppStrings.Get("UpdateInstallFailed"));
            }
        }

        [RelayCommand]
        public void Cancel()
        {
            if (!IsDownloading) return;
            _cancellation?.Cancel();
        }

        [RelayCommand]
        public void Later()
        {
            _cancellation?.Cancel();
            Dismissed?.Invoke();
        }

        [RelayCommand]
        public void Retry()
        {
            State = UpdateOverlayState.Available;
            ErrorText = string.Empty;
            ResetProgress();
        }

        private void OnBytesReceived(long received)
        {
            if (TotalBytes <= 0)
            {
                ProgressText = FormatSize(received);
                return;
            }

            double fraction = Math.Clamp((double)received / TotalBytes, 0, 1);
            Progress = fraction;
            PercentText = ((int)Math.Round(fraction * 100)) + "%";
            ProgressText = AppStrings.Format("UpdateProgressOf", FormatSize(received), FormatSize(TotalBytes));
        }

        private void Fail(string message)
        {
            ErrorText = message;
            StatusText = string.Empty;
            State = UpdateOverlayState.Failed;
        }

        private void ResetProgress()
        {
            Progress = 0;
            PercentText = "0%";
            ProgressText = TotalBytes > 0
                ? AppStrings.Format("UpdateProgressOf", FormatSize(0), FormatSize(TotalBytes))
                : string.Empty;
        }

        /// <summary>
        /// Decimal megabytes, matching how the size is published on the download page
        /// and in the release ledger, so the two never disagree by 5%.
        /// </summary>
        private static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "0.0 MB";
            return (bytes / 1000000d).ToString("0.0") + " MB";
        }
    }
}
