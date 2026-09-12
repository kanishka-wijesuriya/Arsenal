using Arsenal.Application.Models;
using Arsenal.Display;

namespace Arsenal.Application.Services.Contracts
{
    public interface IPerformanceService
    {
        int CurrentMode { get; }
        string CurrentModeName { get; }
        event Action<int> ModeChanged;
        event Action<string> ModeLabelChanged;

        /// <summary>
        /// Applies a user-selected performance profile. Manual selections notify by
        /// default so every presentation surface provides the same immediate feedback.
        /// </summary>
        void SetMode(int modeIndex, bool notify = true);
        void CycleMode(bool backward = false);
        PerformanceProfile GetCurrentProfile();
        void SaveProfile(PerformanceProfile profile);
        void ResetProfile(int modeIndex);
        bool IsCpuBoostSupported { get; }
        bool IsRyzenSmuSupported { get; }
        bool IsIntelMsrSupported { get; }
        /// <summary>
        /// Curve Optimizer undervolting, which needs PawnIO present and one of the
        /// CPUs the original allow-lists. Writes are dropped for anything else, so
        /// the sliders are hidden rather than shown doing nothing.
        /// </summary>
        bool IsUndervoltSupported { get; }
        /// <summary>The integrated-GPU offset, which is narrower still.</summary>
        bool IsIgpuUndervoltSupported { get; }
        void ApplyPowerLimits(int spl, int sppt, int fppt);
        void ApplyUndervolt(int cpuUvMv, int igpuUvMv);

        /// <summary>
        /// SPL, SPPT and FPPT all share one ceiling, because that is what the writer
        /// checks all three against. Varies by chassis - 50 W on an Ally, 250 W on an
        /// Advantage Edition - so it cannot be a constant in a view.
        /// </summary>
        ControlRange PowerLimitRange { get; }

        /// <summary>Throttle temperature this CPU will take, not a generic 65-105.</summary>
        ControlRange CpuTempRange { get; }

        /// <summary>Curve Optimizer offset, in millivolts and always negative.</summary>
        ControlRange CpuUndervoltRange { get; }

        /// <summary>The integrated-GPU offset, narrower than the CPU one.</summary>
        ControlRange IgpuUndervoltRange { get; }

        /// <summary>Fan hysteresis in degrees. Zero means "leave the firmware alone".</summary>
        ControlRange FanHysteresisRange { get; }
    }

    public interface IGpuService
    {
        int CurrentGpuMode { get; }
        bool IsEcoSupported { get; }
        bool IsMuxSupported { get; }

        /// <summary>
        /// Whether this machine has a discrete GPU at all. Distinct from Eco and MUX
        /// support: a dedicated GPU that can be neither switched off nor multiplexed
        /// still exists, still draws power and still deserves its clock controls.
        /// </summary>
        bool HasDedicatedGpu { get; }
        bool IsXgmConnected { get; }
        event Action<int> GpuModeChanged;
        event Action<string?> GpuLockStatusChanged;
        /// <summary>Busy for the whole span of a GPU switch, with a stage message.</summary>
        event Action<bool, string?> GpuBusyChanged;

        void SetGpuMode(int mode, int auto = 0);
        void SetGpuClocks(int coreOffsetMhz, int memoryOffsetMhz);
        void SetGpuPower(int dynamicBoostW, int tempTargetC, int powerTargetW);
        void ToggleXgm();
        void KillGpuApps();
        void RestartNvServices();

        /// <summary>Core clock offset this GPU accepts - wider on the newer parts.</summary>
        ControlRange GpuCoreOffsetRange { get; }

        /// <summary>Memory clock offset, which widens with the same parts.</summary>
        ControlRange GpuMemoryOffsetRange { get; }

        /// <summary>Maximum-clock cap. The top of the range means "no cap".</summary>
        ControlRange GpuClockLimitRange { get; }

        /// <summary>Dynamic Boost wattage. 5, 15, 20 or 25 W depending on the model.</summary>
        ControlRange GpuBoostRange { get; }

        /// <summary>Temperature target the firmware will take for the discrete GPU.</summary>
        ControlRange GpuTempRange { get; }

        /// <summary>
        /// The adjustable part of the GPU's power budget, which sits on top of
        /// <see cref="GpuPowerBaseWatts"/> rather than replacing it.
        /// </summary>
        ControlRange GpuPowerOffsetRange { get; }

        /// <summary>
        /// The fixed part of the GPU's TGP, read from the firmware. The wattage the
        /// user cares about is this plus the offset, so the readout adds them.
        /// </summary>
        int GpuPowerBaseWatts { get; }

