using Arsenal.Ally;
using Arsenal.AnimeMatrix;
using Arsenal.Battery;
using Arsenal.Display;
using Arsenal.Gpu;
using Arsenal.Helpers;
using Arsenal.Input;
using Arsenal.Mode;
using Arsenal.Overlay;
using Arsenal.Peripherals;
using Arsenal.USB;
using System.Diagnostics;
using System.Globalization;

namespace Arsenal
{
    public interface IUiBridge
    {
        void ShowToast(string message, ToastIcon icon = ToastIcon.Charger, string? detail = null);
        void VisualiseBattery(int limit);
        void VisualiseBatteryFull();
        void VisualiseGPUMode();
        void VisualiseGPUOn();
        void VisualiseGPUEco();
        void VisualiseUpdates(string version);
        void VisualiseArmoury(bool running);
        void ShowMode(int mode);
        void SetModeLabel(string label);
        void FansInit();
        void GPUInit();
        void LabelFansResult(string result);
        bool ConfirmGpuModeRestart(int currentMode, int targetMode);
        void RunOnUi(Action action);
    }

    public static class Program
    {
        public static AsusACPI acpi { get; set; } = default!;
        public static ModeControl modeControl { get; set; } = default!;
        public static GPUModeControl gpuControl { get; set; } = default!;
        public static AllyControl allyControl { get; set; } = default!;
        public static ClamshellModeControl clamshellControl { get; set; } = default!;
        public static InputDispatcher? inputDispatcher { get; set; }
        public static AniMatrixControl? matrixControl { get; set; }
        public static HardwareOverlay? hardwareOverlay { get; set; }
        public static ToastForm toast { get; set; } = default!;

        public static IUiBridge? Bridge { get; set; }

        public enum PowerSource { Battery, Barrel, USBC }
        public static PowerSource currentSource = PowerSource.Battery;

        public static bool usbcProfile = AppConfig.Is("usbc_profile");

        public static int PerformanceKey() =>
            usbcProfile ? (int)ReadPowerSource() : (int)SystemInformation.PowerStatus.PowerLineStatus;

        public static PowerSource ReadPowerSource()
        {
            if (SystemInformation.PowerStatus.PowerLineStatus != PowerLineStatus.Online)
                return PowerSource.Battery;

            int chargerMode = acpi?.DeviceGet(AsusACPI.ChargerMode) ?? 0;
            if (chargerMode > 0 && (chargerMode & AsusACPI.ChargerBarrel) == 0)
                return PowerSource.USBC;

            return PowerSource.Barrel;
        }

        public static void InitCoreHardware()
        {
            acpi = new AsusACPI();
            toast = new ToastForm();
            modeControl = new ModeControl();
            gpuControl = new GPUModeControl();
            allyControl = new AllyControl();
            clamshellControl = new ClamshellModeControl();
            inputDispatcher = new InputDispatcher();
            matrixControl = new AniMatrixControl();
            hardwareOverlay = new HardwareOverlay();

            Modes.InitFullSpeed();
            HardwareControl.RecreateGpuControl();
        }
    }
}
