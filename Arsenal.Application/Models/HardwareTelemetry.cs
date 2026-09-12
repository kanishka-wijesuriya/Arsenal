namespace Arsenal.Application.Models
{
    public record HardwareTelemetry(
        float CpuTemp = 0,
        float CpuUsage = 0,
        float CpuPower = 0,
        float GpuTemp = 0,
        float GpuUsage = 0,
        float GpuPower = 0,
        int FanCpuRpm = 0,
        int FanGpuRpm = 0,
        int FanMidRpm = 0,
        int FanXgmRpm = 0,
        int BatteryPercentage = 0,
        float BatteryDischargeRate = 0,
        bool IsAcConnected = true,
        string GpuStatus = "Active"
    );
}
