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
            new("performance", "Performance", SymbolRegular.Gauge24, QuickTileGroup.Performance,
                "Steps through Silent, Balanced and Turbo", QuickDetailPage.Performance),
            new("gpu", "GPU mode", SymbolRegular.Glance24, QuickTileGroup.Performance,
                "Steps through the graphics modes this machine supports", QuickDetailPage.Gpu),
            new("kill_gpu_apps", "Close GPU apps", SymbolRegular.Dismiss24, QuickTileGroup.Performance,
                "Closes whatever is holding the dedicated GPU open"),
            new("restart_nv", "Restart NVIDIA services", SymbolRegular.ArrowSync24, QuickTileGroup.Performance,
                "Restarts the NVIDIA display services without a reboot"),
            new("xgm", "XG Mobile", SymbolRegular.PlugConnected24, QuickTileGroup.Performance,
                "Connects or disconnects an attached XG Mobile"),
            new("auto_tdp", "Auto TDP", SymbolRegular.Timer24, QuickTileGroup.Performance,
                "Holds the frame rate by moving the power limit"),
            new("fps_limit", "Frame limit", SymbolRegular.XboxController24, QuickTileGroup.Performance,
                "Steps through the built-in frame rate caps"),

            // ---- Display ------------------------------------------------------
            new("refresh", "Refresh rate", SymbolRegular.Desktop24, QuickTileGroup.Display,
                "Switches between the lowest and highest rate the panel offers", QuickDetailPage.Display),
            new("overdrive", "Panel overdrive", SymbolRegular.Flash24, QuickTileGroup.Display,
                "Faster pixel response at the highest refresh rate"),
            new("auto_refresh", "Automatic refresh", SymbolRegular.Sparkle24, QuickTileGroup.Display,
                "Lets the rate follow the power source"),
            new("miniled", "Mini-LED zones", SymbolRegular.Lightbulb24, QuickTileGroup.Display,
                "Switches the backlight between single and multi-zone"),
            new("visual", "Colour profile", SymbolRegular.Color24, QuickTileGroup.Display,
                "Picks a GameVisual profile", QuickDetailPage.Visual),
            new("gamut", "Colour gamut", SymbolRegular.Diversity24, QuickTileGroup.Display,
                "Picks the panel's colour space", QuickDetailPage.Gamut),
            new("resolution", "Resolution", SymbolRegular.Window24, QuickTileGroup.Display,
                "Switches a dual-mode panel between its two resolutions"),
            new("hdr", "HDR control", SymbolRegular.Sparkle24, QuickTileGroup.Display,
                "Hands HDR tone mapping back to Windows"),
            new("touchscreen", "Touch screen", SymbolRegular.Cursor24, QuickTileGroup.Display,
                "Turns the touch digitiser on and off"),

            // ---- Battery ------------------------------------------------------
            new("full_charge", "Full charge", SymbolRegular.BatteryCharge24, QuickTileGroup.Battery,
                "Charges to 100% once, ignoring the limit"),
            new("charge_limit", "Charge limit", SymbolRegular.ShieldCheckmark24, QuickTileGroup.Battery,
                "Picks the level charging stops at", QuickDetailPage.ChargeLimit),
            new("battery_report", "Battery report", SymbolRegular.DocumentBulletList24, QuickTileGroup.Battery,
                "Writes the Windows battery health report and opens it"),

            // ---- Lighting -----------------------------------------------------
            new("keyboard", "Keyboard light", SymbolRegular.Keyboard24, QuickTileGroup.Lighting,
                "Steps the backlight through its four levels", QuickDetailPage.Keyboard),
            new("aura", "Aura effect", SymbolRegular.Color24, QuickTileGroup.Lighting,
                "Picks a keyboard lighting effect", QuickDetailPage.Aura),
            new("matrix", "AniMe Matrix", SymbolRegular.Stack24, QuickTileGroup.Lighting,
                "Sets the lid display's brightness", QuickDetailPage.Matrix),

            // ---- System -------------------------------------------------------
            new("overlay", "Overlay", SymbolRegular.DataUsage24, QuickTileGroup.System,
                "Chooses whether the hardware readout is off, always on, or only in games", QuickDetailPage.Overlay),
            new("auto_switch", "Automatic modes", SymbolRegular.Sparkle24, QuickTileGroup.System,
                "Switches performance mode with the power source"),
            new("touchpad", "Touchpad", SymbolRegular.CursorClick24, QuickTileGroup.System,
                "Enables or disables the laptop touchpad"),
            new("fn_lock", "Fn lock", SymbolRegular.Keyboard24, QuickTileGroup.System,
                "Swaps the function row between media keys and F1-F12"),
            new("status_leds", "Status lights", SymbolRegular.Lightbulb24, QuickTileGroup.System,
                "Turns the chassis indicator lights on and off"),
            new("number_pad", "Number pad", SymbolRegular.Keyboard24, QuickTileGroup.System,
                "Lights the number pad on the touchpad"),
            new("clamshell", "Clamshell mode", SymbolRegular.Laptop24, QuickTileGroup.System,
                "Keeps the machine awake with the lid closed"),
            new("boot_sound", "Startup sound", SymbolRegular.Alert24, QuickTileGroup.System,
                "The chime the firmware plays at power-on"),
            new("aspm", "PCIe power saving", SymbolRegular.Power24, QuickTileGroup.System,
                "Lets PCIe devices enter their low-power states"),
            new("standby_network", "Networking in standby", SymbolRegular.Sleep24, QuickTileGroup.System,
                "Keeps the network alive while the machine sleeps"),
            new("always_on_top", "Keep window on top", SymbolRegular.PanelRight24, QuickTileGroup.System,
                "Holds the Arsenal window above other windows"),
            new("power_options", "Power options", SymbolRegular.Settings24, QuickTileGroup.System,
                "Opens the Windows power plan settings"),
        };

        private static readonly Dictionary<string, QuickTileDefinition> ByKey =
            All.ToDictionary(tile => tile.Key, StringComparer.Ordinal);

        public static QuickTileDefinition? Find(string? key) =>
            key is not null && ByKey.TryGetValue(key, out QuickTileDefinition? tile) ? tile : null;

        public static string GroupName(QuickTileGroup group) => group switch
        {
            QuickTileGroup.Performance => "Performance",
            QuickTileGroup.Display => "Display",
            QuickTileGroup.Battery => "Battery",
            QuickTileGroup.Lighting => "Lighting",
            _ => "System"
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
