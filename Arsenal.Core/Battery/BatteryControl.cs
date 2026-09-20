using Arsenal.Helpers;
using Microsoft.Win32;
using System.Diagnostics;

namespace Arsenal.Battery
{
    public static class BatteryControl
    {
        public static event Action<int>? OnBatteryLimitChanged;
        public static event Action<bool>? OnBatteryFullChanged;

        static bool _chargeFull = AppConfig.Is("charge_full");
        public static bool chargeFull
        {
            get => _chargeFull;
            set
            {
                AppConfig.Set("charge_full", value ? 1 : 0);
                _chargeFull = value;
                OnBatteryFullChanged?.Invoke(value);
            }
        }

        public static void ToggleBatteryLimitFull()
        {
            if (chargeFull) SetBatteryChargeLimit();
            else SetBatteryLimitFull();
        }

        public static void SetBatteryLimitFull()
        {
            chargeFull = true;
            // Keep the reported rate in step with the firmware limit, otherwise Windows
            // keeps showing smart charging while the battery is actually charging to
            // full - or, as happens more often, a stale 100 here hides the indicator
            // when a limit really is in force.
            SetAsusChargeLimit(100);
            Program.acpi.DeviceSet(AsusACPI.BatteryLimit, 100, "BatteryLimit");
            OnBatteryFullChanged?.Invoke(true);
            Program.Bridge?.VisualiseBatteryFull();
        }

        public static void UnSetBatteryLimitFull()
        {
            chargeFull = false;
            Logger.WriteLine("Battery fully charged");
            OnBatteryFullChanged?.Invoke(false);
            Program.Bridge?.VisualiseBatteryFull();
        }

        public static void AutoBattery(bool init = false)
        {
            if (chargeFull && !init) SetBatteryLimitFull();
            else SetBatteryChargeLimit();
        }

        public static void SetAsusChargeLimit(int value)
        {
            // This registry value is what the ASUS System Control Interface reports to
            // Windows, and it is what puts the smart-charging shield on the battery
            // icon. Writing under HKLM needs elevation, so without it the limit still
            // applies at the firmware level but Windows never shows the indicator.
            if (!ProcessHelper.IsUserAdministrator())
            {
                Logger.WriteLine($"ChargingRate = {value} : skipped, needs administrator (battery icon will not show smart charging)");
                return;
            }
            const string keyPath = @"SOFTWARE\ASUS\ASUS System Control Interface\AsusOptimization\ASUS Keyboard Hotkeys";
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: true);
                key?.SetValue("ChargingRate", value, RegistryValueKind.DWord);
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"Failed to set ChargingRate: {ex.Message}");
            }
        }

        public static void SetBatteryChargeLimit(int setLimit = -1)
        {
            int limit = setLimit;
            if (limit < 0) limit = AppConfig.Get("charge_limit");
            if (limit < 40 || limit > 100) return;

            if (AppConfig.IsChargeLimit6080())
            {
                if (limit > 85) limit = 100;
                else if (limit >= 80) limit = 80;
                else if (limit < 60) limit = 60;
            }

            // Upstream only syncs this when an explicit limit is passed, so the restore
            // path at startup leaves whatever was there last. That is how the value
            // drifts out of step with the firmware and the battery icon stops showing
            // smart charging. Write it every time so the two cannot disagree.
            SetAsusChargeLimit(limit);

            Program.acpi.DeviceSet(AsusACPI.BatteryLimit, limit, "BatteryLimit");

            AppConfig.Set("charge_limit", limit);
            chargeFull = false;

            OnBatteryLimitChanged?.Invoke(limit);
            Program.Bridge?.VisualiseBattery(limit);
        }

        /// <summary>
        /// Writes the Windows battery report and opens the page Windows produces.
        /// </summary>
        /// <remarks>
        /// The in-app summary reads the same scan through
        /// <see cref="BatteryReportReader"/>; this remains for the places that want the
        /// whole document - the command palette, and a paired phone asking for it, where
        /// there is no page to show a summary on.
        ///
        /// <para>It no longer runs through PowerShell. The old call handed
        /// <c>powercfg /batteryreport; explorer battery-report.html</c> to a shell with
        /// the working directory set to the user's profile, which dropped the report in
        /// the middle of their home folder and depended on that path surviving a second
        /// parser. Both files live in Arsenal's own folder now.</para>
        /// </remarks>
        public static void BatteryReport()
        {
            _ = Task.Run(async () =>
            {
                BatteryReportData? report = await BatteryReportReader.GenerateAsync().ConfigureAwait(false);
                if (report is null || report.HtmlPath.Length == 0) return;

                try
                {
                    Process.Start(new ProcessStartInfo(report.HtmlPath) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Logger.WriteLine(ex.Message);
                }
            });
        }
    }
}
