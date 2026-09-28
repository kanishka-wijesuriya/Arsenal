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
    public class CoolingService : ICoolingService
    {
        public bool CustomFansSupported => Program.acpi.IsSupported(AsusACPI.PerformanceMode);

        public event Action<string>? CalibrationStatusChanged;
        public event Action? CalibrationCompleted;

        public CoolingService()
        {
            FanSensorControl.OnCalibrationStatus += (status) => CalibrationStatusChanged?.Invoke(status);
            FanSensorControl.OnCalibrationCompleted += () => CalibrationCompleted?.Invoke();
        }

        public FanCurveModel GetFanCurve(int fanIndex, int modeIndex)
        {
            AsusFan fan = (AsusFan)fanIndex;
            string fanName = fan switch
            {
                AsusFan.CPU => "CPU Fan",
                AsusFan.GPU => "GPU Fan",
                AsusFan.Mid => "Mid Fan",
                AsusFan.XGM => "XGM Fan",
                _ => $"Fan {fanIndex}"
            };

            byte[]? data = AppConfig.GetFanConfig(fan);
            if (data == null || data.Length < 16)
            {
                return FanCurveModel.CreateDefault(fanIndex, fanName);
            }

            return FanCurveModel.FromByteArray(fanIndex, fanName, data);
        }

        public void SaveFanCurve(int fanIndex, int modeIndex, FanCurveModel curve)
        {
            AsusFan fan = (AsusFan)fanIndex;
            AppConfig.SetFanConfig(fan, curve.ToByteArray());
        }

        public void ResetFanCurves(int modeIndex)
        {
            AppConfig.SetFanConfig(AsusFan.CPU, AppConfig.GetDefaultCurve(AsusFan.CPU));
            AppConfig.SetFanConfig(AsusFan.GPU, AppConfig.GetDefaultCurve(AsusFan.GPU));
            AppConfig.SetFanConfig(AsusFan.Mid, AppConfig.GetDefaultCurve(AsusFan.Mid));
            ApplyFanCurves(modeIndex);
        }

        public void ApplyFanCurves(int modeIndex)
        {
            AppConfig.SetMode("auto_apply", 1);
            Program.modeControl?.AutoFans(true);
        }

        public void StartCalibration()
        {
            new FanSensorControl().StartCalibration();
        }
    }
}
