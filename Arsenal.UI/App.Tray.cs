using Arsenal.Application.Models;
using Arsenal.Application.Services.Contracts;
using Arsenal.AutoUpdate;
using Arsenal.Battery;
using Arsenal.Gpu;
using Arsenal.Helpers;
using Arsenal.Peripherals;
using Arsenal.UI.ViewModels;
using Arsenal.UI.Services;
using Arsenal.UI.Services.Remote;
using Arsenal.UI.Views.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace Arsenal.UI
{
    public partial class App
    {
        private DateTime _trayLeftPressAt = DateTime.MinValue;
        private DateTime _lastTrayLeftClickAt = DateTime.MinValue;
        private static readonly TimeSpan TrayLeftPressWindow = TimeSpan.FromMilliseconds(700);
        private static readonly TimeSpan TrayLeftClickDebounce = TimeSpan.FromMilliseconds(320);

        private void InitTrayIcon()
        {
            TrayIcon = new Forms.NotifyIcon
            {
                Text = "Arsenal",
                Visible = true
            };

            try
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "arsenal.ico");
                if (File.Exists(iconPath))
                    TrayIcon.Icon = new Icon(iconPath);
                else
                    TrayIcon.Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty) ?? SystemIcons.Application;
            }
            catch
            {
                TrayIcon.Icon = SystemIcons.Application;
            }

            UpdateTrayTooltip();

            TrayIcon.MouseDown += (s, e) =>
            {
                if (e.Button == Forms.MouseButtons.Left)
                    _trayLeftPressAt = DateTime.UtcNow;
            };

            TrayIcon.MouseClick += (s, e) =>
            {
                if (e.Button == Forms.MouseButtons.Left)
                {
                    Dispatcher.Invoke(HandleTrayLeftClick);
                }
                else if (e.Button == Forms.MouseButtons.Right)
                {
                    Dispatcher.Invoke(ToggleTrayMenu);
                }
            };
        }

        private void HandleTrayLeftClick()
        {
            DateTime now = DateTime.UtcNow;
            if (now - _lastTrayLeftClickAt < TrayLeftClickDebounce) return;
            _lastTrayLeftClickAt = now;

            // The press that produced this click may already have closed the panel by
            // taking activation from it. Toggling would then read the panel as closed
            // and open it again, so a click that owns the close in flight is spent on it.
            if (_quickPanelWindow?.TryClaimTrayPressDismissal(_trayLeftPressAt) == true) return;

            ToggleQuickPanel();
        }

        internal bool IsTrayLeftPressActive
            => DateTime.UtcNow - _trayLeftPressAt <= TrayLeftPressWindow;

        /// <summary>
        /// Puts the charge limit on the tray icon's tooltip. Windows owns its own
        /// battery icon and gives no way for an application to annotate it, so this and
        /// the marker on the Battery page are where the limit is visible.
        /// </summary>
        private static void UpdateTrayTooltip()
        {
            if (TrayIcon is null) return;

            int limit = BatteryControl.chargeFull ? 100 : AppConfig.Get("charge_limit", 100);
            TrayIcon.Text = limit >= 100
                ? "Arsenal"
                : $"Arsenal: charging stops at {limit}%";
        }
    }
}
