using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace Arsenal.UI.Controls
{
    /// <summary>
    /// An action-centre tile: the body toggles the setting, and an optional chevron on
    /// the trailing edge opens the full list of states for it.
    ///
    /// The two halves are separate hit targets, matching the Wi-Fi and Bluetooth tiles
    /// in the Windows quick settings flyout.
    /// </summary>
    public class QuickTile : ToggleButton
    {
        /// <summary>
        /// The checked state mirrors the hardware, not the click. Letting the base class
        /// flip it locally made the tile show the new state for a frame before the view
        /// model corrected it; the command it raises is what actually changes anything.
        /// </summary>
        protected override void OnToggle()
        {
        }

        public static readonly DependencyProperty IconProperty =
            DependencyProperty.Register(nameof(Icon), typeof(SymbolRegular), typeof(QuickTile),
                new PropertyMetadata(SymbolRegular.Empty));

        public SymbolRegular Icon
        {
            get => (SymbolRegular)GetValue(IconProperty);
            set => SetValue(IconProperty, value);
        }

        public static readonly DependencyProperty LabelProperty =
            DependencyProperty.Register(nameof(Label), typeof(string), typeof(QuickTile),
                new PropertyMetadata(null));

        /// <summary>What the tile controls, for example "GPU mode".</summary>
        public string? Label
        {
            get => (string?)GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }

        public static readonly DependencyProperty StateProperty =
            DependencyProperty.Register(nameof(State), typeof(string), typeof(QuickTile),
                new PropertyMetadata(null));

        /// <summary>The state it is in right now, for example "Standard".</summary>
        public string? State
        {
            get => (string?)GetValue(StateProperty);
            set => SetValue(StateProperty, value);
        }

        public static readonly DependencyProperty ExpandCommandProperty =
            DependencyProperty.Register(nameof(ExpandCommand), typeof(ICommand), typeof(QuickTile),
                new PropertyMetadata(null));

        /// <summary>
        /// Invoked by the chevron. When null the chevron is not shown and the whole tile
        /// is just a toggle.
        /// </summary>
        public ICommand? ExpandCommand
        {
            get => (ICommand?)GetValue(ExpandCommandProperty);
            set => SetValue(ExpandCommandProperty, value);
        }

        public static readonly DependencyProperty ExpandCommandParameterProperty =
            DependencyProperty.Register(nameof(ExpandCommandParameter), typeof(object), typeof(QuickTile),
                new PropertyMetadata(null));

        public object? ExpandCommandParameter
        {
            get => GetValue(ExpandCommandParameterProperty);
            set => SetValue(ExpandCommandParameterProperty, value);
        }

        public static readonly DependencyProperty IsEditingProperty =
            DependencyProperty.Register(nameof(IsEditing), typeof(bool), typeof(QuickTile),
                new PropertyMetadata(false));

        /// <summary>
        /// The grid is being rearranged. The chevron gives way to a control that takes
        /// the tile off the grid, and the tile outlines itself so it reads as movable
        /// rather than as a switch that is about to fire.
        /// </summary>
        public bool IsEditing
        {
            get => (bool)GetValue(IsEditingProperty);
            set => SetValue(IsEditingProperty, value);
        }

        public static readonly DependencyProperty RemoveCommandProperty =
            DependencyProperty.Register(nameof(RemoveCommand), typeof(ICommand), typeof(QuickTile),
                new PropertyMetadata(null));

        public ICommand? RemoveCommand
        {
            get => (ICommand?)GetValue(RemoveCommandProperty);
            set => SetValue(RemoveCommandProperty, value);
        }

        public static readonly DependencyProperty RemoveCommandParameterProperty =
            DependencyProperty.Register(nameof(RemoveCommandParameter), typeof(object), typeof(QuickTile),
                new PropertyMetadata(null));

        public object? RemoveCommandParameter
        {
            get => GetValue(RemoveCommandParameterProperty);
            set => SetValue(RemoveCommandParameterProperty, value);
        }
    }
}
