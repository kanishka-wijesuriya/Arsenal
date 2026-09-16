using Arsenal.Display;
using Arsenal.Gpu.NVidia;
using Arsenal.Helpers;
using Arsenal.USB;
using System.Diagnostics;

namespace Arsenal.Gpu
{
    public class GPUModeControl
    {
        public static int gpuMode;
        public static bool? gpuExists = null;

        static bool nvRestartPending;

        public static event Action<int>? OnGPUModeChanged;
        public static event Action<bool, bool>? OnGPUButtonsChanged;
        public static event Action<bool>? OnHideGPUModes;
        public static event Action<string?>? OnLockGPUModes;

        /// <summary>
        /// True for the whole span of a GPU switch, false once it has finished.
        /// OnLockGPUModes cannot be used for this: it fires again part-way through the
        /// NVIDIA service restart, and the mode-changed events it sits between are
        /// raised mid-operation, so a listener could not tell start from end.
        /// </summary>
        public static event Action<bool, string?>? OnGPUBusyChanged;

        public GPUModeControl()
        {
        }

        public void InitGPUMode()
        {
            if (AppConfig.NoGpu())
            {
                OnHideGPUModes?.Invoke(false);
                return;
            }

            int eco = Program.acpi.DeviceGet(AsusACPI.GPUEco);
            int mux = Program.acpi.DeviceGet(AsusACPI.GPUMux);

            Logger.WriteLine("Eco flag : " + eco);
            Logger.WriteLine("Mux flag : " + mux);

            if (eco == 1 && HardwareControl.GpuControl?.IsValid == true)
            {
                Logger.WriteLine("Eco half-state");
                if (AppConfig.IsEcoBootFix())
                {
                    HardwareControl.DisposeGpuControl();
                    Task.Run(() => Program.acpi.DeviceSet(AsusACPI.GPUEco, eco, "GPUEco Force Fix"));
                }
            }

            OnGPUButtonsChanged?.Invoke(eco >= 0, mux >= 0);

            if (mux == 0)
            {
                gpuMode = AsusACPI.GPUModeUltimate;
            }
            else
            {
                if (eco == 1)
                    gpuMode = AsusACPI.GPUModeEco;
                else
                    gpuMode = AsusACPI.GPUModeStandard;

                // GPU mode not supported
                if (eco < 0 && mux < 0)
                {
                    if (gpuExists is null) gpuExists = Program.acpi.GetFan(AsusFan.GPU) >= 0;
                    OnHideGPUModes?.Invoke((bool)gpuExists);
                }
            }

            AppConfig.Set("gpu_mode", gpuMode);
            OnGPUModeChanged?.Invoke(gpuMode);
            Program.Bridge?.VisualiseGPUMode();

            Aura.CustomRGB.ApplyGPUColor(gpuMode);

            CheckGpuError();
        }

        public void SetGPUMode(int GPUMode, int auto = 0)
        {
            int CurrentGPU = AppConfig.Get("gpu_mode");

            bool crossesUltimate = (CurrentGPU == AsusACPI.GPUModeUltimate) != (GPUMode == AsusACPI.GPUModeUltimate);
            bool externalDisplayConnected = auto == 0 &&
                CurrentGPU != GPUMode &&
                GPUMode == AsusACPI.GPUModeEco &&
                ScreenNative.IsExternalDisplayConnected(log: true);
            var confirmation = GpuModeChangeConfirmation.Evaluate(
                CurrentGPU, GPUMode, auto, externalDisplayConnected);

            if (confirmation.IsRequired && Program.Bridge is not null &&
                !Program.Bridge.ConfirmGpuModeChange(CurrentGPU, GPUMode, confirmation))
            {
                OnGPUModeChanged?.Invoke(CurrentGPU);
                Program.Bridge.VisualiseGPUMode();
                return;
            }

            AppConfig.Set("gpu_auto", auto);

            if (CurrentGPU == GPUMode)
            {
                OnGPUModeChanged?.Invoke(GPUMode);
                Program.Bridge?.VisualiseGPUMode();
                return;
            }

            if (crossesUltimate)
            {
                if (GPUMode == AsusACPI.GPUModeUltimate && Program.acpi.DeviceGet(AsusACPI.GPUMux) < 0)
                {
                    Logger.WriteLine("Mux not supported");
                    OnGPUModeChanged?.Invoke(CurrentGPU);
                    Program.Bridge?.VisualiseGPUMode();
                    return;
                }

                // The MUX sequence takes the best part of a second - a register write, a
                // settle delay, a read back - and it used to run on whichever thread
                // pressed the tile, which for the quick panel and the main window is the
                // dispatcher. The overlay was raised and then had no thread left to draw
                // itself with, so the spinner arrived already stalled. It runs on a worker
                // now; everything it raises marshals back to the UI on its own.
                OnGPUBusyChanged?.Invoke(true, GPUMode == AsusACPI.GPUModeUltimate
                    ? "Switching to Ultimate"
                    : "Leaving Ultimate");
                Task.Run(() => SwitchMuxMode(CurrentGPU, GPUMode));
                return;
            }

            if (GPUMode == AsusACPI.GPUModeEco)
            {
                OnGPUModeChanged?.Invoke(GPUMode);
                Program.Bridge?.VisualiseGPUMode();
                AppConfig.Set("gpu_mode", GPUMode);
                SetGPUEco(1);
            }
            else if (GPUMode == AsusACPI.GPUModeStandard)
            {
                OnGPUModeChanged?.Invoke(GPUMode);
                Program.Bridge?.VisualiseGPUMode();
                AppConfig.Set("gpu_mode", GPUMode);
                SetGPUEco(0);
            }
        }

