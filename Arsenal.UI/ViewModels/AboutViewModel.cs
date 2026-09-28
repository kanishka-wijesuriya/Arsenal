using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Arsenal.Application.Models;
using Arsenal.Application.Services;
using Arsenal.Application.Services.Contracts;
using Arsenal.AutoUpdate;
using Arsenal.Helpers;
using Arsenal.Mode;
using Arsenal.UI.Services;
using Arsenal.UI.Services.Remote;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace Arsenal.UI.ViewModels
{
    public partial class AboutViewModel : ObservableObject
    {
        public bool CanSelfUpdate => App.CanSelfUpdate;

        /// <summary>Where the complete corresponding source for this build lives.</summary>
        /// <remarks>
        /// Arsenal is GPL-3.0, so anyone holding a binary is entitled to its source.
        /// The About page is where a person looks for that, which makes this link part
        /// of the licence obligation rather than a nicety.
        /// </remarks>
        public const string SourceUrl = "https://github.com/kanishka-wijesuriya/Arsenal";

        private const string LicenseUrl = "https://get-arsenal.com/license";
        private const string UpstreamUrl = "https://github.com/seerge/g-helper";

        /// <summary>
        /// The same destination the website's support dialog offers, so a person who
        /// donates from the app lands where they would from get-arsenal.com.
        /// </summary>
        private const string SupportUrl = "https://www.paypal.com/donate/?hosted_button_id=JHYS3YF2FNS4J";

        private readonly IUpdateService _updateService;

        [ObservableProperty]
        private string _appName = "Arsenal";

        [ObservableProperty]
        private string _version = string.Empty;

        [ObservableProperty]
        private string _hardwareModel = "ASUS ROG / TUF";

        [ObservableProperty]
        private string _biosVersion = "N/A";

        /// <summary>
        /// The machine's specification, one property per row.
        /// </summary>
        /// <remarks>
        /// Named properties rather than a collection so the page can be built from the
        /// app's own SettingsRow primitives. SettingsGroup is itself an ItemsControl that
        /// stacks those rows and draws the hairlines between them - nesting a second
        /// ItemsControl inside it left the group with one child, no dividers, and none of
        /// the padding every other page gets.
        ///
        /// Anything the machine would not answer for stays empty, and the row hides
        /// itself, which is the same treatment the capability-gated rows elsewhere use.
        /// </remarks>
        [ObservableProperty] private string _specModel = string.Empty;
        [ObservableProperty] private string _specBios = string.Empty;
        [ObservableProperty] private string _specProcessor = string.Empty;
        [ObservableProperty] private string _specMemory = string.Empty;
        [ObservableProperty] private string _specGraphics = string.Empty;
        [ObservableProperty] private string _specDisplay = string.Empty;
        [ObservableProperty] private string _specStorage = string.Empty;
        [ObservableProperty] private string _specOperatingSystem = string.Empty;

        [ObservableProperty]
        private bool _isLoadingSpecifications = true;

        [ObservableProperty]
        private UpdateInfo _updateInfo = new();

        [ObservableProperty]
        private bool _isCheckingForUpdate;

        [ObservableProperty]
        private bool _isInstallingUpdate;

        [ObservableProperty]
        private string _updateStatus = "Ready to check for an Arsenal update";

        public AboutViewModel(IUpdateService updateService)
        {
            _updateService = updateService;
            Version = ReadDisplayVersion();
            HardwareModel = AppConfig.GetModel();
            BiosVersion = AppConfig.GetBiosAndModel().Item1;

            string assemblyVersion = ReleaseVersion.CurrentString();
            UpdateInfo = new UpdateInfo { CurrentVersion = assemblyVersion, LatestVersion = assemblyVersion };
            _updateService.UpdateStatusChanged += info => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                UpdateInfo = info;
                IsCheckingForUpdate = false;
                UpdateStatus = info.IsUpdateAvailable
                    ? string.Format("Arsenal v{0} is available", info.LatestVersion)
                    : "Arsenal is up to date";
            });

            LoadSpecifications();
        }

        [RelayCommand]
        private void OpenSource() => OpenExternal(SourceUrl);

        [RelayCommand]
        private void OpenLicense() => OpenExternal(LicenseUrl);

        [RelayCommand]
        private void OpenUpstream() => OpenExternal(UpstreamUrl);

        /// <summary>
        /// Opens the donation page in the default browser. Arsenal never handles the
        /// payment itself: PayPal does, in the browser, where the address bar is visible.
        /// </summary>
        [RelayCommand]
        private void OpenSupport() => OpenExternal(SupportUrl);

        private static void OpenExternal(string url)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Logger.WriteLine("Could not open " + url + ": " + ex.Message); }
        }

        [RelayCommand]
        public async Task CheckForUpdate()
        {
            if (!CanSelfUpdate) return;
            IsCheckingForUpdate = true;
            UpdateStatus = "Checking for an Arsenal update...";
            try
            {
                UpdateInfo info = await _updateService.CheckForUpdatesAsync(true);
                UpdateInfo = info;
                UpdateStatus = info.IsUpdateAvailable
                    ? string.Format("Arsenal v{0} is available", info.LatestVersion)
                    : "Arsenal is up to date";
            }
            catch (Exception ex)
            {
                UpdateStatus = "Could not check for an Arsenal update";
                Logger.WriteLine("About update check: " + ex.Message);
            }
            finally { IsCheckingForUpdate = false; }
        }

        [RelayCommand]
        public async Task DownloadUpdate()
        {
            if (!CanSelfUpdate || string.IsNullOrWhiteSpace(UpdateInfo.DownloadUrl) || IsInstallingUpdate) return;

            // The card owns the download, its progress bar and its failure state, and
            // it is the same card the automatic check raises. Handing off keeps one
            // update experience rather than two that behave differently.
            if (System.Windows.Application.Current?.MainWindow is Views.Windows.MainWindow window)
            {
                window.ShowApplicationUpdate(UpdateInfo);
                return;
            }

            // No window to draw the card in - the About page cannot be reached without
            // one, but the fallback keeps the button working rather than dead.
            IsInstallingUpdate = true;
            UpdateStatus = "Verifying and installing the update";
            try
            {
                if (!await _updateService.DownloadAndInstallUpdateAsync())
                    UpdateStatus = "Could not install the update. Arsenal was not changed";
            }
            catch (Exception ex)
            {
                UpdateStatus = "Could not install the update. Arsenal was not changed";
                Logger.WriteLine("About update install: " + ex.Message);
            }
            finally { IsInstallingUpdate = false; }
        }

        /// <summary>
        /// Prefers the informational version, which carries the pre-release label the
        /// assembly version cannot hold - "1.0.0-beta.1" rather than "1.0.0.0".
        /// </summary>
        private static string ReadDisplayVersion()
        {
            return "v" + ReleaseVersion.CurrentDisplayString();
        }

        private async void LoadSpecifications()
        {
            SystemSpecSheet specs = await Task.Run(SystemSpecs.Get);

            SpecModel = specs.Model ?? string.Empty;
            SpecBios = specs.Bios ?? string.Empty;
            SpecProcessor = specs.Processor ?? string.Empty;
            SpecMemory = specs.Memory ?? string.Empty;
            SpecGraphics = specs.Graphics ?? string.Empty;
            SpecDisplay = specs.Display ?? string.Empty;
            SpecStorage = specs.Storage ?? string.Empty;
            SpecOperatingSystem = specs.OperatingSystem ?? string.Empty;

            IsLoadingSpecifications = false;
        }
    }
}
