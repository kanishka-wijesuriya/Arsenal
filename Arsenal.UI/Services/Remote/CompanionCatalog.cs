using Arsenal.Application.Models;
using Arsenal.Application.Services.Contracts;
using Arsenal.Display;
using Arsenal.Helpers;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace Arsenal.UI.Services.Remote;

/// <summary>
/// A machine-readable description of every setting the companion can reach.
///
/// The phone used to carry its own copy of each option list - the performance profiles,
/// the Aura effects, the Slash patterns, the white-point steps - and its own idea of how
/// far each slider travelled. Every one of those was a guess about hardware it cannot
/// see, and each was wrong somewhere: a fixed three-entry profile list hid custom
/// profiles, a fixed DPI track claimed a 2000 DPI mouse reached 36000, a fixed
/// System/Clock/Audio triple named modes the device did not have. Adding a mode on the
/// desktop also meant shipping a new phone build before anyone could reach it.
///
/// So the desktop describes itself instead. Each entry names the action to send, the key
/// its value arrives under in the snapshot, what kind of control it is, and - for a
/// choice - the options this machine actually offers, or - for a slider - the bounds the
/// writer will accept. The lists come from the same places the desktop's own pages read
/// them, so the two cannot drift apart.
///
/// Values do not live here. The snapshot carries those, and the phone joins the two on
/// <c>key</c>. Ranges are in the snapshot too, because some of them are computed lazily
/// and can move while the app is running.
/// </summary>
internal static class CompanionCatalog
{
    /// <summary>
    /// Option lists change only when hardware appears or disappears, so the catalog is
    /// built at most this often. Every snapshot reports the current version and the
    /// phone re-fetches only when that moves.
    /// </summary>
    private static readonly TimeSpan RebuildAfter = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions HashOptions = new(JsonSerializerDefaults.Web);
    private static readonly object Gate = new();
    private static object[]? _groups;
    private static int _version;
    private static DateTime _builtUtc = DateTime.MinValue;

    /// <summary>The whole catalog, for <c>GET /v1/catalog</c>.</summary>
    internal static object Build(IServiceProvider services)
    {
        EnsureCurrent(services);
        lock (Gate) return new { version = _version, groups = _groups ?? Array.Empty<object>() };
    }

    /// <summary>
    /// The version a snapshot advertises. Changes whenever any option list, title or
    /// availability flag changes, which is the phone's signal to fetch the catalog again.
    /// </summary>
    internal static int Version(IServiceProvider services)
    {
        EnsureCurrent(services);
        lock (Gate) return _version;
    }

