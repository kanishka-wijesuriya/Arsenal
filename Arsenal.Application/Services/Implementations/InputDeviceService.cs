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
