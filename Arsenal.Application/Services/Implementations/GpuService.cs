using Arsenal.Application.Models;
using Arsenal.Application.Services.Contracts;
using Arsenal.Gpu;
using Arsenal.Gpu.NVidia;
using Arsenal.Helpers;
using Arsenal.Mode;
using PawnIO;

namespace Arsenal.Application.Services.Implementations
{
    public class GpuService : IGpuService
    {
        private readonly bool _isEcoSupported;
        private readonly bool _isMuxSupported;

        /// <summary>
        /// The fixed part of the GPU's TGP, in watts. Read once: it is a property of
        /// the chassis, and DeviceGet on a machine without the register is a wasted
        /// round trip on every binding refresh.
        /// </summary>
        private readonly int _gpuPowerBase;

        /// <summary>
        /// Deferred because it shells out to nvidia-smi. Nothing on the startup path
        /// asks for it - only the performance page does, and only once it is opened.
        /// </summary>
        private readonly Lazy<int> _gpuPowerCeiling;

        public int CurrentGpuMode => AppConfig.Is("gpu_auto") ? 3 : AppConfig.Get("gpu_mode", AsusACPI.GPUModeStandard);
        public bool IsEcoSupported => _isEcoSupported;

        // The model list is authoritative for machines that ship without a discrete
        // GPU at all; Eco and MUX support only describe what can be done with one.
        public bool HasDedicatedGpu => !AppConfig.NoGpu();
        public bool IsMuxSupported => _isMuxSupported;
        public bool IsXgmConnected => Program.acpi.IsXGConnected();

        public event Action<int>? GpuModeChanged;
        public event Action<string?>? GpuLockStatusChanged;
        public event Action<bool, string?>? GpuBusyChanged;

        public GpuService()
        {
            // These are capabilities, not live mode state. Probing them for every remote
            // snapshot can queue a UI-thread WMI read behind an in-progress GPU write for
            // several seconds. AsusACPI's support cache is authoritative and immutable for
            // the lifetime of this machine session, so resolve each capability once.
            _isEcoSupported = Program.acpi.IsSupported(AsusACPI.GPUEco);
            _isMuxSupported = Program.acpi.IsSupported(AsusACPI.GPUMux);

            _gpuPowerBase = Program.acpi.IsSupported(AsusACPI.GPU_POWER)
                ? Program.acpi.DeviceGet(AsusACPI.GPU_BASE)
                : 0;

            // The card's own ceiling less the fixed base and less whatever Dynamic
            // Boost may add on top, because all three land on the same budget. This is
            // the calculation the writer's MaxGPUPower guard is checked against, so it
            // is also assigned back to keep the two from drifting apart.
            _gpuPowerCeiling = new Lazy<int>(() =>
            {
                if (_gpuPowerBase <= 0) return AsusACPI.MaxGPUPower;

                int cardMax = NvidiaSmi.GetMaxGPUPower();
                if (cardMax <= 0) return AsusACPI.MaxGPUPower;

                int ceiling = cardMax - _gpuPowerBase - AsusACPI.MaxGPUBoost;
                if (ceiling <= AsusACPI.MinGPUPower) return AsusACPI.MaxGPUPower;

                AsusACPI.MaxGPUPower = ceiling;
                Logger.WriteLine($"GPU TGP: base {_gpuPowerBase}W + up to {ceiling}W (card max {cardMax}W)");
                return ceiling;
            }, LazyThreadSafetyMode.ExecutionAndPublication);

            // Warmed off the startup thread so the first open of the performance page
            // does not wait on a process launch. It also settles AsusACPI.MaxGPUPower
            // early, which is what the writer checks stored values against - leaving it
            // until a page asked would keep dropping TGP writes on a machine whose owner
            // never opens that page.
            if (_gpuPowerBase > 0) Task.Run(() => _ = _gpuPowerCeiling.Value);

            GPUModeControl.OnGPUModeChanged += (_) => GpuModeChanged?.Invoke(CurrentGpuMode);
            GPUModeControl.OnLockGPUModes += (status) => GpuLockStatusChanged?.Invoke(status);
            GPUModeControl.OnGPUBusyChanged += (busy, message) => GpuBusyChanged?.Invoke(busy, message);
        }

        public void SetGpuMode(int mode, int auto = 0)
        {
            if (mode == 3)
            {
                int physicalMode = AppConfig.Get("gpu_mode", AsusACPI.GPUModeStandard);
                if (physicalMode == AsusACPI.GPUModeUltimate && Program.Bridge is not null &&
                    !Program.Bridge.ConfirmGpuModeChange(
                        physicalMode,
                        AsusACPI.GPUModeStandard,
                        new GpuModeChangeConfirmation(RequiresRestart: true, WarnsExternalDisplays: false)))
                {
                    GpuModeChanged?.Invoke(CurrentGpuMode);
                    return;
                }
                AppConfig.Set("gpu_auto", 1);
                Program.gpuControl?.AutoGPUMode(true);
                GpuModeChanged?.Invoke(CurrentGpuMode);
                return;
            }
            Program.gpuControl?.SetGPUMode(mode, auto);
        }

        public void SetGpuClocks(int coreOffsetMhz, int memoryOffsetMhz)
        {
            AppConfig.SetMode("gpu_core", coreOffsetMhz);
            AppConfig.SetMode("gpu_memory", memoryOffsetMhz);
            Program.modeControl?.SetGPUClocks(true);
        }

        public void SetGpuPower(int dynamicBoostW, int tempTargetC, int powerTargetW)
        {
            AppConfig.SetMode("gpu_boost", dynamicBoostW);
            AppConfig.SetMode("gpu_temp", tempTargetC);
            AppConfig.SetMode("gpu_power", powerTargetW);
            Program.modeControl?.SetGPUPower();
        }

        public void ToggleXgm()
        {
            Program.gpuControl?.ToggleXGM();
        }

        public void KillGpuApps()
        {
            Program.gpuControl?.KillGPUApps();
        }

        public void RestartNvServices()
        {
            NvidiaGpuControl.RestartNVService();
        }

        // NvidiaGpuControl widens its offset bounds in its own constructor once it has
        // identified the card, so these are read live rather than captured here - the
        // performance page is built well after that has happened.
        public ControlRange GpuCoreOffsetRange => new(NvidiaGpuControl.MinCoreOffset, NvidiaGpuControl.MaxCoreOffset);
        public ControlRange GpuMemoryOffsetRange => new(NvidiaGpuControl.MinMemoryOffset, NvidiaGpuControl.MaxMemoryOffset);
        public ControlRange GpuClockLimitRange => new(NvidiaGpuControl.MinClockLimit, NvidiaGpuControl.MaxClockLimit);
        public ControlRange GpuBoostRange => new(AsusACPI.MinGPUBoost, AsusACPI.MaxGPUBoost);
        public ControlRange GpuTempRange => new(AsusACPI.MinGPUTemp, AsusACPI.MaxGPUTemp);
        public ControlRange GpuPowerOffsetRange => new(AsusACPI.MinGPUPower, _gpuPowerCeiling.Value);
        public int GpuPowerBaseWatts => _gpuPowerBase;
        public bool IsGpuPowerAdjustable => _gpuPowerBase > 0;
    }
}
