using Arsenal.Application.Models;
using Arsenal.Display;

namespace Arsenal.Application.Services.Contracts
{
    public interface IGpuService
    {
        int CurrentGpuMode { get; }
        bool IsEcoSupported { get; }
        bool IsMuxSupported { get; }

        /// <summary>
        /// Whether this machine has a discrete GPU at all. Distinct from Eco and MUX
        /// support: a dedicated GPU that can be neither switched off nor multiplexed
        /// still exists, still draws power and still deserves its clock controls.
        /// </summary>
        bool HasDedicatedGpu { get; }
        bool IsXgmConnected { get; }
        event Action<int> GpuModeChanged;
        event Action<string?> GpuLockStatusChanged;
        /// <summary>Busy for the whole span of a GPU switch, with a stage message.</summary>
        event Action<bool, string?> GpuBusyChanged;

        void SetGpuMode(int mode, int auto = 0);
        void SetGpuClocks(int coreOffsetMhz, int memoryOffsetMhz);
        void SetGpuPower(int dynamicBoostW, int tempTargetC, int powerTargetW);
        void ToggleXgm();
        void KillGpuApps();
        void RestartNvServices();

        /// <summary>Core clock offset this GPU accepts - wider on the newer parts.</summary>
        ControlRange GpuCoreOffsetRange { get; }

        /// <summary>Memory clock offset, which widens with the same parts.</summary>
        ControlRange GpuMemoryOffsetRange { get; }

        /// <summary>Maximum-clock cap. The top of the range means "no cap".</summary>
        ControlRange GpuClockLimitRange { get; }

        /// <summary>Dynamic Boost wattage. 5, 15, 20 or 25 W depending on the model.</summary>
        ControlRange GpuBoostRange { get; }

        /// <summary>Temperature target the firmware will take for the discrete GPU.</summary>
        ControlRange GpuTempRange { get; }

        /// <summary>
        /// The adjustable part of the GPU's power budget, which sits on top of
        /// <see cref="GpuPowerBaseWatts"/> rather than replacing it.
        /// </summary>
        ControlRange GpuPowerOffsetRange { get; }

        /// <summary>
        /// The fixed part of the GPU's TGP, read from the firmware. The wattage the
        /// user cares about is this plus the offset, so the readout adds them.
        /// </summary>
        int GpuPowerBaseWatts { get; }

        /// <summary>
        /// False when the firmware reports no base TGP. The offset is meaningless
        /// without one - there is nothing for it to be an offset from - and its own
        /// ceiling is derived from it, so the row has no range to draw.
        /// </summary>
        bool IsGpuPowerAdjustable { get; }
    }
}
