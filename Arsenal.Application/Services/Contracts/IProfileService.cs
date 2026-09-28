using Arsenal.Application.Models;
using Arsenal.AutoUpdate;
using Arsenal.Display;

namespace Arsenal.Application.Services.Contracts
{
    public interface IProfileService
    {
        int AutoAcMode { get; set; }
        int AutoBatteryMode { get; set; }
        bool IsAutoSwitchEnabled { get; set; }
        void OnPowerSourceChanged(bool isAc);
        void OnAppForegroundChanged(string processName);
    }
}
