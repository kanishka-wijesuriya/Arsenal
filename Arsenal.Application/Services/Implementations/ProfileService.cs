using Arsenal.Application.Services.Contracts;
using Arsenal.Fan;
using Arsenal.Gpu;
using Arsenal.Helpers;
using Arsenal.Mode;
using Arsenal.USB;
using Arsenal.AutoUpdate;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace Arsenal.Application.Services.Implementations
{
    public class ProfileService : IProfileService
    {
        public int AutoAcMode
        {
            get => AppConfig.Get("performance_1", AsusACPI.PerformanceBalanced);
            set => AppConfig.Set("performance_1", value);
        }

        public int AutoBatteryMode
        {
            get => AppConfig.Get("performance_0", AsusACPI.PerformanceSilent);
            set => AppConfig.Set("performance_0", value);
        }

        public bool IsAutoSwitchEnabled
        {
            get => AppConfig.IsNotFalse("auto_switch_enabled");
            set => AppConfig.Set("auto_switch_enabled", value ? 1 : 0);
        }

        public void OnPowerSourceChanged(bool isAc)
        {
            if (!IsAutoSwitchEnabled) return;
            int targetMode = isAc ? AutoAcMode : AutoBatteryMode;
            Program.modeControl?.SetPerformanceMode(targetMode, true);
        }

        public void OnAppForegroundChanged(string processName)
        {
            int profile = AppConfig.Get("app_profile_" + processName, -1);
            if (profile >= 0)
            {
                Program.modeControl?.SetPerformanceMode(profile, true);
            }
        }
    }
}
