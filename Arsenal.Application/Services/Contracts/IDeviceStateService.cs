using Arsenal.Application.Models;
using Arsenal.AutoUpdate;
using Arsenal.Display;

namespace Arsenal.Application.Services.Contracts
{
    public interface IDeviceStateService : IDisposable
    {
        HardwareTelemetry CurrentTelemetry { get; }
        event Action<HardwareTelemetry> TelemetryUpdated;
        void SetPollingInterval(TimeSpan interval);
        void PausePolling();
        void ResumePolling();
        void RefreshNow();
    }
}