    private static void EnsureCurrent(IServiceProvider services)
    {
        lock (Gate)
        {
            if (_groups is not null && DateTime.UtcNow - _builtUtc < RebuildAfter) return;
        }

        object[] groups = BuildGroups(services);

        // A content hash rather than a counter: the catalog is rebuilt on a timer, and a
        // counter would tell every phone to re-fetch a document that had not changed.
        int version = StableHash(JsonSerializer.Serialize(groups, HashOptions));

        lock (Gate)
        {
            _groups = groups;
            _version = version;
            _builtUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// FNV-1a. String.GetHashCode is randomised per process, so it would change the
    /// version on every desktop restart and make every phone re-fetch for nothing.
    /// </summary>
    private static int StableHash(string value)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in value)
            {
                hash ^= c;
                hash *= 16777619;
            }
            return (int)hash;
        }
    }

    private static object[] BuildGroups(IServiceProvider services)
    {
        var performance = services.GetRequiredService<IPerformanceService>();
        var gpu = services.GetRequiredService<IGpuService>();
        var lighting = services.GetRequiredService<ILightingService>();
        var groups = new List<object>();

        // ---- Performance ---------------------------------------------------------
        var performanceSettings = new List<object>
        {
            Choice("performance.mode", "performanceMode", "Profile", Modes(), "Each mode keeps its own saved limits."),
            Toggle("performance.applyPower", "applyPower", "Custom power limits", "Apply these values whenever this profile activates."),
            Slider("performance.spl", "spl", "Sustained power", "powerLimit", "W"),
            Slider("performance.sppt", "sppt", "Slow burst", "powerLimit", "W"),
            Slider("performance.fppt", "fppt", "Fast peak", "powerLimit", "W"),
            Slider("performance.cpuTemp", "cpuTempLimit", "Temperature limit", "cpuTemp", "°C"),
            // Seven policies, in the order the desktop's own list uses them as indices.
            // The phone used to offer the first three and call the second one "On".
            Choice("performance.cpuBoost", "cpuBoost", "Boost policy", new[]
            {
                AppStrings.Get("DisplayDisabled"),
                AppStrings.Get("PerformanceEnabled"),
                AppStrings.Get("PerformanceAggressive"),
                AppStrings.Get("PerformanceEfficientEnabled"),
                AppStrings.Get("PerformanceEfficientAggressive"),
                AppStrings.Get("PerformanceAggressiveAtGuaranteed"),
                AppStrings.Get("PerformanceEfficientAtGuaranteed"),
            }.Select((name, index) => (index, name))),
        };

        if (performance.IsUndervoltSupported)
        {
            performanceSettings.Add(Toggle("performance.applyUndervolt", "applyUndervolt", "Apply undervolt", "Enable offsets for this profile."));
            performanceSettings.Add(Slider("performance.cpuUndervolt", "cpuUndervolt", "CPU undervolt", "cpuUndervolt", "mV"));
        }
        if (performance.IsIgpuUndervoltSupported)
        {
            performanceSettings.Add(Slider("performance.igpuUndervolt", "igpuUndervolt", "iGPU undervolt", "igpuUndervolt", "mV"));
        }

        performanceSettings.Add(Action("performance.save", "Save and apply profile"));
        performanceSettings.Add(Action("performance.reset", "Reset this profile"));
        groups.Add(Group("performance", "Performance", "Tune", performanceSettings));

        // ---- Dedicated GPU -------------------------------------------------------
        if (gpu.HasDedicatedGpu)
        {
            groups.Add(Group("gpu", "Dedicated GPU", "Tune", new List<object>
            {
                Choice("gpu.mode", "gpuMode", "GPU mode", GpuModes(gpu), "Which graphics hardware is active."),
                Slider("performance.gpuCore", "gpuCoreOffset", "Core clock offset", "gpuCoreOffset", "MHz"),
                Slider("performance.gpuMemory", "gpuMemoryOffset", "Memory clock offset", "gpuMemoryOffset", "MHz"),
                Slider("performance.gpuBoost", "gpuBoost", "Dynamic boost", "gpuBoost", "W"),
                Slider("performance.gpuTemp", "gpuTempTarget", "Temperature target", "gpuTemp", "°C"),
                Slider("performance.gpuPower", "gpuPowerTarget", "Power target", "gpuPowerTotal", "W"),
                Slider("performance.gpuClock", "gpuClockLimit", "Maximum clock", "gpuClockLimit", "MHz"),
                Action("gpu.killApps", "Close GPU applications"),
                Action("gpu.restartServices", "Restart NVIDIA services"),
                Action("gpu.toggleXgm", "Toggle XG Mobile"),
            }));
        }

        // ---- Fans ----------------------------------------------------------------
        groups.Add(Group("fans", "Cooling", "Tune", new List<object>
        {
            Toggle("performance.applyFans", "applyFans", "Apply custom curves"),
            Slider("performance.fanHysteresisUp", "fanHysteresisUp", "Ramp-up hysteresis", "fanHysteresis", "°C"),
            Slider("performance.fanHysteresisDown", "fanHysteresisDown", "Ramp-down hysteresis", "fanHysteresis", "°C"),
            Curve("fans.cpuCurve", "cpuFanCurve", "CPU fan"),
            Curve("fans.gpuCurve", "gpuFanCurve", "GPU fan"),
            Curve("fans.midCurve", "midFanCurve", "Mid fan"),
            Action("fans.apply", "Apply fan curves"),
            Action("fans.calibrate", "Calibrate fans"),
        }));

        // ---- Display -------------------------------------------------------------
        var displaySettings = new List<object>
        {
            Slider("display.panelBrightness", "panelBrightness", "Brightness", "panelBrightness", "%"),
            Toggle("display.autoRefresh", "autoRefresh", "Automatic refresh rate", "Lower refresh on battery and restore it on AC."),
            Toggle("display.overdrive", "overdrive", "Panel overdrive", "Reduce pixel response time at maximum refresh."),
            Toggle("display.miniLed", "miniLed", "Multi-zone Mini-LED", "Use local dimming for deeper contrast."),
            Slider("display.oledDimming", "oledDimming", "OLED dimming", "oledDimming", "%"),
            Choice("display.visualProfile", "visualProfile", "Visual profile", VisualControl.GetVisualModes().Select(p => ((int)p.Key, p.Value))),
            Choice("display.gamut", "gamut", "Colour gamut", VisualControl.GetGamutModes().Select(p => ((int)p.Key, p.Value.Replace("Gamut: ", string.Empty)))),
            Choice("display.temperature", "colorTemperature", "White point", VisualControl.GetTemperatures().Select(p => (p.Key, p.Value))),
            Action("display.resolution", "Toggle panel resolution"),
            Action("display.hdr", "Toggle HDR control"),
            Action("display.touch", "Toggle touchscreen"),
            Action("display.installProfiles", "Install ASUS colour profiles"),
        };
        groups.Add(Group("display", "Display", "Display", displaySettings));

        // ---- Battery -------------------------------------------------------------
        groups.Add(Group("battery", "Battery", "Battery", new List<object>
        {
            Slider("battery.limit", "chargeLimit", "Maximum charge level", "chargeLimit", "%"),
            Toggle("battery.fullCharge", "fullChargeOverride", "Temporary full charge", "Charge to 100% once, then return to the saved ceiling."),
            Action("battery.report", "Generate battery report"),
        }));

        // ---- Keyboard lighting ---------------------------------------------------
        var lightingSettings = new List<object>
        {
            Choice("lighting.brightness", "keyboardBrightness", "Brightness", Pairs(("Off", 0), ("Low", 1), ("Medium", 2), ("Max", 3))),
        };
        if (lighting.HasAuraEffects)
        {
            lightingSettings.Add(Choice("lighting.mode", "lightingMode", "Aura effect", Arsenal.USB.Aura.GetModes().Select(p => ((int)p.Key, p.Value))));
            lightingSettings.Add(Choice("lighting.speed", "lightingSpeed", "Animation speed", Arsenal.USB.Aura.GetSpeeds().Select(p => ((int)p.Key, p.Value))));
        }
        if (lighting.HasKeyboardColor) lightingSettings.Add(Colour("lighting.color", "lightingColor", "Colour"));
        lightingSettings.Add(Toggle("lighting.awake", "lightAwake", "Awake", "Keep lighting on while the PC is in use."));
        lightingSettings.Add(Toggle("lighting.boot", "lightBoot", "Boot", "Play lighting while Windows starts."));
        lightingSettings.Add(Toggle("lighting.sleep", "lightSleep", "Sleep", "Keep a low-power effect while sleeping."));
        lightingSettings.Add(Toggle("lighting.shutdown", "lightShutdown", "Shutdown", "Keep supported zones lit after shutdown."));
        groups.Add(Group("lighting", "Keyboard backlight", "Lighting", lightingSettings));

        // ---- The secondary lighting device --------------------------------------
        if (lighting.HasAnimeMatrix || lighting.HasSlash)
        {
            bool slash = lighting.HasSlash;
            var deviceSettings = new List<object>
            {
                Choice("matrix.mode", "matrixMode", slash ? "Pattern" : "Visual", MatrixModes(slash),
                    slash ? "Every animation this Slash strip can play." : "What the AniMe panel displays."),
                Choice("matrix.brightness", "matrixBrightness", "Device brightness", Pairs(("Off", 0), ("Low", 1), ("Medium", 2), ("High", 3))),
                Toggle("matrix.offBattery", "matrixOffBattery", "Turn off on battery"),
                Toggle("matrix.offLid", "matrixOffLid", "Turn off with lid closed"),
            };
            if (lighting.HasAnimeMatrix)
            {
                deviceSettings.Add(Toggle("matrix.flip", "matrixFlip", "Rotate 180°", "For a lid-down or wall-mounted panel."));
            }
            groups.Add(Group("matrix", slash ? "Slash display" : "AniMe Matrix", "Lighting", deviceSettings));
        }

        if (lighting.HasSlash)
        {
            groups.Add(Group("slash", "Slash animations", "Lighting", new List<object>
            {
                Slider("slash.interval", "slashInterval", "Animation interval", "slashInterval", string.Empty),
                Toggle("slash.boot", "slashBootAnimation", "Boot animation"),
                Toggle("slash.sleep", "slashSleepAnimation", "Sleep animation"),
                Choice("slash.sleepPattern", "slashSleepPattern", "Sleep pattern", Pairs(("Default", 0), ("Current visual", 1))),
                Toggle("slash.lowBattery", "slashLowBatteryAlert", "Low-battery alert"),
                Toggle("slash.batteryIndicator", "slashBatteryIndicator", "Battery-level indicator"),
                Toggle("slash.powerSaving", "slashPowerSaving", "Dim at low battery"),
                Choice("slash.dimLevel", "slashDimLevel", "Low-battery dim level",
                    new[] { 10, 20, 30, 40, 50, 100 }.Select(level => (level, level + "%"))),
            }));
        }

        // ---- Automation ----------------------------------------------------------
        groups.Add(Group("automation", "Automation", "Automation", new List<object>
        {
            Toggle("automation.enabled", "autoSwitch", "Switch profile automatically"),
            Choice("automation.acMode", "autoAcMode", "On AC power", Modes()),
            Choice("automation.batteryMode", "autoBatteryMode", "On battery", Modes()),
            Toggle("automation.ecoOnBattery", "ecoOnBattery", "Eco GPU mode on battery"),
            Toggle("automation.clamshell", "clamshellMode", "Clamshell mode"),
        }));

        // ---- Advanced ------------------------------------------------------------
        groups.Add(Group("advanced", "System policy", "Advanced", new List<object>
        {
            Action("advanced.asusServices", "Stop ASUS background services"),
            Toggle("advanced.fnLock", "fnLock", "Fn lock", "Invert the default behaviour of the function row."),
            Toggle("advanced.statusLeds", "statusLeds", "Status LEDs"),
            Toggle("advanced.numberPad", "numberPad", "NumberPad"),
            Toggle("advanced.overlay", "hardwareOverlay", "On-screen hardware monitor"),
            Toggle("advanced.overlayGaming", "overlayGamingOnly", "Only while gaming"),
            Toggle("advanced.pcie", "pciePowerSaving", "PCIe link power management"),
            Toggle("advanced.standbyNetwork", "standbyNetworking", "Standby networking"),
            Toggle("advanced.nvidiaPlatform", "nvidiaPlatform", "NVIDIA platform controller"),
            Toggle("advanced.closeGpuApps", "closeGpuApps", "Close apps before Eco mode"),
            Toggle("advanced.usbcOptimized", "optimizedOnUsbC", "Optimized mode on USB-C power"),
            Toggle("advanced.disableOverdrive", "disableOverdriveAutomation", "Disable overdrive automation"),
            Toggle("advanced.forceOverdrive", "forceOverdrive", "Force panel overdrive"),
            Toggle("advanced.autoClamshell", "automaticClamshell", "Automatic clamshell mode"),
            Toggle("advanced.bootSound", "bootSound", "Boot sound"),
            Toggle("advanced.alwaysOnTop", "alwaysOnTop", "Always on top"),
            Slider("advanced.hibernate", "hibernateMinutes", "Hibernate after", "hibernateMinutes", "min"),
            Slider("advanced.backlightBattery", "backlightBatterySeconds", "Backlight timeout · battery", "backlightBatterySeconds", "s"),
            Slider("advanced.backlightAc", "backlightAcSeconds", "Backlight timeout · AC", "backlightAcSeconds", "s"),
            Action("advanced.powerOptions", "Open Windows power options"),
            Action("advanced.log", "Open Arsenal log"),
        }));

        if (AppConfig.IsAlly())
        {
            groups.Add(Group("handheld", "ROG Ally", "Advanced", new List<object>
            {
                Toggle("advanced.autoTdp", "autoTdp", "Automatic TDP"),
                Action("advanced.fpsLimit", "Frame-rate limit"),
            }));
        }

        // ---- Peripherals ---------------------------------------------------------
        // Per-device settings, so these carry a deviceId alongside the value. The bounds
        // for DPI and the list of polling rates belong to each mouse and travel with it
        // in the snapshot rather than here.
        groups.Add(Group("peripherals", "Peripherals", "Peripherals", new List<object>
        {
            Choice("peripherals.sleep", null, "Sleep timeout", new[]
            {
                (0, AppStrings.Get("Never")), (1, "1 min"), (2, "2 min"),
                (3, "3 min"), (5, "5 min"), (10, "10 min"),
            }),
            Action("peripherals.refresh", "Scan again"),
        }));

        // ---- The desktop application --------------------------------------------
        groups.Add(Group("app", "Windows app", "Software", new List<object>
        {
            Toggle("app.startup", "runOnStartup", "Run at Windows sign-in"),
            Toggle("app.closeToTray", "minimizeToTray", "Minimise to tray on close"),
            Toggle("app.checkUpdates", "checkUpdates", "Check for updates at startup"),
            Choice("app.theme", "theme", "Theme", Pairs(("System", 0), ("Dark", 1), ("Light", 2))),
            Toggle("app.toast", "toastEnabled", "Show floating notifications"),
            Choice("app.toastStyle", "toastStyle", "Design", Pairs(("Fluent", 0), ("Compact", 1), ("Accent", 2))),
            Choice("app.toastPosition", "toastPosition", "Location",
                Pairs(("Top right", 0), ("Bottom right", 1), ("Top centre", 2), ("Bottom centre", 3))),
            Slider("app.toastDuration", "toastDurationSeconds", "Visible duration", "toastDuration", "s"),
            Toggle("app.toastProgress", "toastProgress", "Countdown bar"),
            Action("app.testToast", "Preview notification"),
            Action("app.runSetup", "Run setup again"),
        }));

        return groups.ToArray();
    }

    // ---- Option sources ----------------------------------------------------------

    private static IEnumerable<(int, string)> Modes() =>
        Arsenal.Mode.Modes.GetDictonary().Select(pair => (pair.Key, pair.Value));

    private static IEnumerable<(int, string)> MatrixModes(bool slash) => slash
        ? Arsenal.AnimeMatrix.SlashDevice.Modes.Select(pair => ((int)pair.Key, pair.Value))
        : new[]
        {
            AppStrings.Get("FeatureBanner"), AppStrings.Get("AuraZoneLogo"), AppStrings.Get("MatrixPicture"),
            AppStrings.Get("MatrixClock"), AppStrings.Get("MatrixAudio"), AppStrings.Get("MatrixText"),
        }.Select((name, index) => (index, name));

    private static IEnumerable<(int, string)> GpuModes(IGpuService gpu)
    {
        var modes = new List<(int, string)>();
        if (gpu.IsEcoSupported) modes.Add((0, "Eco"));
        modes.Add((1, "Standard"));
        if (gpu.IsMuxSupported) modes.Add((2, "Ultimate"));
        modes.Add((3, "Optimized"));
        return modes;
    }

    private static IEnumerable<(int, string)> Pairs(params (string Name, int Id)[] options) =>
        options.Select(option => (option.Id, option.Name));

    // ---- Entry shapes ------------------------------------------------------------

    private static object Group(string id, string title, string page, List<object> settings) =>
        new { id, title, page, settings = settings.ToArray() };

    private static object Choice(string action, string? key, string title, IEnumerable<(int Id, string Name)> options, string? description = null) =>
        new
        {
            action,
            key,
            kind = "choice",
            title,
            description,
            options = options.Select(option => new { id = option.Id, name = option.Name }).ToArray(),
        };

    /// <summary>
    /// A slider. The bounds are named rather than carried: they live in the snapshot's
    /// <c>ranges</c>, because several of them are computed lazily on the desktop and can
    /// widen after the catalog was built.
    /// </summary>
    private static object Slider(string action, string key, string title, string range, string unit, string? description = null) =>
        new { action, key, kind = "slider", title, description, range, unit };

    private static object Toggle(string action, string key, string title, string? description = null) =>
        new { action, key, kind = "toggle", title, description };

    private static object Action(string action, string title, string? description = null) =>
        new { action, key = (string?)null, kind = "action", title, description };

    private static object Colour(string action, string key, string title, string? description = null) =>
        new { action, key, kind = "colour", title, description };

    private static object Curve(string action, string key, string title, string? description = null) =>
        new { action, key, kind = "curve", title, description };
}