        /// <summary>
        /// False when the firmware reports no base TGP. The offset is meaningless
        /// without one - there is nothing for it to be an offset from - and its own
        /// ceiling is derived from it, so the row has no range to draw.
        /// </summary>
        bool IsGpuPowerAdjustable { get; }
    }

    public interface IDisplayService
    {
        int CurrentRefreshRate { get; }
        int MaxRefreshRate { get; }
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

    public interface IBatteryService
    {
        int ChargeLimit { get; }
        bool IsFullChargeOverride { get; }
        float DischargeRateWatts { get; }
        int BatteryPercent { get; }
        event Action<int> ChargeLimitChanged;
        event Action<bool> FullChargeOverrideChanged;

        void SetChargeLimit(int limitPercent);
        void ToggleFullChargeOverride();
        void GenerateBatteryReport();
    }

    public interface ICoolingService
    {
        bool CustomFansSupported { get; }
        event Action<string> CalibrationStatusChanged;
        event Action CalibrationCompleted;

        FanCurveModel GetFanCurve(int fanIndex, int modeIndex);
        void SaveFanCurve(int fanIndex, int modeIndex, FanCurveModel curve);
        void ResetFanCurves(int modeIndex);
        void ApplyFanCurves(int modeIndex);
        void StartCalibration();
    }

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

    public interface IPeripheralService
    {
        IReadOnlyList<PeripheralDeviceModel> Devices { get; }
        event Action DevicesChanged;

        void RefreshDevices();
        void SetDpi(string deviceId, int dpi);
        void SetPollingRate(string deviceId, int rateHz);
        void SetSleepTimeout(string deviceId, int minutes);
        void SetKeyboardLighting(string deviceId, int mode, int primaryArgb, int secondaryArgb, int speed, int brightness);
        void SetKeyboardProfile(string deviceId, int profile);
        void SetKeyboardEnergy(string deviceId, int sleepMinutes, int lowBatteryWarningPercent);
        void SetKeyboardOled(string deviceId, bool enabled, int brightness, int mode);
    }

    /// <summary>Built-in pointer and touch devices exposed by the laptop itself.</summary>
    public interface IInputDeviceService
    {
        bool HasTouchScreen { get; }
        bool HasTouchpad { get; }
        bool IsTouchpadEnabled { get; }
        event Action<bool> TouchpadStateChanged;

        void ToggleTouchpad();
    }

    public interface IUpdateService
    {
        event Action<UpdateInfo> UpdateStatusChanged;
        Task<UpdateInfo> CheckForUpdatesAsync(bool force = false);

        /// <summary>
        /// Downloads, verifies and starts installing the pending application update.
        /// <paramref name="progress"/> reports bytes received so the caller can show a
        /// real download bar; the signed byte count is on the <see cref="UpdateInfo"/>
        /// the check returned.
        /// </summary>
        Task<bool> DownloadAndInstallUpdateAsync(IProgress<long>? progress = null, CancellationToken cancellationToken = default);
        Task<List<UpdateInfo>> CheckAsusUpdatesAsync();

        /// <summary>
        /// Fetches one ASUS driver package to disk and returns where it landed, or null
        /// if it did not arrive.
        /// </summary>
        /// <remarks>
        /// The alternative is handing the URL to the default browser, which leaves the
        /// user to find the file afterwards and gives the application no idea whether
        /// anything was downloaded. <paramref name="progress"/> reports percent complete,
        /// or -1 for as long as the server has not declared a length.
        /// </remarks>
        /// <param name="expectedSha256">
        /// ASUS's published hash for the package, when the scan returned one. The file is
        /// offered to the shell to run afterwards, so a package that does not match what
        /// the publisher says it should be is discarded rather than kept.
        /// </param>
        Task<string?> DownloadAsusPackageAsync(
            string downloadUrl, IProgress<int>? progress, CancellationToken cancellationToken,
            string? expectedSha256 = null);

        /// <summary>Folder the packages above are written to.</summary>
        string DownloadFolder { get; }
    }

    public interface IProfileService
    {
        int AutoAcMode { get; set; }
        int AutoBatteryMode { get; set; }
        bool IsAutoSwitchEnabled { get; set; }
        void OnPowerSourceChanged(bool isAc);
        void OnAppForegroundChanged(string processName);
    }

    public interface IDeviceStateService : IDisposable
    {
        HardwareTelemetry CurrentTelemetry { get; }
        event Action<HardwareTelemetry> TelemetryUpdated;
        void SetPollingInterval(TimeSpan interval);
        void PausePolling();
        void ResumePolling();
        void RefreshNow();
    }

    public interface ISettingsSearchService
    {
        IReadOnlyList<SearchItem> Search(string query);
        void RegisterItem(SearchItem item);
    }
}
