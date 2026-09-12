namespace Arsenal.Application.Models
{
    public class PerformanceProfile
    {
        public int ModeIndex { get; set; }
        public string Name { get; set; } = string.Empty;
        public int CpuBoost { get; set; } = 0; // 0=Disabled, 1=Enabled, 2=Aggressive, etc.
        public int Spl { get; set; } = -1; // Sustained Power Limit (W)
        public int Sppt { get; set; } = -1; // Slow Package Power Tracking (W)
        public int Fppt { get; set; } = -1; // Fast Package Power Tracking (W)
        public int CpuTempLimit { get; set; } = -1;
        public int CpuUndervolt { get; set; } = 0; // mV offset (negative)
        public int IgpuUndervolt { get; set; } = 0;
        public int GpuCoreOffset { get; set; } = 0; // MHz
        public int GpuMemoryOffset { get; set; } = 0; // MHz
        public int GpuBoost { get; set; } = -1; // Dynamic Boost (W)
        public int GpuTempTarget { get; set; } = -1; // °C
        public int GpuPowerTarget { get; set; } = -1; // TGP (W)
        public int GpuClockLimit { get; set; } = 0;
        public int FanHysteresisUp { get; set; } = 0;
        public int FanHysteresisDown { get; set; } = 0;
        public bool ApplyUndervolt { get; set; } = false;
        public bool ApplyFans { get; set; } = false;
        public bool ApplyPower { get; set; } = false;
    }
}
