using Arsenal.Application.Models;
using Arsenal.AutoUpdate;
using Arsenal.Display;

namespace Arsenal.Application.Services.Contracts
{
    /// <summary>Built-in pointer and touch devices exposed by the laptop itself.</summary>
    public interface IInputDeviceService
    {
        bool HasTouchScreen { get; }
        bool HasTouchpad { get; }
        bool IsTouchpadEnabled { get; }
        event Action<bool> TouchpadStateChanged;

        void ToggleTouchpad();
    }
}