        /// <summary>
        /// The multiplexer half of a GPU switch, which ends in a reboot. Kept off the
        /// calling thread - see <see cref="SetGPUMode"/>.
        /// </summary>
        private void SwitchMuxMode(int currentMode, int targetMode)
        {
            try
            {
                if (targetMode == AsusACPI.GPUModeUltimate)
                {
                    Program.acpi.SetGPUEco(0);
                    Thread.Sleep(500);

                    int eco = Program.acpi.DeviceGet(AsusACPI.GPUEco);
                    Logger.WriteLine("Eco flag : " + eco);
                    if (eco == 1)
                    {
                        // The dGPU never came back, so driving the panel from it would
                        // leave a black screen after the restart.
                        OnGPUBusyChanged?.Invoke(false, null);
                        OnGPUModeChanged?.Invoke(currentMode);
                        Program.Bridge?.VisualiseGPUMode();
                        return;
                    }

                    Program.acpi.DeviceSet(AsusACPI.GPUMux, 0, "GPUMux");
                }
                else
                {
                    Program.acpi.DeviceSet(AsusACPI.GPUMux, 1, "GPUMux");
                }

                AppConfig.Set("gpu_mode", targetMode);
                OnGPUModeChanged?.Invoke(targetMode);
                Program.Bridge?.VisualiseGPUMode();

                // Deliberately left busy: Windows is going down, and clearing the
                // overlay here would flash the UI back for the last second.
                OnGPUBusyChanged?.Invoke(true, "Restarting Windows");
                Process.Start(ProcessHelper.SystemPath("shutdown"), "/r /t 1");
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Error switching GPU Mux: " + ex.Message);
                OnGPUBusyChanged?.Invoke(false, null);
                OnGPUModeChanged?.Invoke(currentMode);
                Program.Bridge?.VisualiseGPUMode();
            }
        }

