using Arsenal.AutoUpdate;
using Arsenal.Display;

namespace Arsenal.Application.Services.Contracts
{
    public interface IDisplayService
    {
        int CurrentRefreshRate { get; }
        int MaxRefreshRate { get; }
        /// <summary>
        /// False while Windows is not driving the built-in panel - clamshell, "second
        /// screen only", or a topology still settling after a hotplug. The refresh and
        /// overdrive controls write to that panel and nothing else, so they have no
        /// effect while this is false.
        /// </summary>
        bool IsInternalPanelActive { get; }

        /// <summary>
        /// Every display Windows is currently driving and the mode it is running, for
        /// reporting only. Ordered with the built-in panel first.
        /// </summary>
        IReadOnlyList<Arsenal.Display.ActiveDisplayInfo> GetConnectedDisplays();
        bool IsOverdriveEnabled { get; }
        bool IsAutoRefreshEnabled { get; }
        bool IsHdrEnabled { get; }
        bool IsAcmEnabled { get; }
        bool IsMiniLedSupported { get; }
        bool IsOverdriveSupported { get; }
        /// <summary>Overdrive is supported and not switched off in Advanced.</summary>
        bool IsOverdriveAvailable { get; }
        /// <summary>FHD/UHD switching, which only dual-mode panels expose.</summary>
        bool IsResolutionToggleSupported { get; }
        /// <summary>Handing HDR back to Windows, which needs HDR on and the control to answer.</summary>
        bool IsHdrControlSupported { get; }
        bool HasColorProfiles { get; }
        bool CanInstallColorProfiles { get; }
        /// <summary>
        /// False when GameVisual is Disabled. OLED dimming, gamut and white point all
        /// share that ASUS color pipeline and must not silently turn it back on.
        /// </summary>
        bool IsColorPipelineEnabled { get; }
        int MiniLedMode { get; } // 0=Off/Single, 1=MultiZone

        /// <summary>
        /// Extreme Low Motion Blur as the panel reports it: 1 on, 0 off, negative on a
        /// panel without it. Read over the display's AUX channel, so it can be slow.
        /// </summary>
        int ElmbState { get; }
        int Brightness { get; }
        int PanelBrightness { get; }
        int CurrentVisualProfile { get; }
        int ColorTemperature { get; }
        int CurrentGamut { get; }
        event Action<ScreenStatusSnapshot> DisplayStatusChanged;
        event Action<bool> ColorPipelineStateChanged;

        /// <summary>
        /// Raised whenever the display backlight changes, whoever changed it - this
        /// app, the brightness keys, or the Windows slider. Both the main window and
        /// the quick panel follow it so all three stay in step.
        /// </summary>
        event Action<int> PanelBrightnessChanged;

        /// <summary>
        /// Applies a refresh rate and, optionally, an explicit overdrive state.
        /// Pass <see cref="Arsenal.Display.ScreenControl.MAX_REFRESH"/> as <paramref name="hz"/>
        /// to let the panel's live maximum be resolved at write time rather than
        /// relying on a value cached by the caller.
        /// </summary>
        void SetRefreshRate(int hz, bool? overdrive = null);
        void SetAutoRefresh(bool enabled);
        void SetOverdrive(bool enable);
        void ToggleMiniLed();
        void SetElmb(bool enabled);
        void SetBrightness(int brightness);
        void SetPanelBrightness(int brightness);
        void SetVisualProfile(int profileId);
        void SetColorTemperature(int kelvin);
        void SetGamut(int gamutId);
        void ToggleTouchScreen();
        void SetScreenPadBrightness(int brightness);
        void ToggleResolution();
        void ToggleHdrControl();
        Task<bool> InstallColorProfilesAsync();
    }
}
