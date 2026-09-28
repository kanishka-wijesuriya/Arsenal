using Arsenal.Application.Models;
using Arsenal.Application.Services.Contracts;
using Arsenal.Display;
using Arsenal.Fan;
using Arsenal.Helpers;
using Arsenal.Peripherals;
using Arsenal.Peripherals.Keyboard;
using Arsenal.Peripherals.Keyboard.Models;
using Arsenal.Peripherals.Mouse;
using Arsenal.USB;
using System.Runtime.CompilerServices;

namespace Arsenal.Application.Services.Implementations
{
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
                // A mouse being read again clears its stages before reading them, and the
                // battery it reports first raises this refresh in between. A stage not
                // read yet is skipped rather than dereferenced: the exception came back
                // through the battery event and abandoned the read half done.
                if (dev.DpiSettings != null)
                {
                    foreach (var dpi in dev.DpiSettings)
                        if (dpi is not null) dpiList.Add((int)dpi.DPI);
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
                    HasMousePerformance = dev.DPIProfileCount() > 0,
                    HasMouseLighting = dev.HasRGB(),
                    MaxMouseLightingBrightness = dev.MaxBrightness(),
                    MouseLightingZones = dev.HasRGB() ? MouseLightingZones(dev) : new List<MouseLightingZoneModel>(),
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

        public void SetMouseLighting(string deviceId, int zone, int mode, int colorArgb, int brightness, bool randomColor, int speed, int direction)
        {
            AsusMouse? mouse;
            lock (_devicesGate) _mice.TryGetValue(deviceId, out mouse);
            if (mouse is null || !mouse.HasRGB() || !mouse.IsDeviceReady) return;

            var lightingZone = (LightingZone)zone;
            if (lightingZone != LightingZone.All && !mouse.SupportedLightingZones().Contains(lightingZone)) return;
            var lightingMode = (LightingMode)mode;
            if (!mouse.IsLightingModeSupported(lightingMode) || !mouse.IsLightingModeSupportedForZone(lightingMode, lightingZone)) return;

            // A fresh setting rather than the cached one: SetLightingSetting shares a single
            // instance across every zone when writing All, so editing the cache in place
            // would change the other zones' readouts before the write had happened.
            var current = mouse.LightingSettingForZone(lightingZone) ?? new LightingSetting();
            var setting = new LightingSetting
            {
                LightingMode = lightingMode,
                Brightness = Math.Clamp(brightness, 0, mouse.MaxBrightness()),
                RGBColor = mouse.SupportsColorSetting(lightingMode) ? System.Drawing.Color.FromArgb(colorArgb) : current.RGBColor,
                RandomColor = mouse.SupportsRandomColor(lightingMode) && randomColor,
                AnimationSpeed = SpeedFromIndex(speed),
                AnimationDirection = direction == 1 ? AnimationDirection.CounterClockwise : AnimationDirection.Clockwise,
            };

            mouse.SetLightingSetting(setting, lightingZone);
            RefreshDevices();
        }

        private static List<MouseLightingZoneModel> MouseLightingZones(AsusMouse mouse)
        {
            var zones = new List<MouseLightingZoneModel> { MouseLightingZone(mouse, LightingZone.All) };
            LightingZone[] supported = mouse.SupportedLightingZones();
            if (supported.Length > 1)
                zones.AddRange(supported.Select(zone => MouseLightingZone(mouse, zone)));
            return zones;
        }

        private static MouseLightingZoneModel MouseLightingZone(AsusMouse mouse, LightingZone zone)
        {
            LightingSetting setting = mouse.LightingSettingForZone(zone) ?? new LightingSetting();
            return new MouseLightingZoneModel
            {
                Zone = (int)zone,
                Label = MouseZoneLabel(zone),
                Modes = Enum.GetValues<LightingMode>()
                    .Where(mode => mouse.IsLightingModeSupported(mode) && mouse.IsLightingModeSupportedForZone(mode, zone))
                    .Select(mode => new MouseLightingModeOption
                    {
                        Value = (int)mode,
                        Label = MouseModeLabel(mode),
                        HasColor = mouse.SupportsColorSetting(mode),
                        HasRandomColor = mouse.SupportsRandomColor(mode),
                        HasSpeed = mouse.SupportsAnimationSpeed(mode),
                        HasDirection = mouse.SupportsAnimationDirection(mode),
                    })
                    .ToList(),
                Mode = (int)setting.LightingMode,
                ColorArgb = setting.RGBColor.ToArgb(),
                Brightness = Math.Clamp(setting.Brightness, 0, mouse.MaxBrightness()),
                RandomColor = setting.RandomColor,
                Speed = setting.AnimationSpeed switch { AnimationSpeed.Slow => 0, AnimationSpeed.Fast => 2, _ => 1 },
                Direction = setting.AnimationDirection == AnimationDirection.CounterClockwise ? 1 : 0,
            };
        }

        private static AnimationSpeed SpeedFromIndex(int index) => index switch
        {
            0 => AnimationSpeed.Slow,
            2 => AnimationSpeed.Fast,
            _ => AnimationSpeed.Medium
        };

        private static string MouseZoneLabel(LightingZone zone) => zone switch
        {
            LightingZone.All => "All zones",
            LightingZone.Scrollwheel => "Scroll wheel",
            _ => zone.ToString()
        };

        private static string MouseModeLabel(LightingMode mode) => mode switch
        {
            LightingMode.ColorCycle => "Color cycle",
            LightingMode.BatteryState => "Battery level",
            _ => mode.ToString()
        };

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

        public AsusMouse? FindMouse(string deviceId)
        {
            lock (_devicesGate) return _mice.TryGetValue(deviceId, out AsusMouse? mouse) ? mouse : null;
        }

        public AsusKeyboard? FindKeyboard(string deviceId)
        {
            lock (_devicesGate) return _keyboards.TryGetValue(deviceId, out AsusKeyboard? keyboard) ? keyboard : null;
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
}
