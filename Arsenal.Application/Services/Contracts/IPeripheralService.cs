using Arsenal.Application.Models;
using Arsenal.AutoUpdate;
using Arsenal.Display;

namespace Arsenal.Application.Services.Contracts
{
    public interface IPeripheralService
    {
        IReadOnlyList<PeripheralDeviceModel> Devices { get; }
        event Action DevicesChanged;

        void RefreshDevices();
        void SetDpi(string deviceId, int dpi);
        void SetPollingRate(string deviceId, int rateHz);
        void SetSleepTimeout(string deviceId, int minutes);
        void SetMouseLighting(string deviceId, int zone, int mode, int colorArgb, int brightness, bool randomColor, int speed, int direction);
        void SetKeyboardLighting(string deviceId, int mode, int primaryArgb, int secondaryArgb, int speed, int brightness);
        void SetKeyboardProfile(string deviceId, int profile);
        void SetKeyboardEnergy(string deviceId, int sleepMinutes, int lowBatteryWarningPercent);
        void SetKeyboardOled(string deviceId, bool enabled, int brightness, int mode);

        /// <summary>
        /// The mouse or pad behind a listed device, for the Peripherals page's own
        /// settings, or null once it has gone.
        /// </summary>
        /// <remarks>
        /// The page works on the device itself, the way G-Helper's mouse window does,
        /// rather than through a transfer model per setting: a mouse carries twenty-odd
        /// capabilities, each with its own range and write, and copying every one of
        /// them into a model and back again would only add a place for them to differ.
        /// </remarks>
        Arsenal.Peripherals.Mouse.AsusMouse? FindMouse(string deviceId);

        /// <summary>The keyboard behind a listed device, or null once it has gone.</summary>
        Arsenal.Peripherals.Keyboard.AsusKeyboard? FindKeyboard(string deviceId);
    }
}
