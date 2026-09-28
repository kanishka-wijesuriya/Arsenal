using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Arsenal.Application.Models;
using Arsenal.Application.Services.Contracts;
using Arsenal.Helpers;
using Arsenal.UI.Services;
using Arsenal.UI.Services.Remote;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;

namespace Arsenal.UI.ViewModels
{
    public partial class UpdatesViewModel : ObservableObject
    {
        private readonly IUpdateService _updateService;

        [ObservableProperty]
        private bool _isChecking = false;

        [ObservableProperty]
        private string _statusMessage = "Ready to scan this laptop";

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

        public string AllDriversFilterText => string.Format("All  {0}", DriverCount);
        public string OutdatedDriversFilterText => string.Format("Updates  {0}", OutdatedDriverCount);
        public string CurrentDriversFilterText => string.Format("Current  {0}", CurrentDriverCount);
        public string OptionalDriversFilterText => string.Format("Optional  {0}", OptionalDriverCount);

        public UpdatesViewModel(IUpdateService updateService)
        {
            _updateService = updateService;
        }

        [RelayCommand]
        public async Task CheckUpdates()
        {
            IsChecking = true;
            StatusMessage = "Reading ASUS support and installed Windows drivers...";
            try
            {
                List<UpdateInfo> drivers = await _updateService.CheckAsusUpdatesAsync();

                // A scan returns fresh records, every one of them Idle, so a package
                // fetched before the application was last closed would be offered for
                // download again. The file in the downloads folder outlives the record,
                // and is what the card should be reflecting.
                foreach (UpdateInfo driver in drivers)
                {
                    if (string.IsNullOrWhiteSpace(driver.DownloadUrl)) continue;
                    string? existing = _updateService.FindDownloadedPackage(driver.DownloadUrl);
                    if (existing is null) continue;

                    driver.DownloadedPath = existing;
                    driver.DownloadProgress = 100;
                    driver.DownloadState = DriverDownloadState.Ready;
                }

                AsusUpdates.Clear();
                foreach (UpdateInfo driver in drivers) AsusUpdates.Add(driver);

                DriverCount = drivers.Count;
                OutdatedDriverCount = drivers.Count(driver => driver.State == UpdateState.Outdated);
                CurrentDriverCount = drivers.Count(driver => driver.State is UpdateState.UpToDate or UpdateState.Newer);
                OptionalDriverCount = drivers.Count(driver => driver.State is UpdateState.Unknown or UpdateState.NotInstalled);
                RefreshFilterLabels();
                ApplyDriverFilter();

                StatusMessage = OutdatedDriverCount > 0
                    ? string.Format(
                        OutdatedDriverCount == 1 ? "{0} driver update available" : "{0} driver updates available",
                        OutdatedDriverCount)
                    : OptionalDriverCount > 0
                        ? string.Format(
                            OptionalDriverCount == 1 ? "Installed versions verified · {0} optional package not detected" : "Installed versions verified · {0} optional packages not detected",
                            OptionalDriverCount)
                        : "Every detected ASUS driver is up to date";
            }
            catch (Exception ex)
            {
                StatusMessage = "Driver scan could not be completed";
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
                    StatusMessage = string.Format("{0} could not be downloaded.", driver.Title);
                }
                else
                {
                    driver.DownloadedPath = path;
                    driver.DownloadState = DriverDownloadState.Ready;
                    StatusMessage = string.Format("{0} downloaded and ready to install.", driver.Title);
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
                StatusMessage = string.Format("{0} could not be downloaded.", driver.Title);
            }
        }

        [RelayCommand]
        public void ShowDriverInFolder(UpdateInfo? driver)
        {
            string path = driver?.DownloadedPath ?? string.Empty;
            try
            {
                if (File.Exists(path))
                    Process.Start(new ProcessStartInfo(ProcessHelper.SystemPath("explorer.exe"), $"/select,\"{path}\"") { UseShellExecute = true });
                else if (Directory.Exists(_updateService.DownloadFolder))
                    Process.Start(new ProcessStartInfo(_updateService.DownloadFolder) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Driver reveal: " + ex.Message);
            }
        }
    }
}
