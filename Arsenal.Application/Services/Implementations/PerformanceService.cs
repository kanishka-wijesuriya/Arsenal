using Arsenal.Application.Models;
using Arsenal.Application.Services.Contracts;
using Arsenal.Gpu;
using Arsenal.Gpu.NVidia;
using Arsenal.Helpers;
using Arsenal.Mode;
using PawnIO;

namespace Arsenal.Application.Services.Implementations
{
    public class PerformanceService : IPerformanceService
    {
        public int CurrentMode => Modes.GetCurrent();
        public string CurrentModeName => Modes.GetCurrentName();

        public event Action<int>? ModeChanged;
        public event Action<string>? ModeLabelChanged;
        public event Action? ProfilesChanged;

        public bool IsCpuBoostSupported => true;
        public bool IsRyzenSmuSupported => CpuInfo.IsAMD;
        public bool IsIntelMsrSupported => !CpuInfo.IsAMD;

        // Same two conditions the original applies before showing its undervolt
        // panels: the PawnIO driver has to be there, and the CPU has to be one that
        // ModeControl will actually write an offset for.
        private static bool PawnInstalled => ModeControl.IsPawnAvailable() || ModeControl.IsPawnInstalled();
        public bool IsUndervoltSupported => PawnInstalled && CpuInfo.IsSupportedUV();
        public bool IsIgpuUndervoltSupported => PawnInstalled && CpuInfo.IsSupportedUViGPU();

        // Every one of these mirrors a guard the writer already applies. AsusACPI
        // narrows its statics in its constructor from the chassis model, and CpuInfo
        // reads its own from config, so both are settled long before a page is built.
        public ControlRange PowerLimitRange => new(AsusACPI.MinTotal, AsusACPI.MaxTotal);
        public ControlRange CpuTempRange => new(CpuInfo.MinTemp, CpuInfo.DefaultTemp);
        public ControlRange CpuUndervoltRange => new(CpuInfo.MinCPUUV, CpuInfo.MaxCPUUV);
        public ControlRange IgpuUndervoltRange => new(CpuInfo.MinIGPUUV, CpuInfo.MaxIGPUUV);

        // Not a hardware register range: the payload is a byte of degrees, and zero is
        // the writer's "leave the firmware default alone". Twenty degrees is already
        // past any useful curve, so the ceiling is a sanity bound rather than a limit.
        public ControlRange FanHysteresisRange => new(0, 20);

        public PerformanceService()
        {
            ModeControl.OnModeChanged += (mode) => ModeChanged?.Invoke(mode);
            ModeControl.OnModeLabelChanged += (label) => ModeLabelChanged?.Invoke(label);
        }

        public void SetMode(int modeIndex, bool notify = true)
        {
            Program.modeControl?.SetPerformanceMode(modeIndex, notify);
        }

        public void CycleMode(bool backward = false)
        {
            Program.modeControl?.CyclePerformanceMode(backward);
        }

        public PerformanceProfile GetCurrentProfile()
        {
            int mode = CurrentMode;
            return new PerformanceProfile
            {
                ModeIndex = mode,
                Name = Modes.GetCurrentName(),
                CpuBoost = AppConfig.GetMode("auto_boost"),
                Spl = AppConfig.GetMode("limit_total"),
                Sppt = AppConfig.GetMode("limit_slow"),
                Fppt = AppConfig.GetMode("limit_fast"),
                CpuTempLimit = AppConfig.GetMode("cpu_temp"),
                CpuUndervolt = AppConfig.GetMode("cpu_uv"),
                IgpuUndervolt = AppConfig.GetMode("igpu_uv"),
                GpuCoreOffset = AppConfig.GetMode("gpu_core") == -1 ? 0 : AppConfig.GetMode("gpu_core"),
                GpuMemoryOffset = AppConfig.GetMode("gpu_memory") == -1 ? 0 : AppConfig.GetMode("gpu_memory"),
                GpuBoost = AppConfig.GetMode("gpu_boost"),
                GpuTempTarget = AppConfig.GetMode("gpu_temp"),
                GpuPowerTarget = AppConfig.GetMode("gpu_power"),
                GpuClockLimit = AppConfig.GetMode("gpu_clock_limit") < 0 ? NvidiaGpuControl.MaxClockLimit : AppConfig.GetMode("gpu_clock_limit"),
                FanHysteresisUp = AppConfig.GetMode("hysteresis_up"),
                FanHysteresisDown = AppConfig.GetMode("hysteresis_down"),
                ApplyUndervolt = AppConfig.IsMode("auto_uv"),
                ApplyFans = AppConfig.IsApplyFans(),
                ApplyPower = AppConfig.IsApplyPower()
            };
        }

