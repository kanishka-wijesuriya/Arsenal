using Arsenal.Application.Models;
using Arsenal.Application.Services.Contracts;
using Arsenal.Display;
using Arsenal.Fan;
using Arsenal.Gpu;
using Arsenal.Helpers;
using Arsenal.Mode;
using Arsenal.USB;
using Arsenal.AutoUpdate;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace Arsenal.Application.Services.Implementations
{
    public class DeviceStateService : IDeviceStateService
    {
        /// <summary>
        /// How often a visible surface is refreshed. Fan, temperature and power
        /// readings move slowly enough that this is a presentation cadence, not a
        /// sampling requirement.
        /// </summary>
        private const double VisibleIntervalMs = 1000;

        private readonly System.Timers.Timer _telemetryTimer;
        private HardwareTelemetry _currentTelemetry = new();
        private int _pollInProgress;
        private volatile bool _publishUpdates;

        public HardwareTelemetry CurrentTelemetry => _currentTelemetry;
        public event Action<HardwareTelemetry>? TelemetryUpdated;

        public DeviceStateService()
        {
            // The dashboard is a permanent consumer of the original sensor loop.
            // Keep the fan, usage, power and battery channels active so homepage
            // telemetry reflects the same live readings as the legacy overlay.
            HardwareControl.readFans = true;
            HardwareControl.readUsage = true;
            HardwareControl.readPower = true;
            HardwareControl.readBattery = true;

            // Stopped until something is actually looking. Telemetry is sampled when
            // ResumePolling is called as a window opens.
            _telemetryTimer = new System.Timers.Timer(VisibleIntervalMs) { AutoReset = true };
            _telemetryTimer.Elapsed += (s, e) => PollHardware();
        }

        public void SetPollingInterval(TimeSpan interval)
        {
            _telemetryTimer.Interval = Math.Max(500, interval.TotalMilliseconds);
        }

        /// <summary>
        /// Stops sampling. Called whenever no window is on screen.
        ///
        /// Nothing consumes telemetry in that state, which is why this stops rather
        /// than slows down. The phone companion calls <see cref="RefreshNow"/> itself
        /// immediately before answering a request, and the in-game overlay reads the
        /// sensors directly, so neither depends on this timer. A background poll here
        /// was a complete ACPI, GPU and power read - the most expensive thing the
        /// application does - whose result was then thrown away.
        /// </summary>
        public void PausePolling()
        {
            _publishUpdates = false;
            _telemetryTimer.Stop();

            // NVML is the most expensive thing the application keeps open, and it is only
            // open because this timer reads GPU power and VRAM through it. Measured with
            // the address-space walk, nvml.dll charges this process 18.8MB of private
            // data - more than any other library, ours or the graphics drivers' - and it
            // is held for the life of the process for the sake of two numbers that
            // nothing is currently displaying.
            //
            // Every NvmlHelper getter calls Init itself, so this costs one re-initialise
            // the next time a window asks for GPU telemetry, on the background poll
            // ResumePolling already queues rather than on the thread drawing the page.
            NvmlHelper.Shutdown();
        }

        public void ResumePolling()
        {
            bool wasPublishing = _publishUpdates;
            _publishUpdates = true;
            _telemetryTimer.Interval = VisibleIntervalMs;
            _telemetryTimer.Start();

            if (wasPublishing) return;

            // Draw the last known sample at once so the page is never blank, then
            // replace it with a fresh one without waiting out a whole interval.
            TelemetryUpdated?.Invoke(_currentTelemetry);
            ThreadPool.QueueUserWorkItem(_ => PollHardware());
        }

        public void RefreshNow() => PollHardware();

        private void PollHardware()
        {
            // System.Timers.Timer is re-entrant. Sensor access includes ACPI, GPU and
            // power providers, so a slow sample must never overlap the next one and
            // multiply both CPU usage and native handles.
            if (Interlocked.Exchange(ref _pollInProgress, 1) != 0) return;
            try
            {
                // The optional legacy overlay can reset these shared flags when it
                // closes. Reassert them on every dashboard poll so RPM, usage and
                // power never become permanently null/zero after an overlay state
                // change. ReadSensorsOverlay is the original path that populates
                // HardwareControl.gpuPower from NVML/ADL.
                HardwareControl.readFans = true;
                HardwareControl.readUsage = true;
                HardwareControl.readPower = true;
                HardwareControl.readBattery = true;
                HardwareControl.ReadSensorsOverlay();

                float cpuTemp = HardwareControl.cpuTemp ?? 0;
                float cpuUsage = (float)(HardwareControl.cpuUsage ?? 0);
                float cpuPower = HardwareControl.cpuPower ?? 0;

                float gpuTemp = HardwareControl.gpuTemp ?? 0;
                float gpuUsage = (float)(HardwareControl.gpuUsage ?? 0);
                float gpuPower = HardwareControl.gpuPower ?? 0;

                int fanCpu = HardwareControl.cpuFanRPM ?? 0;
                int fanGpu = HardwareControl.gpuFanRPM ?? 0;
                int fanMid = 0;
                int fanXgm = 0;

                int batteryPercent = (int)Math.Round((float)System.Windows.Forms.SystemInformation.PowerStatus.BatteryLifePercent * 100);
                float dischargeRate = (float)(HardwareControl.batteryRate ?? 0);
                bool isAc = Program.currentSource != Program.PowerSource.Battery;

                string gpuStatus = GPUModeControl.gpuMode switch
                {
                    AsusACPI.GPUModeEco => "Eco (Disabled)",
                    AsusACPI.GPUModeStandard => "Standard (Active)",
                    AsusACPI.GPUModeUltimate => "Ultimate (dGPU Direct)",
                    _ => "Optimized"
                };

                _currentTelemetry = new HardwareTelemetry(
                    CpuTemp: cpuTemp,
                    CpuUsage: cpuUsage,
                    CpuPower: cpuPower,
                    GpuTemp: gpuTemp,
                    GpuUsage: gpuUsage,
                    GpuPower: gpuPower,
                    FanCpuRpm: fanCpu,
                    FanGpuRpm: fanGpu,
                    FanMidRpm: fanMid,
                    FanXgmRpm: fanXgm,
                    BatteryPercentage: batteryPercent,
                    BatteryDischargeRate: dischargeRate,
                    IsAcConnected: isAc,
                    GpuStatus: gpuStatus
                );

                if (_publishUpdates) TelemetryUpdated?.Invoke(_currentTelemetry);
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Telemetry error: " + ex.Message);
            }
            finally
            {
                Volatile.Write(ref _pollInProgress, 0);
            }
        }

        public void Dispose()
        {
            _telemetryTimer.Stop();
            _telemetryTimer.Dispose();
        }
    }
}
