using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Arsenal.Application.Models;
using Arsenal.Application.Services.Contracts;
using Arsenal.AutoUpdate;
using Arsenal.Helpers;
using Arsenal.UI.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace Arsenal.UI.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        private bool _isReady;

        public bool CanSelfUpdate => App.CanSelfUpdate;

        [ObservableProperty]
        private bool _runOnStartup = false;

        [ObservableProperty]
        private bool _startMinimized;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(AdministratorDescription))]
        private bool _runAsAdministrator;

        [ObservableProperty]
        private bool _minimizeToTray = true;

        [ObservableProperty]
        private int _selectedTheme = 0; // 0=System, 1=Dark, 2=Light

        /// <summary>Whether a settings section is a place you open or a card already open.</summary>
        [ObservableProperty]
        private bool _useSubpages = Controls.SettingsGroup.SubpagesEnabled;

        /// <summary>Draw on the processor rather than the graphics card.</summary>
        [ObservableProperty]
        private bool _softwareRendering = AppConfig.Is("software_render");

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsCustomAccent))]
        [NotifyPropertyChangedFor(nameof(AccentDescription))]
        private int _selectedAccentSource; // 0=Windows, 1=Custom

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CustomAccentBrush))]
        private System.Windows.Media.Color _customAccentColor;

        [ObservableProperty]
        private string _customAccentHex = string.Empty;

        [ObservableProperty]
        private int _selectedLanguage = 0;

        [ObservableProperty]
        private string _selectedLanguageCode = string.Empty;

        [ObservableProperty]
        private bool _checkUpdatesOnStartup = true;

        [ObservableProperty] private bool _toastEnabled = true;
        [ObservableProperty] private int _toastStyle;
        [ObservableProperty] private int _toastPosition;
        [ObservableProperty] private int _toastDurationSeconds = 4;
        [ObservableProperty] private bool _toastProgress = true;

        public IReadOnlyList<string> ToastStyles { get; } = new[] { "Fluent", "Compact", "Accent" };
        public IReadOnlyList<string> ToastPositions { get; } = new[]
        {
            "Top right",
            "Bottom right",
            "Top center",
            "Bottom center"
        };

        public bool IsCustomAccent => SelectedAccentSource == 1;

        public string AccentDescription => IsCustomAccent
            ? "Overrides the Windows colour across Arsenal and applies immediately."
            : "Follows the accent colour selected in Windows Personalisation.";

        public System.Windows.Media.SolidColorBrush CustomAccentBrush
        {
            get
            {
                var brush = new System.Windows.Media.SolidColorBrush(CustomAccentColor);
                brush.Freeze();
                return brush;
            }
        }


        public SettingsViewModel()
        {
            RunAsAdministrator = AppConfig.Is("run_as_admin");
            RunOnStartup = Startup.IsScheduled();
            StartMinimized = AppConfig.Is(ApplicationLaunch.StartMinimizedSetting);
            MinimizeToTray = AppConfig.IsNotFalse("minimize_to_tray");
            SelectedTheme = AppConfig.Get("theme", 0);
            SelectedAccentSource = Math.Clamp(AppConfig.Get(AccentColorService.SourceSetting, 0), 0, 1);
            CustomAccentColor = AccentColorService.GetCustomAccent();
            CustomAccentHex = AccentColorService.ToHex(CustomAccentColor);
            CheckUpdatesOnStartup = CanSelfUpdate && AppConfig.IsNotFalse("check_updates");
            ToastEnabled = AppConfig.IsNotFalse("toast_enabled");
            ToastStyle = Math.Clamp(AppConfig.Get("toast_style", 0), 0, 2);
            ToastPosition = Math.Clamp(AppConfig.Get("toast_position", 0), 0, 3);
            ToastDurationSeconds = Math.Clamp((int)Math.Round(AppConfig.Get("toast_duration", 3500) / 1000d), 2, 12);
            ToastProgress = AppConfig.IsNotFalse("toast_progress");
            _isReady = true;
        }

        [RelayCommand]
        public void ToggleStartup()
        {
            if (RunOnStartup)
            {
                Startup.Schedule();
            }
            else
            {
                Startup.UnSchedule();
            }
        }

        [RelayCommand]
        public void SavePreferences()
        {
            AppConfig.Set(ApplicationLaunch.StartMinimizedSetting, StartMinimized ? 1 : 0);
            AppConfig.Set("minimize_to_tray", MinimizeToTray ? 1 : 0);
            AppConfig.Set("theme", SelectedTheme);
            AppConfig.Set("check_updates", CanSelfUpdate && CheckUpdatesOnStartup ? 1 : 0);

            Arsenal.UI.App.ApplyConfiguredTheme();
        }

        [RelayCommand]
        public void SetTheme(object? themeParam)
        {
            SelectedTheme = themeParam is int value ? value : int.TryParse(themeParam?.ToString(), out int parsed) ? parsed : 0;
            SavePreferences();
        }

        [RelayCommand]
        public void SetAccentSource(object? sourceParam)
        {
            SelectedAccentSource = sourceParam is int value
                ? Math.Clamp(value, 0, 1)
                : int.TryParse(sourceParam?.ToString(), out int parsed) ? Math.Clamp(parsed, 0, 1) : 0;

            AppConfig.Set(AccentColorService.SourceSetting, SelectedAccentSource);
            if (IsCustomAccent)
                AppConfig.Set(AccentColorService.ColorSetting, AccentColorService.ToHex(CustomAccentColor));
            Arsenal.UI.App.ApplyConfiguredAccent();
        }

        [RelayCommand]
        public void ApplyCustomAccentHex()
        {
            if (AccentColorService.TryParse(CustomAccentHex, out System.Windows.Media.Color color))
            {
                CustomAccentColor = color;
                CustomAccentHex = AccentColorService.ToHex(color);
                AppConfig.Set(AccentColorService.ColorSetting, CustomAccentHex);
            }
            else
            {
                CustomAccentHex = AccentColorService.ToHex(CustomAccentColor);
            }
        }

        [RelayCommand]
        public void TestToast() => ToastManager.Show(
            "Notifications are ready",
            ToastIcon.Charger,
            "This is how they will look");

        /// <summary>
        /// Several features write under HKLM and are silently skipped without
        /// elevation: the ASUS ChargingRate value behind the battery icon's
        /// smart-charging shield, the charge-limit scheduled task, and the panel
        /// refresh flag.
        /// </summary>
        public bool IsElevated { get; } = Arsenal.Helpers.ProcessHelper.IsUserAdministrator();

        public bool NeedsElevation => !IsElevated;

        public string AdministratorDescription => RunAsAdministrator
            ? IsElevated
                ? "Enabled. This session is running with administrator rights; turn it off to make future launches standard."
                : "Enabled. Windows will request administrator approval for this and future manual launches."
            : "Disabled. Arsenal opens normally and requests elevation only for actions that need it.";

        [RelayCommand]
        public void RunSetup() => (System.Windows.Application.Current as App)?.ShowSetup();

        partial void OnRunOnStartupChanged(bool value)
        {
            if (!_isReady) return;
            if (value) Startup.Schedule(); else Startup.UnSchedule();
        }

        partial void OnRunAsAdministratorChanged(bool value)
        {
            if (!_isReady) return;

            AppConfig.Set("run_as_admin", value ? 1 : 0);
            if (value && !IsElevated)
            {
                AppConfig.Flush();
                if (ProcessHelper.RunAsAdmin("--settings")) return;

                AppConfig.Set("run_as_admin", 0);
                _isReady = false;
                RunAsAdministrator = false;
                _isReady = true;
                return;
            }

            // Keep an existing sign-in task's privilege level synchronized with the
            // preference. The current elevated process stays elevated until it exits.
            if (RunOnStartup)
            {
                Startup.UnSchedule();
                Startup.Schedule();
            }
        }

        partial void OnMinimizeToTrayChanged(bool value) { if (_isReady) SavePreferences(); }
        partial void OnStartMinimizedChanged(bool value) { if (_isReady) SavePreferences(); }

        /// <summary>
        /// Applied to every group that is on screen, at once.
        /// </summary>
        /// <remarks>
        /// No restart and no page rebuild: the control takes the setting itself and the
        /// live groups re-dress where they stand, including the ones on this very page
        /// underneath the switch being flipped.
        /// </remarks>
        partial void OnUseSubpagesChanged(bool value)
        {
            if (!_isReady) return;
            Controls.SettingsGroup.SetSubpagesEnabled(value);
        }

        /// <summary>
        /// Takes effect on the next start, and says so rather than pretending.
        /// </summary>
        /// <remarks>
        /// WPF fixes its render mode when it composes its first window, so nothing can
        /// change it while one is on screen.
        /// </remarks>
        partial void OnSoftwareRenderingChanged(bool value)
        {
            if (!_isReady) return;
            AppConfig.Set("software_render", value ? 1 : 0);
        }

        partial void OnCheckUpdatesOnStartupChanged(bool value) { if (_isReady) SavePreferences(); }
        partial void OnCustomAccentColorChanged(System.Windows.Media.Color value)
        {
            CustomAccentHex = AccentColorService.ToHex(value);
            if (!_isReady) return;
            AppConfig.Set(AccentColorService.ColorSetting, CustomAccentHex);
            if (IsCustomAccent) Arsenal.UI.App.ApplyConfiguredAccent();
        }
        partial void OnToastEnabledChanged(bool value) { if (_isReady) SaveToastPreferences(); }
        partial void OnToastStyleChanged(int value) { if (_isReady) SaveToastPreferences(); }
        partial void OnToastPositionChanged(int value) { if (_isReady) SaveToastPreferences(); }
        partial void OnToastDurationSecondsChanged(int value) { if (_isReady) SaveToastPreferences(); }
        partial void OnToastProgressChanged(bool value) { if (_isReady) SaveToastPreferences(); }

        private void SaveToastPreferences()
        {
            AppConfig.Set("toast_enabled", ToastEnabled ? 1 : 0);
            AppConfig.Set("toast_style", Math.Clamp(ToastStyle, 0, 2));
            AppConfig.Set("toast_position", Math.Clamp(ToastPosition, 0, 3));
            AppConfig.Set("toast_duration", Math.Clamp(ToastDurationSeconds, 2, 12) * 1000);
            AppConfig.Set("toast_progress", ToastProgress ? 1 : 0);
        }
    }
}
