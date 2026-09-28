using Arsenal.Application.Services.Contracts;
using Arsenal.Display;
using Arsenal.Fan;
using Arsenal.Helpers;
using Arsenal.Peripherals;
using Arsenal.Peripherals.Keyboard;
using Arsenal.Peripherals.Keyboard.Models;
using Arsenal.Peripherals.Mouse;
using Arsenal.USB;
using System.Diagnostics;

namespace Arsenal.Application.Services.Implementations
{
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
}
