using Arsenal.Application.Models;
using Arsenal.Display;

namespace Arsenal.Application.Services.Contracts
{
    public interface IPerformanceService
    {
        int CurrentMode { get; }
        string CurrentModeName { get; }
        event Action<int> ModeChanged;
        event Action<string> ModeLabelChanged;
        event Action ProfilesChanged;

        /// <summary>
        /// Applies a user-selected performance profile. Manual selections notify by
        /// default so every presentation surface provides the same immediate feedback.
        /// </summary>
        void SetMode(int modeIndex, bool notify = true);
        void CycleMode(bool backward = false);
        PerformanceProfile GetCurrentProfile();
        IReadOnlyList<PerformancePlanInfo> GetProfiles();
        int CreateProfile(string? name = null);
        bool RenameProfile(int modeIndex, string name);
        bool DeleteProfile(int modeIndex);
        void SaveProfile(PerformanceProfile profile);
        void ResetProfile(int modeIndex);
        bool IsCpuBoostSupported { get; }
        bool IsRyzenSmuSupported { get; }
        bool IsIntelMsrSupported { get; }
        /// <summary>
        /// Curve Optimizer undervolting, which needs PawnIO present and one of the
        /// CPUs the original allow-lists. Writes are dropped for anything else, so
        /// the sliders are hidden rather than shown doing nothing.
        /// </summary>
        bool IsUndervoltSupported { get; }
        /// <summary>The integrated-GPU offset, which is narrower still.</summary>
        bool IsIgpuUndervoltSupported { get; }
        void ApplyPowerLimits(int spl, int sppt, int fppt);
        void ApplyUndervolt(int cpuUvMv, int igpuUvMv);

        /// <summary>
        /// SPL, SPPT and FPPT all share one ceiling, because that is what the writer
        /// checks all three against. Varies by chassis - 50 W on an Ally, 250 W on an
        /// Advantage Edition - so it cannot be a constant in a view.
        /// </summary>
        ControlRange PowerLimitRange { get; }

        /// <summary>Throttle temperature this CPU will take, not a generic 65-105.</summary>
        ControlRange CpuTempRange { get; }

        /// <summary>Curve Optimizer offset, in millivolts and always negative.</summary>
        ControlRange CpuUndervoltRange { get; }

        /// <summary>The integrated-GPU offset, narrower than the CPU one.</summary>
        ControlRange IgpuUndervoltRange { get; }

        /// <summary>Fan hysteresis in degrees. Zero means "leave the firmware alone".</summary>
        ControlRange FanHysteresisRange { get; }

        /// <summary>
        /// The firmware's own CPU limits on newer chassis: cross-loading power, the share
        /// of the GPU's dynamic boost handed to the CPU, and a temperature limit. Each is
        /// there only where the firmware answers for it.
        /// </summary>
        bool IsCrossLoadSupported { get; }
        bool IsGpuToCpuSupported { get; }
        bool IsFirmwareCpuTempSupported { get; }
        ControlRange CrossLoadRange { get; }

        /// <summary>Taken in 5 W steps.</summary>
        ControlRange GpuToCpuRange { get; }
        ControlRange FirmwareCpuTempRange { get; }
    }
}
