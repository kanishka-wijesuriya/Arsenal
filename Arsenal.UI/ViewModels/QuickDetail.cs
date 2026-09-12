using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Controls;

namespace Arsenal.UI.ViewModels
{
    /// <summary>Which setting the quick panel is showing the full state list for.</summary>
    public enum QuickDetailPage
    {
        None,
        Performance,
        Gpu,
        Display,
        Keyboard,
        Aura,
        Visual,
        Gamut,
        Matrix,
        ChargeLimit,
        Overlay,
        PerformanceShortcuts,

        /// <summary>Choosing which tile occupies a slot. Reached only from edit mode.</summary>
        TilePicker
    }

    /// <summary>One selectable state inside a quick panel detail page.</summary>
    public partial class QuickOptionItem : ObservableObject
    {
        public QuickOptionItem(QuickDetailPage page, int value, string name, string? detail = null, string? key = null,
            SymbolRegular icon = SymbolRegular.Empty, string? placement = null)
        {
            Page = page;
            Value = value;
            Name = name;
            Detail = detail;
            Key = key;
            Icon = icon;
            Placement = placement;
        }

        public QuickDetailPage Page { get; }
        public int Value { get; }
        public string Name { get; }
        public string? Detail { get; }
        public string? Key { get; }

        /// <summary>
        /// Shown in the gutter on pages where the rows are things rather than states -
        /// the tile picker, where the glyph is how you recognise a tile you have seen on
        /// the grid. Empty on the state lists, whose gutter belongs to the tick.
        /// </summary>
        public SymbolRegular Icon { get; }

        /// <summary>
        /// Set only for a tile-picker row whose tile is already on the panel. Keeping the
        /// page label separate from the description lets the picker show it as a badge
        /// without changing the option model used by the setting-state lists.
        /// </summary>
        public string? Placement { get; }

        [ObservableProperty]
        private bool _isSelected;
    }
}