        public void SetGPUEco(int eco)
        {
            OnLockGPUModes?.Invoke(null);
            OnGPUBusyChanged?.Invoke(true, eco == 1 ? "Switching to Eco" : "Switching to Standard");

            Task.Run(async () =>
            {
                try
                {
                int status = 1;

                Program.modeControl?.WaitForApply();

                if (eco == 1)
                {
                    HardwareControl.KillGPUApps();
                    HardwareControl.DisposeGpuControl();
                    if (AppConfig.IsNVPlatform()) NvidiaGpuControl.StopNVService();
                }

                Logger.WriteLine($"Running eco command {eco}");

                try
                {
                    status = Program.acpi.SetGPUEco(eco);
                    await Task.Delay(TimeSpan.FromMilliseconds(AppConfig.Get("refresh_delay", 500)));

                    InitGPUMode();
                    ScreenControl.AutoScreen();

                    if (eco == 0)
                    {
                        if (AppConfig.IsNVPlatform() || nvRestartPending)
                        {
                            OnLockGPUModes?.Invoke("Restarting NV Services...");
                            OnGPUBusyChanged?.Invoke(true, "Restarting NV Services...");
                            await Task.Delay(TimeSpan.FromMilliseconds(AppConfig.Get("nv_delay", 5000)));
                            if (AppConfig.IsNVPlatform()) NvidiaGpuControl.RestartNVService();
                            else NvidiaGpuControl.RestartNvContainer();
                            nvRestartPending = false;
                            InitGPUMode();
                            await Task.Delay(TimeSpan.FromMilliseconds(1000));
                        }

                        await HardwareControl.RecreateGpuControlWithRetry(3, 2);
                        if (HardwareControl.GpuControl is null)
                            await HardwareControl.RecreateGpuControlWithRetry(3, 5);
                        CheckGpuError();
                        CheckStandardHalfState();
                    }

                    if (AppConfig.IsModeReapply())
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(1000));
                        Program.modeControl?.AutoPerformance();
                    }
                    else
                    {
                        Program.modeControl?.SetGPUClocks(false);
                    }
                }
                catch (Exception ex)
                {
                    Logger.WriteLine("Error setting GPU Eco: " + ex.Message);
                }
                }
                finally
                {
                    OnGPUBusyChanged?.Invoke(false, null);
                }
            });
        }

        public static bool IsPlugged() =>
            Program.currentSource == Program.PowerSource.Barrel ||
            (Program.currentSource == Program.PowerSource.USBC && !AppConfig.Is("optimized_usbc"));

        public static bool suspended = false;

        public bool AutoGPUMode(bool optimized = false, int delay = 0)
        {
            bool GpuAuto = AppConfig.Is("gpu_auto");
            bool ForceGPU = AppConfig.IsForceSetGPUMode() && !GpuAuto;

            int GpuMode = AppConfig.Get("gpu_mode");

            if (!GpuAuto && !ForceGPU) return false;

            if (suspended)
            {
                Logger.WriteLine("Skipping GPU Mode switch: Suspend");
                return false;
            }

            int eco = Program.acpi.DeviceGet(AsusACPI.GPUEco);
            int mux = Program.acpi.DeviceGet(AsusACPI.GPUMux);

            if (mux == 0)
            {
                if (optimized) SetGPUMode(AsusACPI.GPUModeStandard, 1);
                return false;
            }
            else
            {
                if (eco == 1)
                    if ((GpuAuto && IsPlugged()) || (ForceGPU && GpuMode == AsusACPI.GPUModeStandard))
                    {
                        if (delay > 0) Thread.Sleep(delay);
                        SetGPUEco(0);
                        return true;
                    }
                if (eco == 0)
                    if ((GpuAuto && !IsPlugged()) || (ForceGPU && GpuMode == AsusACPI.GPUModeEco))
                    {
                        if (Program.acpi.IsXGConnected()) return false;
                        if (HardwareControl.IsUsedGPU())
                        {
                            // If dGPU is heavily in use, skip auto eco or proceed based on preference
                            if (!AppConfig.Is("force_auto_eco")) return false;
                        }

                        if (delay > 0) Thread.Sleep(delay);
                        SetGPUEco(1);
                        return true;
                    }
            }

            return false;
        }

        public void ToggleXGM(bool silent = false)
        {
            Task.Run(async () =>
            {
                OnLockGPUModes?.Invoke(null);

                if (Program.acpi.DeviceGet(AsusACPI.GPUXG) == 1)
                {
                    XGM.Reset();
                    HardwareControl.KillGPUApps();

                    Program.acpi.DeviceSet(AsusACPI.GPUXG, 0, "GPU XGM");
                    await Task.Delay(TimeSpan.FromSeconds(15));
                    HardwareControl.RecreateGpuControl();
                }
                else
                {
                    if (AppConfig.Is("xgm_special"))
                        Program.acpi.DeviceSet(AsusACPI.GPUXG, 0x101, "GPU XGM");
                    else
                        Program.acpi.DeviceSet(AsusACPI.GPUXG, 1, "GPU XGM");

                    XGM.Init();

                    await Task.Delay(TimeSpan.FromSeconds(15));
                    await HardwareControl.RecreateGpuControlWithRetry(6, 5);

                    if (AppConfig.IsApplyFans())
                        XGM.SetFan(AppConfig.GetFanConfig(AsusFan.XGM));
                }

                InitGPUMode();
            });
        }

        public void KillGPUApps()
        {
            if (HardwareControl.GpuControl is not null)
            {
                HardwareControl.GpuControl.KillGPUApps();
            }
        }

        public void CaptureNvBootState()
        {
            nvRestartPending = Program.acpi.IsNVidiaGPU() && Program.acpi.DeviceGet(AsusACPI.GPUEco) == 1;
        }

        public void CheckStandardHalfState()
        {
            if (gpuMode != AsusACPI.GPUModeStandard || HardwareControl.GpuControl is not null) return;

            Logger.WriteLine("Standard half-state");
            if (!AppConfig.IsStandardForceFix()) return;

            Task.Run(async () =>
            {
                Program.acpi.DeviceSet(AsusACPI.GPUEco, 0, "GPUStandard Force Fix");
                await Task.Delay(TimeSpan.FromMilliseconds(AppConfig.Get("nv_delay", 5000)));
                HardwareControl.RecreateGpuControl();
            });
        }

        public void StandardModeFix()
        {
            if (!AppConfig.IsStandardModeFix()) return;
            if (Program.acpi.DeviceGet(AsusACPI.GPUMux) == 0) return; // Ultimate mode

            Logger.WriteLine("Forcing Standard Mode on shutdown");
            Program.acpi.SetGPUEco(0);
        }

        public static string? gpuError = null;

        public static void CheckGpuError() => Task.Run(() =>
        {
            string? error = DeviceHelper.GetGpuError();
            if (gpuError == error) return;
            gpuError = error;
            if (error != null) Logger.WriteLine(error);
            OnGPUModeChanged?.Invoke(gpuMode);
            Program.Bridge?.VisualiseGPUMode();
        });
    }
}
