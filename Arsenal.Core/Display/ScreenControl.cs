using Arsenal.Helpers;
using Microsoft.Win32;
using System.Diagnostics;

namespace Arsenal.Display
{
    public static class ScreenControl
    {

        public const int MAX_REFRESH = 1000;
        public static int MIN_RATE = AppConfig.Get("min_rate", 60);
        public static int MAX_RATE = AppConfig.Get("max_rate");

        public static int GetMaxRate(string? laptopScreen)
        {
            if (MAX_RATE > 0) return MAX_RATE;
            else return ScreenNative.GetMaxRefreshRate(laptopScreen);
        }

        public static void AutoScreen(bool force = false)
        {
            if (force || AppConfig.Is("screen_auto"))
            {
                if (SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online)
                    SetScreen(MAX_REFRESH, 1);
                else
                    SetScreen(MIN_RATE, 0);
            }
            else
            {
                SetScreen(overdrive: AppConfig.Get("overdrive"));
            }
        }

        public static void SetAutoRefresh(int auto)
        {
            AppConfig.Set("screen_auto", auto);
            if (auto == 0) SetAsusRefreshFlag(0);
        }

        public static void SetAsusRefreshFlag(int value)
        {
            if (!ProcessHelper.IsUserAdministrator()) return;
            const string keyPath = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{8714A8D1-0F08-4681-9DF6-A8C4607A58B4}";
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: true);
                key?.SetValue("RefreshFlag", value, RegistryValueKind.DWord);
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"Failed to set RefreshFlag: {ex.Message}");
            }
        }

        public static void ToggleScreenRate()
        {
            // FindLaptopScreen returns null when there is no internal panel to find - an
            // external-only setup, or a display stack that has not finished enumerating
            // after a dock or sleep. GetRefreshRate answers -1 for null, so the guard
            // below already covered it, but only by way of an invariant in another file.
            // Checking here says it out loud and survives either method changing.
            var laptopScreen = ScreenNative.FindLaptopScreen(true);
            if (laptopScreen is null) return;

            var refreshRate = ScreenNative.GetRefreshRate(laptopScreen);
            if (refreshRate < 0) return;

            ScreenNative.SetRefreshRate(laptopScreen, refreshRate > MIN_RATE ? MIN_RATE : GetMaxRate(laptopScreen));
            InitScreen();
        }


        public static void SetScreen(int frequency = -1, int overdrive = -1, int miniled = -1)
        {
            var laptopScreen = ScreenNative.FindLaptopScreen(true);
            if (laptopScreen is null) return;

            var refreshRate = ScreenNative.GetRefreshRate(laptopScreen);

            if (refreshRate < 0) return;

            if (frequency >= MAX_REFRESH)
            {
                frequency = GetMaxRate(laptopScreen);
            }

            if (frequency > 0 && frequency != refreshRate)
            {
                ScreenNative.SetRefreshRate(laptopScreen, frequency);
            }

            if (overdrive >= 0)
            {
                if (!Program.acpi.IsOverdriveSupported())
                {
                    Logger.WriteLine($"ScreenOverdrive = {overdrive} : skipped, panel reports no overdrive");
                }
                else
                {
                    if (AppConfig.IsNoOverdrive()) overdrive = 0;

                    // Normally the write is skipped when the register already holds the
                    // requested value. Under the manual override that skip would hide
                    // the one thing being tested, so always write and let the panel's
                    // own result reach the log.
                    if (AppConfig.Is("force_overdrive") || overdrive != Program.acpi.DeviceGet(AsusACPI.ScreenOverdrive))
                    {
                        Program.acpi.DeviceSet(AsusACPI.ScreenOverdrive, overdrive, "ScreenOverdrive");
                    }
                }
            }

            SetMiniled(miniled);

            InitScreen();
        }

        public static void SetMiniled(int miniled = -1)
        {
            if (miniled >= 0)
            {
                if (Program.acpi.IsSupported(AsusACPI.ScreenMiniled1))
                    Program.acpi.DeviceSet(AsusACPI.ScreenMiniled1, miniled, "Miniled1");
                else
                {
                    Program.acpi.DeviceSet(AsusACPI.ScreenMiniled2, miniled, "Miniled2");
                    Thread.Sleep(100);
                }
            }
        }

        public static void InitMiniled()
        {
            if (AppConfig.IsForceMiniled())
            {
                if (ScreenCCD.IsHDR()) SetHDRControl(AppConfig.Get("hdr_control"));
                else SetMiniled(AppConfig.Get("miniled"));
            }
        }

        public static void InitOptimalBrightness()
        {
            int optimalBrightness = AppConfig.Get("optimal_brightness");
            if (optimalBrightness >= 0) SetOptimalBrightness(optimalBrightness);
        }

        public static void SetOptimalBrightness(int status)
        {
            AppConfig.Set("optimal_brightness", status);
            if (status == 2) status = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline ? 1 : 0;
            Program.acpi.DeviceSet(AsusACPI.ScreenOptimalBrightness, status, "Optimal Brightness");
        }

        public static int GetOptimalBrightness()
        {
            return Program.acpi.DeviceGet(AsusACPI.ScreenOptimalBrightness);
        }

        public static void ToogleFHD()
        {
            int fhd = Program.acpi.DeviceGet(AsusACPI.ScreenFHD);
            Logger.WriteLine($"FHD Toggle: {fhd}");

            DialogResult dialogResult = MessageBox.Show("Changing display mode requires reboot", "Reboot now?", MessageBoxButtons.YesNo);
            if (dialogResult == DialogResult.Yes)
            {
                Program.acpi.DeviceSet(AsusACPI.ScreenFHD, (fhd == 1) ? 0 : 1, "FHD");
                Process.Start("shutdown", "/r /t 1");
            }
        }

        public static void SetHDRControl(int status = -1)
        {
            if (status >= 0)
            {
                AppConfig.Set("hdr_control", status);
                Program.acpi.DeviceSet(AsusACPI.ScreenHDRControl, status, "HDR Control");
            }
        }

        public static void ToogleHDRControl()
        {
            int hdrControl = Program.acpi.DeviceGet(AsusACPI.ScreenHDRControl);
            Logger.WriteLine($"HDR Control Toggle: {hdrControl}");
            SetHDRControl((hdrControl == 1) ? 1 : 0);
            Thread.Sleep(200);
            InitScreen();
        }

        public static string ToogleMiniled()
        {
            int miniled1 = Program.acpi.DeviceGet(AsusACPI.ScreenMiniled1);
            int miniled2 = Program.acpi.DeviceGet(AsusACPI.ScreenMiniled2);

            Logger.WriteLine($"MiniledToggle: {miniled1} {miniled2}");

            int miniled;
            string name;

            if (miniled1 >= 0)
            {
                switch (miniled1)
                {
                    case 1: 
                        miniled = 0;
                        name = "One Zone";
                        break;
                    default:
                        miniled = 1;
                        name = "Multi Zone";
                        break;
                }
            }
            else
            {
                switch (miniled2)
                {
                    case 1: 
                        miniled = 2;
                        name = "One Zone";
                        break;
                    case 2: 
                        miniled = 0;
                        name = "Multi Zone";
                        break;
                    default: 
                        miniled = 1;
                        name = "Multi Zone Strong";
                        break;
                }
            }

            AppConfig.Set("miniled", miniled);
            SetScreen(miniled: miniled);
            
            return name;
        }

        public static void InitScreen()
        {
            var laptopScreen = ScreenNative.FindLaptopScreen();
            int frequency = ScreenNative.GetRefreshRate(laptopScreen);
            int maxFrequency = GetMaxRate(laptopScreen);

            if (maxFrequency > 0) AppConfig.Set("max_frequency", maxFrequency);
            else maxFrequency = AppConfig.Get("max_frequency");

            bool screenAuto = AppConfig.Is("screen_auto");
            bool overdriveSetting = Program.acpi.IsOverdriveSupported() && !AppConfig.IsNoOverdrive();

            int overdrive = overdriveSetting ? Program.acpi.DeviceGet(AsusACPI.ScreenOverdrive) : 0;

            int miniled1 = Program.acpi.DeviceGet(AsusACPI.ScreenMiniled1);
            int miniled2 = Program.acpi.DeviceGet(AsusACPI.ScreenMiniled2);

            int miniled = (miniled1 >= 0) ? miniled1 : miniled2;
            bool hdr = false;
            bool acm = false;

            if (miniled >= 0)
            {
                Logger.WriteLine($"Miniled: {miniled1} {miniled2}");
                AppConfig.Set("miniled", miniled);
            }

            try
            {
                hdr = ScreenCCD.GetHDRStatus(out acm);
            } catch (Exception ex)
            {
                Logger.WriteLine(ex.Message);
            }

            bool screenEnabled = (frequency >= 0);

            int fhd = -1;
            if (AppConfig.IsDUO())
            {
                fhd = Program.acpi.DeviceGet(AsusACPI.ScreenFHD);
            }

            int hdrControl = Program.acpi.DeviceGet(AsusACPI.ScreenHDRControl);
            if (hdrControl >= 0) Logger.WriteLine($"HDR Control Status: {hdrControl}");

            // Keep the cached settings aligned with the values read back from the
            // panel firmware. Without this, the UI can continue to report a stale
            // overdrive-on state after disabling it and the next enable action uses
            // the wrong cached refresh/OD combination.
            AppConfig.Set("frequency", frequency);
            AppConfig.Set("overdrive", overdrive);

            OnScreenVisualise?.Invoke(new ScreenStatusSnapshot(
                ScreenEnabled: screenEnabled,
                ScreenAuto: screenAuto,
                Frequency: frequency,
                MaxFrequency: maxFrequency,
                Overdrive: overdrive,
                OverdriveSetting: overdriveSetting,
                Miniled1: miniled1,
                Miniled2: miniled2,
                Hdr: hdr,
                Acm: acm,
                Fhd: fhd,
                HdrControl: hdrControl
            ));
        }

        public static event Action<ScreenStatusSnapshot>? OnScreenVisualise;
    }

    public record ScreenStatusSnapshot(
        bool ScreenEnabled,
        bool ScreenAuto,
        int Frequency,
        int MaxFrequency,
        int Overdrive,
        bool OverdriveSetting,
        int Miniled1,
        int Miniled2,
        bool Hdr,
        bool Acm,
        int Fhd,
        int HdrControl);
}
