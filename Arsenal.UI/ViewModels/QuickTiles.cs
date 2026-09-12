using Arsenal.Helpers;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace Arsenal.UI.ViewModels
{
    /// <summary>Where a tile belongs, used to order and caption the picker.</summary>
    public enum QuickTileGroup
    {
        Performance,
        Display,
        Battery,
        Lighting,
        System
    }

    /// <summary>
    /// One kind of tile the quick panel can show. The catalogue is the whole set; which
    /// of them a given machine can actually offer is decided by the view model, because
    /// that is what holds the capability flags.
    /// </summary>
    /// <param name="Key">Stable identifier. Persisted, so it must not change.</param>
    /// <param name="Summary">One line for the picker, saying what pressing it does.</param>
    /// <param name="Detail">The page the chevron opens, or None for a plain toggle.</param>
    public sealed record QuickTileDefinition(
        string Key,
        string Label,
        SymbolRegular Icon,
        QuickTileGroup Group,
        string Summary,
        QuickDetailPage Detail = QuickDetailPage.None);

    public static class QuickTileCatalog
    {
        /// <summary>
        /// What a fresh install shows: the six settings that were previously hard-coded
        /// into the panel.
        /// </summary>
        public static readonly string[] Defaults =
        {
            "performance", "gpu", "refresh", "keyboard", "overlay", "full_charge"
        };

        /// <summary>
        /// Every tile the panel knows how to draw, in picker order. Grouped rather than
        /// alphabetical: somebody looking for a display setting scans the display block.
        /// </summary>
        public static readonly IReadOnlyList<QuickTileDefinition> All = new QuickTileDefinition[]
        {
            // ---- Performance and graphics -------------------------------------
            new("performance", AppStrings.Get("MousePerformance"), SymbolRegular.Gauge24, QuickTileGroup.Performance,
                AppStrings.Get("QuickTilesStepsThroughSilentBalancedAnd"), QuickDetailPage.Performance),
            new("gpu", AppStrings.Get("DisplayGPUMode"), SymbolRegular.Glance24, QuickTileGroup.Performance,
                AppStrings.Get("QuickTilesStepsThroughTheGraphicsModes"), QuickDetailPage.Gpu),
            new("kill_gpu_apps", AppStrings.Get("QuickTilesCloseGPUApps"), SymbolRegular.Dismiss24, QuickTileGroup.Performance,
                AppStrings.Get("QuickTilesClosesWhateverIsHoldingThe")),
            new("restart_nv", AppStrings.Get("QuickTilesRestartNVIDIAServices"), SymbolRegular.ArrowSync24, QuickTileGroup.Performance,
                AppStrings.Get("QuickTilesRestartsTheNVIDIADisplayServices")),
            new("xgm", AppStrings.Get("QuickTilesXGMobile"), SymbolRegular.PlugConnected24, QuickTileGroup.Performance,
                AppStrings.Get("QuickTilesConnectsOrDisconnectsAnAttached")),
            new("auto_tdp", AppStrings.Get("QuickTilesAutoTDP"), SymbolRegular.Timer24, QuickTileGroup.Performance,
                AppStrings.Get("QuickTilesHoldsTheFrameRateBy")),
            new("fps_limit", AppStrings.Get("QuickTilesFrameLimit"), SymbolRegular.XboxController24, QuickTileGroup.Performance,
                AppStrings.Get("QuickTilesStepsThroughTheBuiltIn")),

            // ---- Display ------------------------------------------------------
            new("refresh", AppStrings.Get("DisplayRefreshRate"), SymbolRegular.Desktop24, QuickTileGroup.Display,
                AppStrings.Get("QuickTilesSwitchesBetweenTheLowestAnd"), QuickDetailPage.Display),
            new("overdrive", AppStrings.Get("QuickTilesPanelOverdrive"), SymbolRegular.Flash24, QuickTileGroup.Display,
                AppStrings.Get("QuickTilesFasterPixelResponseAtThe")),
            new("auto_refresh", AppStrings.Get("HomeAutomaticRefresh"), SymbolRegular.Sparkle24, QuickTileGroup.Display,
                AppStrings.Get("QuickTilesLetsTheRateFollowThe")),
            new("miniled", AppStrings.Get("QuickTilesMiniLEDZones"), SymbolRegular.Lightbulb24, QuickTileGroup.Display,
                AppStrings.Get("QuickTilesSwitchesTheBacklightBetween")),
            new("visual", AppStrings.Get("MainColourProfile"), SymbolRegular.Color24, QuickTileGroup.Display,
                AppStrings.Get("QuickTilesPicksAGameVisualProfile"), QuickDetailPage.Visual),
            new("gamut", AppStrings.Get("DisplayColourGamut"), SymbolRegular.Diversity24, QuickTileGroup.Display,
                AppStrings.Get("QuickTilesPicksThePanelSColour"), QuickDetailPage.Gamut),
            new("resolution", AppStrings.Get("QuickTilesResolution"), SymbolRegular.Window24, QuickTileGroup.Display,
                AppStrings.Get("QuickTilesSwitchesADualModePanel")),
            new("hdr", AppStrings.Get("QuickTilesHDRControl"), SymbolRegular.Sparkle24, QuickTileGroup.Display,
                AppStrings.Get("QuickTilesHandsHDRToneMappingBack")),
            new("touchscreen", AppStrings.Get("QuickTilesTouchScreen"), SymbolRegular.Cursor24, QuickTileGroup.Display,
                AppStrings.Get("QuickTilesTurnsTheTouchDigitiserOn")),

            // ---- Battery ------------------------------------------------------
            new("full_charge", AppStrings.Get("HomeFullCharge"), SymbolRegular.BatteryCharge24, QuickTileGroup.Battery,
                AppStrings.Get("QuickTilesChargesTo100OnceIgnoring")),
            new("charge_limit", AppStrings.Get("BatteryChargeLimit2"), SymbolRegular.ShieldCheckmark24, QuickTileGroup.Battery,
                AppStrings.Get("QuickTilesPicksTheLevelChargingStops"), QuickDetailPage.ChargeLimit),
            new("battery_report", AppStrings.Get("QuickTilesBatteryReport"), SymbolRegular.DocumentBulletList24, QuickTileGroup.Battery,
                AppStrings.Get("QuickTilesWritesTheWindowsBatteryHealth")),

            // ---- Lighting -----------------------------------------------------
            new("keyboard", AppStrings.Get("HomeKeyboardLight"), SymbolRegular.Keyboard24, QuickTileGroup.Lighting,
                AppStrings.Get("QuickTilesStepsTheBacklightThroughIts"), QuickDetailPage.Keyboard),
            new("aura", AppStrings.Get("LightingAuraEffect"), SymbolRegular.Color24, QuickTileGroup.Lighting,
                AppStrings.Get("QuickTilesPicksAKeyboardLightingEffect"), QuickDetailPage.Aura),
            new("matrix", AppStrings.Get("MainAniMeMatrix"), SymbolRegular.Stack24, QuickTileGroup.Lighting,
                AppStrings.Get("QuickTilesSetsTheLidDisplayS"), QuickDetailPage.Matrix),

            // ---- System -------------------------------------------------------
            new("overlay", AppStrings.Get("Overlay"), SymbolRegular.DataUsage24, QuickTileGroup.System,
                AppStrings.Get("QuickTilesChoosesWhetherTheHardwareReadout"), QuickDetailPage.Overlay),
            new("auto_switch", AppStrings.Get("QuickTilesAutomaticModes"), SymbolRegular.Sparkle24, QuickTileGroup.System,
                AppStrings.Get("QuickTilesSwitchesPerformanceModeWithThe")),
            new("touchpad", AppStrings.Get("HomeTouchpad"), SymbolRegular.CursorClick24, QuickTileGroup.System,
                AppStrings.Get("QuickTilesEnablesOrDisablesTheLaptop")),
            new("fn_lock", AppStrings.Get("AdvancedFnLock"), SymbolRegular.Keyboard24, QuickTileGroup.System,
                AppStrings.Get("QuickTilesSwapsTheFunctionRowBetween")),
            new("status_leds", AppStrings.Get("QuickTilesStatusLights"), SymbolRegular.Lightbulb24, QuickTileGroup.System,
                AppStrings.Get("QuickTilesTurnsTheChassisIndicatorLights")),
            new("number_pad", AppStrings.Get("QuickTilesNumberPad"), SymbolRegular.Keyboard24, QuickTileGroup.System,
                AppStrings.Get("QuickTilesLightsTheNumberPadOn")),
            new("clamshell", AppStrings.Get("AutomationClamshellMode"), SymbolRegular.Laptop24, QuickTileGroup.System,
                AppStrings.Get("QuickTilesKeepsTheMachineAwakeWith")),
            new("boot_sound", AppStrings.Get("QuickTilesStartupSound"), SymbolRegular.Alert24, QuickTileGroup.System,
                AppStrings.Get("QuickTilesTheChimeTheFirmwarePlays")),
            new("aspm", AppStrings.Get("QuickTilesPCIePowerSaving"), SymbolRegular.Power24, QuickTileGroup.System,
                AppStrings.Get("QuickTilesLetsPCIeDevicesEnterTheir")),
            new("standby_network", AppStrings.Get("QuickTilesNetworkingInStandby"), SymbolRegular.Sleep24, QuickTileGroup.System,
                AppStrings.Get("QuickTilesKeepsTheNetworkAliveWhile")),
            new("always_on_top", AppStrings.Get("QuickTilesKeepWindowOnTop"), SymbolRegular.PanelRight24, QuickTileGroup.System,
                AppStrings.Get("QuickTilesHoldsTheArsenalWindowAbove")),
            new("power_options", AppStrings.Get("QuickTilesPowerOptions"), SymbolRegular.Settings24, QuickTileGroup.System,
                AppStrings.Get("QuickTilesOpensTheWindowsPowerPlan")),
        };

        private static readonly Dictionary<string, QuickTileDefinition> ByKey =
            All.ToDictionary(tile => tile.Key, StringComparer.Ordinal);

        public static QuickTileDefinition? Find(string? key) =>
            key is not null && ByKey.TryGetValue(key, out QuickTileDefinition? tile) ? tile : null;

        public static string GroupName(QuickTileGroup group) => group switch
        {
            QuickTileGroup.Performance => AppStrings.Get("MousePerformance"),
            QuickTileGroup.Display => AppStrings.Get("AboutDisplay"),
            QuickTileGroup.Battery => AppStrings.Get("Battery"),
            QuickTileGroup.Lighting => AppStrings.Get("Lighting"),
            _ => AppStrings.Get("SettingsSystem")
        };
    }

    /// <summary>One dot beside the tile grid, standing for a page of tiles.</summary>
    public partial class QuickTilePage : ObservableObject
    {
        public QuickTilePage(int index) => Index = index;

        public int Index { get; }

        [ObservableProperty]
        private bool _isCurrent;
    }

    /// <summary>
    /// One position in the quick panel's grid, bound to a tile. The definition is fixed;
    /// the state and the lit indicator follow the hardware.
    /// </summary>
    public partial class QuickTileSlot : ObservableObject
    {
        public QuickTileSlot(QuickTileDefinition definition, ICommand? expandCommand)
        {
            Definition = definition;
            ExpandCommand = expandCommand;
        }

        public QuickTileDefinition Definition { get; }

        public string Key => Definition.Key;
        public string Label => Definition.Label;
        public SymbolRegular Icon => Definition.Icon;

        /// <summary>
        /// Null when the tile has no list to open, which is what hides the chevron and
        /// makes the whole tile a single hit target.
        /// </summary>
        public ICommand? ExpandCommand { get; }

        [ObservableProperty]
        private string _state = string.Empty;

        [ObservableProperty]
        private bool _isChecked;
    }
}
