using Arsenal.Display;
using Arsenal.Mode;
using Microsoft.Win32;

namespace Arsenal.Helpers
{
    public class ClamshellModeControl
    {
        private const double DisplaySettleDelayMilliseconds = 750;
        private const int MaxDisplaySettleAttempts = 4;

        private readonly System.Timers.Timer lidSettleTimer = new() { AutoReset = false };
        private readonly System.Timers.Timer displaySettleTimer = new()
        {
            AutoReset = false,
            Interval = DisplaySettleDelayMilliseconds
        };
        private int displaySettleAttempt;

        public ClamshellModeControl()
        {
            //Save current setting if hibernate or shutdown to prevent reverting the user set option.
            CheckAndSaveLidAction();
            lidSettleTimer.Elapsed += OnLidSettled;
            displaySettleTimer.Elapsed += OnDisplaySettled;
        }


        public bool IsClamshellEnabled()
        {
            return AppConfig.Is("toggle_clamshell_mode");
        }

        public bool IsChargerConnected()
        {
            return SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;
        }

        public bool IsClamshellReady()
        {
            return ScreenNative.IsExternalDisplayConnected(true) && (IsChargerConnected() || AppConfig.Is("clamshell_battery"));
        }

        public void ToggleLidAction()
        {
            if (!IsClamshellEnabled())
            {
                return;
            }

            if (IsClamshellReady())
            {
                EnableClamshellMode();
            }
            else
            {
                DisableClamshellMode();
            }
        }

        public void ScheduleLidToggle()
        {
            if (!IsClamshellEnabled()) return;
            lidSettleTimer.Interval = Math.Max(AppConfig.Get("clamshell_delay"), 2000);
            lidSettleTimer.Stop();
            lidSettleTimer.Start();
        }

        private void OnLidSettled(object? sender, System.Timers.ElapsedEventArgs e)
        {
            ToggleLidAction();
        }
        public static void DisableClamshellMode()
        {
            if (PowerNative.GetLidAction(true) == GetDefaultLidAction()) return;
            PowerNative.SetLidAction(GetDefaultLidAction(), true);
            Logger.WriteLine("Disengaging Clamshell Mode");
        }

        public static void EnableClamshellMode()
        {
            if (PowerNative.GetLidAction(true) == 0) return;
            PowerNative.SetLidAction(0, true);
            Logger.WriteLine("Engaging Clamshell Mode");
        }

        public void UnregisterDisplayEvents()
        {
            SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
            lidSettleTimer.Stop();
            displaySettleTimer.Stop();
        }

        public void RegisterDisplayEvents()
        {
            SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
        }

        private void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e)
        {
            Logger.WriteLine("Display configuration changed.");

            if (IsClamshellEnabled())
                ScheduleLidToggle();

            // DisplaySettingsChanged is raised while Windows is still rebuilding the
            // topology. Querying immediately can return -1 for the internal panel and
            // leave that sentinel on screen until another event happens. Collapse the
            // event burst, then give slower dock transitions a few bounded retries.
            Interlocked.Exchange(ref displaySettleAttempt, 0);
            displaySettleTimer.Stop();
            displaySettleTimer.Start();
        }

        private void OnDisplaySettled(object? sender, System.Timers.ElapsedEventArgs e)
        {
            string? laptopScreen = ScreenNative.FindLaptopScreen();
            bool panelReady = ScreenNative.GetRefreshRate(laptopScreen) > 0;
            int attempt = Interlocked.Increment(ref displaySettleAttempt);
            if (!panelReady && attempt < MaxDisplaySettleAttempts)
            {
                displaySettleTimer.Start();
                return;
            }

            if (AppConfig.Is("screen_force"))
                ScreenControl.AutoScreen();
            else
                ScreenControl.InitScreen();

            if (AppConfig.IsForceMiniled())
                ScreenControl.InitMiniled();
        }

        private static int CheckAndSaveLidAction()
        {
            if (AppConfig.Get("clamshell_default_lid_action", -1) != -1)
            {
                //Seting was alredy set. Do not touch it
                return AppConfig.Get("clamshell_default_lid_action", -1);
            }

            try
            {
                int val = PowerNative.GetLidAction(true);
                //If it is 0 then it is likely already set by clamshell mdoe
                //If 0 was set by the user, then why do they even use clamshell mode?
                //We only care about hibernate or shutdown setting here
                if (val == 2 || val == 3)
                {
                    AppConfig.Set("clamshell_default_lid_action", val);
                    return val;
                }
            } catch (Exception ex)
            {
                Logger.WriteLine("Can't get Lid Action: " + ex.ToString());
            }

            return 1;
        }

        //Power users can change that setting.
        //0 = Do nothing
        //1 = Sleep (default)
        //2 = Hibernate
        //3 = Shutdown
        private static int GetDefaultLidAction()
        {
            int val = AppConfig.Get("clamshell_default_lid_action", 1);

            if (val < 0 || val > 3)
            {
                val = 1;
            }

            return val;
        }
    }
}
