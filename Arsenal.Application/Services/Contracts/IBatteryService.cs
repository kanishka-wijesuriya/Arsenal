using Arsenal.AutoUpdate;
using Arsenal.Display;

namespace Arsenal.Application.Services.Contracts
{
    public interface IBatteryService
    {
        int ChargeLimit { get; }
        bool IsFullChargeOverride { get; }
        float DischargeRateWatts { get; }
        int BatteryPercent { get; }
        event Action<int> ChargeLimitChanged;
        event Action<bool> FullChargeOverrideChanged;

        void SetChargeLimit(int limitPercent);
        void ToggleFullChargeOverride();
        void GenerateBatteryReport();

        /// <summary>Runs the Windows battery scan and returns it as data for the page.</summary>
        Task<Arsenal.Battery.BatteryReportData?> BuildBatteryReportAsync(CancellationToken cancel = default);
    }
}
