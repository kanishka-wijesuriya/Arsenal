using Arsenal.Application.Models;
using Arsenal.Application.Services.Contracts;
using Arsenal.Battery;
using Arsenal.Display;
using Arsenal.Gpu;
using Arsenal.Helpers;
using Arsenal.Peripherals;
using Arsenal.UI.ViewModels;
using Arsenal.UI.Services;
using Arsenal.UI.Services.Remote;
using Arsenal.UI.Views.Windows;
using Microsoft.Extensions.DependencyInjection;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace Arsenal.UI
{
    public partial class App
    {
        private MainWindow? _mainWindow;
        private QuickPanelWindow? _quickPanelWindow;
        private TrayMenuWindow? _trayMenuWindow;

        private void HandleHardwareButtonPress()
        {
            ToggleQuickPanel();
        }

        public void ShowMainWindow()
        {
            MainWindow window = EnsureMainWindow();
            window.Show();
            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;
            window.Activate();
            window.Focus();
        }

        public void ToggleMainWindow()
        {
            MainWindow window = EnsureMainWindow();
            if (window.IsVisible)
            {
                window.Hide();
            }
            else
            {
                ShowMainWindow();
            }
        }

        public void ToggleQuickPanel()
        {
            QuickPanelWindow quickPanel = EnsureQuickPanelWindow();
            if (_trayMenuWindow?.IsVisible == true) _trayMenuWindow.Hide();

            // IsVisible stays true for the whole exit animation, so testing it made a
            // press during the close re-run the close instead of reopening - the panel
            // appeared to ignore the tray until the animation had finished. IsOpenOrOpening
            // asks where the panel is headed rather than where it currently is, which is
            // the question a toggle actually has.
            if (quickPanel.IsOpenOrOpening)
            {
                quickPanel.HideAnimated();
            }
            else
            {
                quickPanel.ShowAnimated();
            }
        }

        public void ToggleTrayMenu()
        {
            TrayMenuWindow trayMenu = EnsureTrayMenuWindow();
            if (_quickPanelWindow?.IsVisible == true) _quickPanelWindow.Hide();
            if (trayMenu.IsOpenOrOpening) trayMenu.HideAnimated();
            else trayMenu.ShowAnimated();
        }

        private MainWindow EnsureMainWindow()
        {
            if (_mainWindow is not null) return _mainWindow;
            _mainWindow = Services.GetRequiredService<MainWindow>();
            MainWindow = _mainWindow;
            _mainWindow.IsVisibleChanged += OnTelemetrySurfaceVisibilityChanged;
            _mainWindow.Closed += OnMainWindowClosed;
            return _mainWindow;
        }

        private void OnMainWindowClosed(object? sender, EventArgs e)
        {
            if (sender is not MainWindow window) return;
            window.IsVisibleChanged -= OnTelemetrySurfaceVisibilityChanged;
            window.Closed -= OnMainWindowClosed;
            if (!ReferenceEquals(_mainWindow, window)) return;

            _mainWindow = null;
            if (ReferenceEquals(MainWindow, window)) MainWindow = null;
        }

        /// <summary>
        /// Closes the native Main Window after a long tray idle. The view model and the
        /// navigation state remain in the resident shell, so reopening creates the same
        /// page and state without retaining the hidden HWND and its composition target.
        /// </summary>
        internal bool ReleaseHiddenMainWindow()
        {
            MainWindow? window = _mainWindow;
            if (window is null || !window.CanReleaseHiddenWindow) return false;

            window.ReleaseOwnedContent();
            window.AllowClose();
            window.Close();
            return true;
        }

        internal bool HasVisibleOwnedWindow =>
            _mainWindow?.IsVisible == true ||
            _quickPanelWindow?.IsVisible == true ||
            _trayMenuWindow?.IsVisible == true;

        internal void ReleaseHiddenMainWindowPageContent()
        {
            if (_mainWindow?.IsVisible == false)
                _mainWindow.ReleaseHiddenPageContent();
        }

        private QuickPanelWindow EnsureQuickPanelWindow()
        {
            if (_quickPanelWindow is not null) return _quickPanelWindow;
            _quickPanelWindow = Services.GetRequiredService<QuickPanelWindow>();
            _quickPanelWindow.IsVisibleChanged += OnTelemetrySurfaceVisibilityChanged;
            return _quickPanelWindow;
        }

        private TrayMenuWindow EnsureTrayMenuWindow()
            => _trayMenuWindow ??= Services.GetRequiredService<TrayMenuWindow>();

        private void OnTelemetrySurfaceVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            bool surfaceVisible = _mainWindow?.IsVisible == true || _quickPanelWindow?.IsVisible == true;

            IDeviceStateService? telemetry = Services.GetService<IDeviceStateService>();
            if (telemetry is not null)
            {
                if (surfaceVisible) telemetry.ResumePolling();
                else telemetry.PausePolling();
            }

            // A release found while the window was hidden has been waiting for a
            // window to be drawn in.
            if (_mainWindow?.IsVisible == true) ShowPendingUpdateIfAny();
        }
    }
}