        public IReadOnlyList<PerformancePlanInfo> GetProfiles() => Modes.GetList()
            .Select(mode => new PerformancePlanInfo(mode, Modes.GetName(mode), Modes.GetBase(mode), mode > 2))
            .ToList();

        public int CreateProfile(string? name = null)
        {
            int mode = Modes.Add(name);
            if (mode < 0) return mode;

            ProfilesChanged?.Invoke();
            SetMode(mode);
            return mode;
        }

        public bool RenameProfile(int modeIndex, string name)
        {
            if (!Modes.Rename(modeIndex, name)) return false;

            ProfilesChanged?.Invoke();
            if (modeIndex == CurrentMode) ModeLabelChanged?.Invoke(Modes.GetName(modeIndex));
            return true;
        }

        public bool DeleteProfile(int modeIndex)
        {
            if (modeIndex <= 2 || !Modes.Exists(modeIndex)) return false;

            bool wasCurrent = modeIndex == CurrentMode;
            Modes.Remove(modeIndex);
            ProfilesChanged?.Invoke();
            if (wasCurrent) SetMode(AsusACPI.PerformanceBalanced);
            return true;
        }

        public void SaveProfile(PerformanceProfile profile)
        {
            if (profile.CpuBoost >= 0) AppConfig.SetMode("auto_boost", profile.CpuBoost);
            if (profile.Spl > 0) AppConfig.SetMode("limit_total", profile.Spl);
            if (profile.Sppt > 0) AppConfig.SetMode("limit_slow", profile.Sppt);
            if (profile.Fppt > 0) AppConfig.SetMode("limit_fast", profile.Fppt);
            if (profile.CpuTempLimit > 0) AppConfig.SetMode("cpu_temp", profile.CpuTempLimit);
            AppConfig.SetMode("cpu_uv", profile.CpuUndervolt);
            AppConfig.SetMode("igpu_uv", profile.IgpuUndervolt);

            AppConfig.SetMode("gpu_core", profile.GpuCoreOffset);
            AppConfig.SetMode("gpu_memory", profile.GpuMemoryOffset);
            if (profile.GpuBoost >= 0) AppConfig.SetMode("gpu_boost", profile.GpuBoost);
            if (profile.GpuTempTarget > 0) AppConfig.SetMode("gpu_temp", profile.GpuTempTarget);
            if (profile.GpuPowerTarget > 0) AppConfig.SetMode("gpu_power", profile.GpuPowerTarget);
            AppConfig.SetMode("gpu_clock_limit", profile.GpuClockLimit);
            AppConfig.SetMode("hysteresis_up", profile.FanHysteresisUp);
            AppConfig.SetMode("hysteresis_down", profile.FanHysteresisDown);

            AppConfig.SetMode("auto_uv", profile.ApplyUndervolt ? 1 : 0);
            AppConfig.SetMode("auto_apply", profile.ApplyFans ? 1 : 0);
            AppConfig.SetMode("auto_apply_power", profile.ApplyPower ? 1 : 0);

            Program.modeControl?.SetPerformanceMode(CurrentMode, true);
        }

        public void ResetProfile(int modeIndex)
        {
            AppConfig.RemoveMode("auto_boost");
            AppConfig.RemoveMode("limit_slow");
            AppConfig.RemoveMode("limit_fast");
            AppConfig.RemoveMode("limit_total");
            AppConfig.RemoveMode("cpu_temp");
            AppConfig.RemoveMode("cpu_uv");
            AppConfig.RemoveMode("igpu_uv");
            AppConfig.RemoveMode("gpu_core");
            AppConfig.RemoveMode("gpu_memory");
            AppConfig.RemoveMode("gpu_boost");
            AppConfig.RemoveMode("gpu_temp");
            AppConfig.RemoveMode("gpu_power");
            AppConfig.RemoveMode("gpu_clock_limit");
            AppConfig.RemoveMode("hysteresis_up");
            AppConfig.RemoveMode("hysteresis_down");
            AppConfig.RemoveMode("auto_uv");
            AppConfig.RemoveMode("auto_apply");
            AppConfig.RemoveMode("auto_apply_power");

            Program.modeControl?.SetPerformanceMode(modeIndex, true);
        }

        public void ApplyPowerLimits(int spl, int sppt, int fppt)
        {
            AppConfig.SetMode("limit_total", spl);
            AppConfig.SetMode("limit_slow", sppt);
            AppConfig.SetMode("limit_fast", fppt);
            AppConfig.SetMode("auto_apply_power", 1);
            Program.modeControl?.SetPower();
        }

        public void ApplyUndervolt(int cpuUvMv, int igpuUvMv)
        {
            AppConfig.SetMode("cpu_uv", cpuUvMv);
            AppConfig.SetMode("igpu_uv", igpuUvMv);
            AppConfig.SetMode("auto_uv", 1);
            Program.modeControl?.SetRyzenPower();
        }
    }
}
