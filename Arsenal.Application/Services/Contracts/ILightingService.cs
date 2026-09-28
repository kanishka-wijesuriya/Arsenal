using Arsenal.AutoUpdate;
using Arsenal.Display;

namespace Arsenal.Application.Services.Contracts
{
    public interface ILightingService
    {
        int Brightness { get; }
        int CurrentMode { get; }
        bool HasAnimeMatrix { get; }
        bool HasSlash { get; }

        /// <summary>
        /// False on white-only backlights, where a colour is accepted and discarded.
        /// The flag is also corrected at runtime from the keyboard's own feature bytes,
        /// so it covers models the name list does not know about.
        /// </summary>
        bool HasKeyboardColor { get; }

        /// <summary>False where the keyboard exposes no selectable Aura effects.</summary>
        bool HasAuraEffects { get; }

        /// <summary>
        /// What the backlight can actually show, as an <c>AuraBacklightType</c>: one
        /// colour for the whole keyboard, four zones, or a colour per key. The preview
        /// draws only what the hardware can do rather than what the effect could look
        /// like on better hardware.
        /// </summary>
        int BacklightZoneType { get; }

        /// <summary>True where the machine has the front-edge light bar.</summary>
        bool HasLightbar { get; }
        int MatrixBrightness { get; }
        int MatrixMode { get; }
        event Action<int> BrightnessChanged;
        event Action<int> ModeChanged;

        void SetBrightness(int level);
        void CycleBrightness(int delta = 1);
        void SetMode(int mode);
        void SetColor(byte r, byte g, byte b);
        void SetSpeed(int speed);
        void SetAwake(bool enabled);
        void SetBoot(bool enabled);
        void SetSleep(bool enabled);
        void SetShutdown(bool enabled);
        void SetMatrixBrightness(int level);
        void SetMatrixMode(int mode);
        void SetMatrixPowerPolicy(bool disableOnBattery, bool disableWithLidClosed);
    }
}
