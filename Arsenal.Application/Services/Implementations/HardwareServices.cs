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

    public class BatteryService : IBatteryService
    {
        public int ChargeLimit => AppConfig.Get("charge_limit", 80);
        public bool IsFullChargeOverride => BatteryControl.chargeFull;
        public float DischargeRateWatts => (float)(HardwareControl.batteryRate ?? 0);
        public int BatteryPercent => (int)Math.Round((float)System.Windows.Forms.SystemInformation.PowerStatus.BatteryLifePercent * 100);

        public event Action<int>? ChargeLimitChanged;
        public event Action<bool>? FullChargeOverrideChanged;

        public BatteryService()
        {
            BatteryControl.OnBatteryLimitChanged += (limit) => ChargeLimitChanged?.Invoke(limit);
            BatteryControl.OnBatteryFullChanged += (full) => FullChargeOverrideChanged?.Invoke(full);
        }

        public void SetChargeLimit(int limitPercent)
        {
            BatteryControl.SetBatteryChargeLimit(limitPercent);
        }

        public void ToggleFullChargeOverride()
        {
            BatteryControl.ToggleBatteryLimitFull();
        }

        public void GenerateBatteryReport()
        {
            BatteryControl.BatteryReport();
        }

        public Task<Arsenal.Battery.BatteryReportData?> BuildBatteryReportAsync(CancellationToken cancel = default)
            => Arsenal.Battery.BatteryReportReader.GenerateAsync(cancel);
    }

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

    public class LightingService : ILightingService
    {
        public int Brightness => Input.InputDispatcher.GetBacklight();
        public int CurrentMode => AppConfig.Get("aura_mode", 0);
        public bool HasAnimeMatrix => AppConfig.IsAnimeMatrix();
        public bool HasSlash => AppConfig.IsSlash();
        public bool HasKeyboardColor => !Arsenal.USB.Aura.isWhite;
        public bool HasAuraEffects => !AppConfig.NoAura();

        public int BacklightZoneType
        {
            get
            {
                var detected = Arsenal.USB.Aura.BacklightType;

                if (detected != Arsenal.USB.AuraBacklightType.Unknown) return (int)detected;

                // The probe needs the keyboard to answer. Until it has - and on a
                // machine whose keyboard never answers - fall back to what ASUS
                // recorded about this SKU, and to the plain single-zone backlight only
                // when there is nothing at all to go on. Promising per-key colour the
                // machine cannot produce is the worse way to be wrong.
                var recorded = Arsenal.Peripherals.Keyboard.AsusMachineInfo.RecordedBacklightType;
                return (int)(recorded == Arsenal.USB.AuraBacklightType.Unknown
                    ? Arsenal.USB.AuraBacklightType.SingleZone
                    : recorded);
            }
        }

        public bool HasLightbar => Arsenal.USB.Aura.HasLightbar;
        public int MatrixBrightness => AppConfig.Get("matrix_brightness", 0);
        public int MatrixMode => AppConfig.Get("matrix_running", 0);

        public event Action<int>? BrightnessChanged;
        public event Action<int>? ModeChanged;

        public LightingService()
        {
            Input.InputDispatcher.OnBacklightChanged += (b) => BrightnessChanged?.Invoke(b);
        }

        public void SetBrightness(int level)
        {
            int target = Math.Clamp(level, 0, AppConfig.Get("max_brightness", 3));
            int current = Input.InputDispatcher.GetBacklight();
            Input.InputDispatcher.SetBacklight(target - current, true);
        }

        public void CycleBrightness(int delta = 1)
        {
            Input.InputDispatcher.SetBacklight(delta);
        }

        public void SetMode(int mode)
        {
            AppConfig.Set("aura_mode", mode);
            Aura.ApplyAura();
            ModeChanged?.Invoke(mode);
        }

        public void SetColor(byte r, byte g, byte b)
        {
            AppConfig.Set("aura_color", (r << 16) | (g << 8) | b);
            Aura.ApplyAura();
        }

        public void SetSpeed(int speed)
        {
            AppConfig.Set("aura_speed", speed);
            Aura.ApplyAura();
        }

        public void SetAwake(bool enabled) { AppConfig.Set("keyboard_awake", enabled ? 1 : 0); Aura.ApplyPower(); }
        public void SetBoot(bool enabled) { AppConfig.Set("keyboard_boot", enabled ? 1 : 0); Aura.ApplyPower(); }
        public void SetSleep(bool enabled) { AppConfig.Set("keyboard_sleep", enabled ? 1 : 0); Aura.ApplyPower(); }
        public void SetShutdown(bool enabled) { AppConfig.Set("keyboard_shutdown", enabled ? 1 : 0); Aura.ApplyPower(); }

        public void SetMatrixBrightness(int level)
        {
            AppConfig.Set("matrix_brightness", Math.Clamp(level, 0, 3));
            Program.matrixControl?.SetDevice();
        }

        public void SetMatrixMode(int mode)
        {
            AppConfig.Set("matrix_running", mode);
            Program.matrixControl?.SetDevice();
        }

        public void SetMatrixPowerPolicy(bool disableOnBattery, bool disableWithLidClosed)
        {
            AppConfig.Set("matrix_auto", disableOnBattery ? 1 : 0);
            AppConfig.Set("matrix_lid", disableWithLidClosed ? 1 : 0);
            try
            {
                Program.matrixControl?.deviceSlash?.SetLightingOnBattery(!disableOnBattery);
                Program.matrixControl?.deviceSlash?.SetLightingOnLidClose(!disableWithLidClosed);
            }
            catch (Exception ex) { Logger.WriteLine("Slash power policy: " + ex.Message); }
            Program.matrixControl?.SetDevice();
        }
    }

    public class PeripheralService : IPeripheralService
    {
        private readonly object _devicesGate = new();
        private readonly List<PeripheralDeviceModel> _devices = new();
        private readonly Dictionary<string, AsusMouse> _mice = new();
        private readonly Dictionary<string, AsusKeyboard> _keyboards = new();
        public IReadOnlyList<PeripheralDeviceModel> Devices
        {
            get { lock (_devicesGate) return _devices.ToArray(); }
        }

        public event Action? DevicesChanged;

        public PeripheralService()
        {
            PeripheralsProvider.OnPeripheralsChanged += RefreshDevices;
            RefreshDevices();
        }

        public void RefreshDevices()
        {
            var devices = new List<PeripheralDeviceModel>();
            var mice = new Dictionary<string, AsusMouse>();
            var keyboards = new Dictionary<string, AsusKeyboard>();
            foreach (var dev in PeripheralsProvider.SnapshotMice())
            {
                string id = RuntimeHelpers.GetHashCode(dev).ToString();
                mice[id] = dev;
                var dpiList = new List<int>();
                if (dev.DpiSettings != null)
                {
                    foreach (var dpi in dev.DpiSettings)
                        dpiList.Add((int)dpi.DPI);
                }

                devices.Add(new PeripheralDeviceModel
                {
                    Id = id,
                    Name = dev.GetDisplayName(),
                    DeviceType = "Mouse",
                    BatteryPercentage = dev.Battery,
                    IsCharging = dev.Charging,
                    IsConnected = dev.IsDeviceReady,
                    HasBattery = dev.HasBattery(),
                    CurrentDpi = dpiList.Count > 0 ? dpiList[0] : 800,
                    PollingRate = PollingRateToHz(dev.PollingRate),
                    DpiProfiles = dpiList,
                    MinDpi = dev.MinDPI(),
                    MaxDpi = dev.MaxDPI(),
                    DpiStep = Math.Max(1, dev.DPIIncrements()),
                    PollingRates = dev.CanSetPollingRate()
                        ? dev.SupportedPollingrates().Select(PollingRateToHz).Where(hz => hz > 0).Distinct().Order().ToList()
                        : new List<int>(),
                    SleepMinutes = PowerOffToMinutes(dev.PowerOffSetting),
                    LowBatteryWarningPercent = dev.LowBatteryWarning
                });
            }

            foreach (var keyboard in PeripheralsProvider.SnapshotKeyboards())
            {
                string id = RuntimeHelpers.GetHashCode(keyboard).ToString();
                keyboards[id] = keyboard;
                bool hasOled = keyboard is Azoth;
                int oledMode = 0;
                int oledAnimationCount = 0;
                bool oledEnabled = false;
                int oledBrightness = 100;
                bool oledClock = false;
                if (keyboard is Azoth oled)
                {
                    oledAnimationCount = oled.OledAnimationCount();
                    oledEnabled = oled.OledEnabled;
                    oledBrightness = oled.OledBrightness < 0 ? 100 : oled.OledBrightness;
                    oledClock = oled.OledClock;
                    oledMode = !oledEnabled ? 0 : oledClock ? oledAnimationCount + 1 : Math.Max(1, oled.OledAnimation + 1);
                }

                devices.Add(new PeripheralDeviceModel
                {
                    Id = id,
                    Name = keyboard.GetDisplayName(),
                    DeviceType = "Keyboard",
                    BatteryPercentage = keyboard.Battery,
                    IsCharging = keyboard.Charging,
                    IsConnected = keyboard.IsDeviceReady,
                    HasBattery = keyboard.HasBattery(),
                    HasKeyboardLighting = keyboard.HasRGB(),
                    HasKeyboardPower = keyboard.HasAutoPowerOff(),
                    LowBatteryWarningMaximum = keyboard.LowBatteryWarningMax(),
                    LowBatteryWarningStep = keyboard.LowBatteryWarningStep(),
                    SleepMinutes = PowerOffToMinutes(keyboard.PowerOffSetting),
                    LowBatteryWarningPercent = keyboard.LowBatteryWarning,
                    LightingModes = keyboard.SupportedLightingModes()
                        .Select(mode => new PeripheralOptionModel { Value = (int)mode, Label = KeyboardModeLabel(mode) })
                        .ToList(),
                    LightingMode = (int)keyboard.StoredMode,
                    LightingBrightness = keyboard.StoredBrightness,
                    MaxLightingBrightness = keyboard.MaxBrightness(),
                    LightingSpeed = (int)keyboard.StoredSpeed,
                    PrimaryColorArgb = keyboard.StoredColor.ToArgb(),
                    SecondaryColorArgb = keyboard.StoredColor2.ToArgb(),
                    HasProfiles = keyboard.HasProfiles(),
                    Profile = keyboard.Profile,
                    ProfileCount = keyboard.ProfileCount(),
                    HasKeyboardOled = hasOled,
                    KeyboardOledEnabled = oledEnabled,
                    KeyboardOledBrightness = oledBrightness,
                    KeyboardOledMode = oledMode,
                    KeyboardOledAnimationCount = oledAnimationCount,
                    KeyboardOledClock = oledClock
                });
            }

            lock (_devicesGate)
            {
                _devices.Clear();
                _devices.AddRange(devices);
                _mice.Clear();
                foreach (var pair in mice) _mice[pair.Key] = pair.Value;
                _keyboards.Clear();
                foreach (var pair in keyboards) _keyboards[pair.Key] = pair.Value;
            }
            DevicesChanged?.Invoke();
        }

        public void SetDpi(string deviceId, int dpi)
        {
            AsusMouse? mouse;
            lock (_devicesGate) _mice.TryGetValue(deviceId, out mouse);
            if (mouse is null || mouse.DpiSettings.Length == 0) return;
            dpi = Math.Clamp(dpi, mouse.MinDPI(), mouse.MaxDPI());
            var current = mouse.DpiSettings[0] ?? new AsusMouseDPI();
            current.DPI = (uint)dpi;
            mouse.SetDPIForProfile(current, 1);
            RefreshDevices();
        }

        public void SetPollingRate(string deviceId, int rateHz)
        {
            AsusMouse? mouse;
            lock (_devicesGate) _mice.TryGetValue(deviceId, out mouse);
            if (mouse is null) return;
            var rate = mouse.SupportedPollingrates()
                .OrderBy(candidate => Math.Abs(PollingRateToHz(candidate) - rateHz))
                .FirstOrDefault();
            mouse.SetPollingRate(rate);
            RefreshDevices();
        }

        public void SetSleepTimeout(string deviceId, int minutes)
        {
            AsusMouse? mouse;
            lock (_devicesGate) _mice.TryGetValue(deviceId, out mouse);
            if (mouse is null || !mouse.HasAutoPowerOff()) return;
            var setting = minutes switch
            {
                <= 1 => PowerOffSetting.Minutes1,
                2 => PowerOffSetting.Minutes2,
                3 or 4 => PowerOffSetting.Minutes3,
                <= 7 => PowerOffSetting.Minutes5,
                <= 30 => PowerOffSetting.Minutes10,
                _ => PowerOffSetting.Never
            };
            mouse.SetEnergySettings(mouse.LowBatteryWarning, setting);
            RefreshDevices();
        }

        public void SetKeyboardLighting(string deviceId, int mode, int primaryArgb, int secondaryArgb, int speed, int brightness)
        {
            AsusKeyboard? keyboard;
            lock (_devicesGate) _keyboards.TryGetValue(deviceId, out keyboard);
            if (keyboard is null || !keyboard.HasRGB()) return;

            var lightingMode = (KeyboardLightingMode)mode;
            if (!keyboard.SupportedLightingModes().Contains(lightingMode)) return;
            var primary = System.Drawing.Color.FromArgb(primaryArgb);
            var secondary = System.Drawing.Color.FromArgb(secondaryArgb);
            var auraSpeed = Enum.IsDefined(typeof(AuraSpeed), speed) ? (AuraSpeed)speed : AuraSpeed.Normal;
            int level = Math.Clamp(brightness, 0, keyboard.MaxBrightness());

            keyboard.StoreLighting(lightingMode, primary, secondary, keyboard.StoredColor3, auraSpeed, level);
            if (keyboard.ApplyLighting(lightingMode, primary, secondary, auraSpeed, level))
                keyboard.SaveLighting();
            RefreshDevices();
        }

        public void SetKeyboardProfile(string deviceId, int profile)
        {
            AsusKeyboard? keyboard;
            lock (_devicesGate) _keyboards.TryGetValue(deviceId, out keyboard);
            if (keyboard is null || !keyboard.SetProfile(profile)) return;
            keyboard.ReadProfile();
            RefreshDevices();
        }

        public void SetKeyboardEnergy(string deviceId, int sleepMinutes, int lowBatteryWarningPercent)
        {
            AsusKeyboard? keyboard;
            lock (_devicesGate) _keyboards.TryGetValue(deviceId, out keyboard);
            if (keyboard is null || !keyboard.HasAutoPowerOff()) return;
            int warning = Math.Clamp(lowBatteryWarningPercent, 0, keyboard.LowBatteryWarningMax());
            int step = Math.Max(1, keyboard.LowBatteryWarningStep());
            warning = (int)Math.Round(warning / (double)step) * step;
            keyboard.SetEnergySettings(warning, MinutesToPowerOff(sleepMinutes));
            RefreshDevices();
        }

        public void SetKeyboardOled(string deviceId, bool enabled, int brightness, int mode)
        {
            AsusKeyboard? keyboard;
            lock (_devicesGate) _keyboards.TryGetValue(deviceId, out keyboard);
            if (keyboard is not Azoth oled) return;

            int count = oled.OledAnimationCount();
            oled.SetOledBrightness(Math.Clamp(brightness, 0, 100));
            if (!enabled || mode <= 0)
            {
                oled.SetOledClock(false);
                oled.SetOledEnabled(false);
            }
            else
            {
                oled.SetOledEnabled(true);
                bool clock = mode > count;
                oled.SetOledClock(clock);
                if (!clock) oled.SetOledAnimation(Math.Clamp(mode - 1, 0, count - 1));
            }
            RefreshDevices();
        }

        private static int PollingRateToHz(PollingRate rate) => rate switch
        {
            PollingRate.PR125Hz => 125,
            PollingRate.PR250Hz => 250,
            PollingRate.PR500Hz => 500,
            PollingRate.PR1000Hz => 1000,
            PollingRate.PR2000Hz => 2000,
            PollingRate.PR4000Hz => 4000,
            PollingRate.PR8000Hz => 8000,
            PollingRate.PR16000Hz => 16000,
            _ => 1000
        };

        private static int PowerOffToMinutes(PowerOffSetting setting) => setting switch
        {
            PowerOffSetting.Minutes1 => 1,
            PowerOffSetting.Minutes2 => 2,
            PowerOffSetting.Minutes3 => 3,
            PowerOffSetting.Minutes5 => 5,
            PowerOffSetting.Minutes10 => 10,
            _ => 0
        };

        private static PowerOffSetting MinutesToPowerOff(int minutes) => minutes switch
        {
            <= 1 => PowerOffSetting.Minutes1,
            2 => PowerOffSetting.Minutes2,
            3 or 4 => PowerOffSetting.Minutes3,
            <= 7 => PowerOffSetting.Minutes5,
            <= 30 => PowerOffSetting.Minutes10,
            _ => PowerOffSetting.Never
        };

        private static string KeyboardModeLabel(KeyboardLightingMode mode) => mode switch
        {
            KeyboardLightingMode.ColorCycle => "Color cycle",
            KeyboardLightingMode.StarryNight => "Starry night",
            KeyboardLightingMode.RainDrop => "Raindrop",
            KeyboardLightingMode.Direct => "Per-key",
            _ => System.Text.RegularExpressions.Regex.Replace(mode.ToString(), "([a-z])([A-Z])", "$1 $2")
        };
    }

    /// <summary>
    /// Exposes the same built-in input-device identities used by the legacy app without
    /// making the Quick Panel construct the much heavier external-peripheral service.
    /// </summary>
    public sealed class InputDeviceService : IInputDeviceService
    {
        public bool HasTouchScreen => TouchscreenHelper.IsAvailable();
        public bool HasTouchpad => Input.InputDispatcher.IsTouchpadAvailable();
        public bool IsTouchpadEnabled => HasTouchpad && Input.InputDispatcher.GetTouchpadState();

        public event Action<bool>? TouchpadStateChanged;

        public InputDeviceService()
        {
            Input.InputDispatcher.OnTouchpadChanged += enabled => TouchpadStateChanged?.Invoke(enabled);
        }

        public void ToggleTouchpad()
        {
            if (!HasTouchpad) return;

            // The legacy path waits briefly for Windows to update the registry state.
            // Keep that wait off WPF's dispatcher so the panel stays responsive.
            _ = Task.Run(() =>
            {
                try { Input.InputDispatcher.ToggleTouchpadEvent(); }
                catch (Exception ex) { Logger.WriteLine("Touchpad toggle: " + ex.Message); }
            });
        }
    }
}
