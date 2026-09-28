using Arsenal.Application.Services.Contracts;
using Arsenal.Display;
using Arsenal.Helpers;
using Arsenal.Peripherals.Keyboard;
using Arsenal.Peripherals.Keyboard.Models;
using Arsenal.Peripherals.Mouse;
using Arsenal.USB;
using System.Runtime.CompilerServices;
using System.Diagnostics;

namespace Arsenal.Application.Services.Implementations
{
    public class DisplayService : IDisplayService, IDisposable
    {
        public int CurrentRefreshRate => AppConfig.Get("frequency", 60);
        // Keep the maximum-rate source identical to the legacy implementation.
        // It honours the optional max_rate override before querying the panel.
        // GetMaxRate resolves a failed enumeration from cache, so this never answers
        // the -1 that used to reach the refresh-rate buttons as "-1 Hz" whenever the
        // internal panel was off behind an external monitor.
        public int MaxRefreshRate => ScreenControl.GetMaxRate(ScreenNative.FindLaptopScreen(true));
        public bool IsInternalPanelActive => ScreenNative.GetRefreshRate(ScreenNative.FindLaptopScreen()) > 0;
        public IReadOnlyList<ActiveDisplayInfo> GetConnectedDisplays() => ScreenNative.GetActiveDisplays();
        public bool IsOverdriveEnabled => AppConfig.Is("overdrive");
        public bool IsAutoRefreshEnabled => AppConfig.Is("screen_auto");
        public bool IsHdrEnabled => ScreenCCD.GetHDRStatus(out _, true);
        public bool IsAcmEnabled => ScreenCCD.GetHDRStatus(out bool acm, true) && acm;
        public bool IsMiniLedSupported => Program.acpi.IsSupported(AsusACPI.ScreenMiniled1) || Program.acpi.IsSupported(AsusACPI.ScreenMiniled2);
        public bool IsOverdriveSupported => Program.acpi.IsOverdriveSupported();
        public bool IsOverdriveAvailable => IsOverdriveSupported && !AppConfig.IsNoOverdrive();

        // Both mirror the probes InitScreen runs, so the controls are already in the
        // right state on first render instead of waiting for the first snapshot.
        public bool IsResolutionToggleSupported => AppConfig.IsDUO() && Program.acpi.DeviceGet(AsusACPI.ScreenFHD) >= 0;
        public bool IsHdrControlSupported => IsHdrEnabled && Program.acpi.DeviceGet(AsusACPI.ScreenHDRControl) >= 0;
        public bool HasColorProfiles => VisualControl.GetGamutModes().Count > 0;
        public bool CanInstallColorProfiles => !HasColorProfiles && ColorProfileHelper.ProfileExists();
        public bool IsColorPipelineEnabled => VisualControl.IsColorPipelineEnabled();
        public int MiniLedMode => Program.acpi.DeviceGet(AsusACPI.ScreenMiniled1);
        public int Brightness => VisualControl.GetBrightness();
        public int PanelBrightness
        {
            get { try { return ScreenBrightness.Get(); } catch { return 50; } }
        }
        public int CurrentVisualProfile => AppConfig.Get("visual", (int)VisualControl.GetDefaultVisualMode());
        public int ColorTemperature => AppConfig.Get("color_temp", VisualControl.DefaultColorTemp);
        public int CurrentGamut => AppConfig.Get("gamut", (int)VisualControl.GetDefaultGamut());

        public event Action<ScreenStatusSnapshot>? DisplayStatusChanged;
        public event Action<int>? PanelBrightnessChanged;
        public event Action<bool>? ColorPipelineStateChanged;

        private System.Management.ManagementEventWatcher? _brightnessWatcher;

        public DisplayService()
        {
            ScreenControl.OnScreenVisualise += (snapshot) => DisplayStatusChanged?.Invoke(snapshot);
            StartBrightnessWatcher();
        }

        /// <summary>
        /// WMI raises WmiMonitorBrightnessEvent for every backlight change regardless of
        /// origin, so one subscription keeps this app's sliders in step with the
        /// brightness keys and the Windows slider - and with each other, since our own
        /// writes come back through the same event.
        /// </summary>
        private void StartBrightnessWatcher()
        {
            try
            {
                _brightnessWatcher = new System.Management.ManagementEventWatcher(
                    new System.Management.ManagementScope(@"\\.\root\wmi"),
                    new System.Management.WqlEventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));

                _brightnessWatcher.EventArrived += (_, e) =>
                {
                    try
                    {
                        int level = Convert.ToInt32(e.NewEvent.GetPropertyValue("Brightness"));
                        PanelBrightnessChanged?.Invoke(Math.Clamp(level, 0, 100));
                    }
                    catch (Exception ex) { Logger.WriteLine("Brightness event: " + ex.Message); }
                };

                _brightnessWatcher.Start();
            }
            catch (Exception ex)
            {
                // Not every panel exposes the WMI brightness class. The sliders still
                // work, they just stop mirroring changes made outside the app.
                Logger.WriteLine("Brightness watcher unavailable: " + ex.Message);
            }
        }

