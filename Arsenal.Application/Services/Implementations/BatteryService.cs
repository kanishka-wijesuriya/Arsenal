using Arsenal.Application.Models;
using Arsenal.Application.Services.Contracts;
using Arsenal.Battery;
using Arsenal.Display;
using Arsenal.Fan;
using Arsenal.Helpers;
using Arsenal.Peripherals;
using Arsenal.Peripherals.Keyboard;
using Arsenal.Peripherals.Keyboard.Models;
using Arsenal.Peripherals.Mouse;
using Arsenal.USB;
using System.Runtime.CompilerServices;
using System.Diagnostics;

namespace Arsenal.Application.Services.Implementations
{
    public class BatteryService : IBatteryService
    {
        public int ChargeLimit => AppConfig.Get("charge_limit", 80);
        public bool IsFullChargeOverride => BatteryControl.chargeFull;
        public float DischargeRateWatts => (float)(HardwareControl.batteryRate ?? 0);
        public int BatteryPercent => (int)Math.Round((float)System.Windows.Forms.SystemInformation.PowerStatus.BatteryLifePercent * 100);

        public event Action<int>? ChargeLimitChanged;
        public event Action<bool>? FullChargeOverrideChanged;

        public BatteryService()
        {
            BatteryControl.OnBatteryLimitChanged += (limit) => ChargeLimitChanged?.Invoke(limit);
            BatteryControl.OnBatteryFullChanged += (full) => FullChargeOverrideChanged?.Invoke(full);
        }

        public void SetChargeLimit(int limitPercent)
        {
            BatteryControl.SetBatteryChargeLimit(limitPercent);
        }

        public void ToggleFullChargeOverride()
        {
            BatteryControl.ToggleBatteryLimitFull();
        }

        public void GenerateBatteryReport()
        {
            BatteryControl.BatteryReport();
        }

        public Task<Arsenal.Battery.BatteryReportData?> BuildBatteryReportAsync(CancellationToken cancel = default)
            => Arsenal.Battery.BatteryReportReader.GenerateAsync(cancel);
    }
}
