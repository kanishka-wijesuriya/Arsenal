using Arsenal.Application.Models;
using Arsenal.Application.Services.Contracts;
using Arsenal.Battery;
using Arsenal.Gpu;
using Arsenal.Helpers;
using Arsenal.UI.ViewModels;
using Arsenal.UI.Services;
using Arsenal.UI.Services.Remote;
using Microsoft.Extensions.DependencyInjection;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Arsenal.UI
{
    public partial class App
    {
        public void ShowMode(int mode) => Dispatch(() =>
        {
            if (GetCreatedViewModel<QuickPanelViewModel>() is { } quick) quick.SelectedPerformanceMode = mode;
            if (GetCreatedViewModel<HomeViewModel>() is { } home) home.CurrentPerformanceMode = mode;
            if (GetCreatedViewModel<MainViewModel>() is { } main)
                main.CurrentModeName = Services.GetRequiredService<IPerformanceService>().CurrentModeName;
        });

        public void SetModeLabel(string label)
        {
            _latestModeLabel = label;
            Dispatch(() =>
            {
                if (GetCreatedViewModel<MainViewModel>() is { } main) main.CurrentModeName = label;
            });
        }

        public void VisualiseGPUMode() => Dispatch(() =>
        {
            int mode = Services.GetRequiredService<IGpuService>().CurrentGpuMode;
            if (GetCreatedViewModel<QuickPanelViewModel>() is { } quick) quick.SelectedGpuMode = mode;
            if (GetCreatedViewModel<HomeViewModel>() is { } home) home.CurrentGpuMode = mode;
            if (GetCreatedViewModel<DisplayViewModel>() is { } display) display.CurrentGpuMode = mode;
        });

        public void VisualiseGPUOn() => VisualiseGPUMode();
        public void VisualiseGPUEco() => VisualiseGPUMode();
        public void VisualiseBattery(int limit) => Dispatch(() =>
        {
            if (GetCreatedViewModel<BatteryViewModel>() is { } battery) battery.ChargeLimit = limit;
            if (GetCreatedViewModel<QuickPanelViewModel>() is { } quick) quick.ChargeLimit = limit;
            UpdateTrayTooltip();
        });
        public void VisualiseBatteryFull() => Dispatch(() =>
        {
            if (GetCreatedViewModel<BatteryViewModel>() is { } battery)
                battery.IsFullChargeOverride = BatteryControl.chargeFull;
            UpdateTrayTooltip();
        });
        public void VisualiseScreen(bool screenEnabled, bool screenAuto, int frequency, int maxFrequency, int overdrive, bool overdriveSetting, int miniled1, int miniled2, bool hdr, bool acm, int fhd, int hdrControl) => Dispatch(() =>
        {
            if (GetCreatedViewModel<DisplayViewModel>() is { } display)
            {
                display.CurrentRefreshRate = frequency;
                display.MaxRefreshRate = maxFrequency;
                display.IsInternalPanelActive = screenEnabled;
                display.IsAutoRefresh = screenAuto;
                display.IsOverdrive = overdrive > 0;
                display.IsOverdriveAvailable = overdriveSetting;
                display.IsMiniLed = miniled1 > 0 || miniled2 > 0;
                display.IsHdr = hdr;
                display.IsResolutionToggleSupported = fhd >= 0;
                display.IsHdrControlSupported = hdr && hdrControl >= 0;
            }

            if (GetCreatedViewModel<QuickPanelViewModel>() is { } quick)
            {
                quick.RefreshRate = frequency;
                quick.IsOverdrive = overdrive > 0;
                quick.IsOverdriveAvailable = overdriveSetting;
                quick.IsAutoRefresh = screenAuto;
                quick.IsMiniLed = miniled1 > 0 || miniled2 > 0;
            }
        });
        public void VisualiseUpdates(string text)
        {
            _latestUpdateStatus = text;
            Dispatch(() =>
            {
                if (GetCreatedViewModel<AboutViewModel>() is { } about) about.UpdateStatus = text;
            });
        }
        public void VisualiseArmoury(bool running)
        {
            _latestAsusOptimizationState = running;
            Dispatch(() =>
            {
                if (GetCreatedViewModel<AdvancedViewModel>() is { } advanced)
                    advanced.AsusOptimizationRunning = running;
            });
        }
        public void FansInit() => Dispatch(() => GetCreatedViewModel<PerformanceViewModel>()?.LoadCurrentProfile());
        public void GPUInit() => VisualiseGPUMode();
        public void LabelFansResult(string label)
        {
            _latestCalibrationStatus = label;
            Dispatch(() =>
            {
                if (GetCreatedViewModel<PerformanceViewModel>() is { } performance)
                    performance.CalibrationStatus = label;
            });
        }
        public bool ConfirmGpuModeChange(
            int currentMode,
            int targetMode,
            GpuModeChangeConfirmation confirmation)
        {
            GpuModeConfirmationContent content = GpuModeConfirmation.CreateContent(targetMode, confirmation);

            // When the phone asked for the switch, the phone is what must answer. A modal
            // on the PC would be invisible to whoever pressed the button, and would hold
            // the request open until somebody walked over to the machine.
            if (RemoteRestartPrompt.IsCompanionCommand)
            {
                return RemoteRestartPrompt.Ask(
                    content.Title,
                    content.Body,
                    content.Confirm,
                    "Cancel");
            }

            bool result = false;
            if (Dispatcher.CheckAccess())
            {
                _quickPanelWindow?.HideForModal();
                result = GpuModeConfirmation.Show(_mainWindow, targetMode, confirmation);
            }
            else
                Dispatcher.Invoke(() =>
                {
                    _quickPanelWindow?.HideForModal();
                    result = GpuModeConfirmation.Show(_mainWindow, targetMode, confirmation);
                });
            return result;
        }
        public void ShowToast(string text, ToastIcon icon, string? detail = null) => Dispatch(() =>
        {
            ToastManager.Show(text, icon, detail);
        });

        public void RunOnUi(Action action) => Dispatch(action);

        private void Dispatch(Action action)
        {
            if (Dispatcher.CheckAccess()) action();
            else Dispatcher.BeginInvoke(action);
        }
    }
}