        /// <summary>
        /// Stops the WMI sink explicitly. Letting it fall to the finalizer leaves the
        /// subscription registered with the WMI service for as long as it takes to get
        /// there, which outlives the process it was meant to serve.
        /// </summary>
        public void Dispose()
        {
            if (_brightnessWatcher is null) return;

            try { _brightnessWatcher.Stop(); } catch { }
            try { _brightnessWatcher.Dispose(); } catch { }
            _brightnessWatcher = null;
        }

        public void SetRefreshRate(int hz, bool? overdrive = null)
        {
            ScreenControl.SetAutoRefresh(0);

            // The refresh selector now sends an explicit OD choice. Keep the
            // legacy default for callers that only specify a rate: low rate is
            // OD off, high rate is OD on.
            int overdriveValue = overdrive.HasValue
                ? (overdrive.Value ? 1 : 0)
                : (hz > ScreenControl.MIN_RATE ? 1 : 0);
            ScreenControl.SetScreen(hz, overdriveValue);
        }

        public void SetAutoRefresh(bool enabled)
        {
            ScreenControl.SetAutoRefresh(enabled ? 1 : 0);
            if (enabled) ScreenControl.AutoScreen();
        }

        public void SetOverdrive(bool enable)
        {
            // Overdrive on its own: leave the refresh rate to whatever the panel is
            // already running. SetScreen skips the mode change for a negative
            // frequency, so nothing has to be read back and re-applied here.
            ScreenControl.SetScreen(overdrive: enable ? 1 : 0);
        }

        public void ToggleMiniLed()
        {
            ScreenControl.ToogleMiniled();
        }

        public void SetBrightness(int brightness)
        {
            if (!IsColorPipelineEnabled) return;
            VisualControl.SetBrightness(brightness);
        }

        public void SetPanelBrightness(int brightness)
        {
            try { ScreenBrightness.Set(Math.Clamp(brightness, 0, 100)); }
            catch (Exception ex) { Logger.WriteLine("Panel brightness: " + ex.Message); }
        }

        public void SetVisualProfile(int profileId)
        {
            VisualControl.SetVisual((SplendidCommand)profileId, ColorTemperature);
            ColorPipelineStateChanged?.Invoke(IsColorPipelineEnabled);
        }

        public void SetColorTemperature(int temperature)
        {
            if (!IsColorPipelineEnabled) return;
            VisualControl.SetVisual((SplendidCommand)CurrentVisualProfile, temperature);
        }

        public void SetGamut(int gamutId)
        {
            if (!IsColorPipelineEnabled) return;
            VisualControl.SetGamut(gamutId);
        }

        public void ToggleTouchScreen()
        {
            Input.InputDispatcher.ToggleTouchScreen();
        }

        public void SetScreenPadBrightness(int brightness)
        {
            Input.InputDispatcher.SetScreenpad(brightness);
        }

        public void ToggleResolution() => ScreenControl.ToogleFHD();
        public void ToggleHdrControl() => ScreenControl.ToogleHDRControl();
        public async Task<bool> InstallColorProfilesAsync()
        {
            try
            {
                if (HasColorProfiles) return true;
                if (!CanInstallColorProfiles) return false;

                if (ProcessHelper.IsUserAdministrator())
                {
                    await ColorProfileHelper.InstallProfileFilesAsync();
                }
                else
                {
                    using var process = Process.Start(new ProcessStartInfo
                    {
                        FileName = Environment.ProcessPath ?? System.Windows.Forms.Application.ExecutablePath,
                        Arguments = "--install-colors",
                        WorkingDirectory = AppContext.BaseDirectory,
                        UseShellExecute = true,
                        Verb = "runas"
                    });
                    if (process is null) return false;
                    await process.WaitForExitAsync();
                    if (process.ExitCode != 0) return false;
                }
                return HasColorProfiles;
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Color profile install: " + ex.Message);
                return false;
            }
        }
    }
}
