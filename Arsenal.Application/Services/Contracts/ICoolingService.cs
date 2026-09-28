using Arsenal.Application.Models;
using Arsenal.AutoUpdate;
using Arsenal.Display;

namespace Arsenal.Application.Services.Contracts
{
    public interface ICoolingService
    {
        bool CustomFansSupported { get; }
        event Action<string> CalibrationStatusChanged;
        event Action CalibrationCompleted;

        FanCurveModel GetFanCurve(int fanIndex, int modeIndex);
        void SaveFanCurve(int fanIndex, int modeIndex, FanCurveModel curve);
        void ResetFanCurves(int modeIndex);
        void ApplyFanCurves(int modeIndex);
        void StartCalibration();
    }
}
