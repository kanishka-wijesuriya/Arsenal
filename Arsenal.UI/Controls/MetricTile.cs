using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace Arsenal.UI.Controls
{
    /// <summary>
    /// A dashboard readout: glyph and label on top, a large primary number, an
    /// optional secondary figure beside it, an optional bar and a caption.
    ///
    /// Everything is optional so the same tile serves temperature, fan RPM and
    /// battery health without each page re-inventing the internal geometry.
    /// </summary>
    public class MetricTile : System.Windows.Controls.Control
    {

        public static readonly DependencyProperty IconProperty =
            DependencyProperty.Register(nameof(Icon), typeof(SymbolRegular), typeof(MetricTile),
                new PropertyMetadata(SymbolRegular.Empty));

        public SymbolRegular Icon
        {
            get => (SymbolRegular)GetValue(IconProperty);
            set => SetValue(IconProperty, value);
        }

        public static readonly DependencyProperty LabelProperty =
            DependencyProperty.Register(nameof(Label), typeof(string), typeof(MetricTile),
                new PropertyMetadata(null));

        public string? Label
        {
            get => (string?)GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }

        public static readonly DependencyProperty ValueProperty =
            DependencyProperty.Register(nameof(Value), typeof(string), typeof(MetricTile),
                new PropertyMetadata(null));

        /// <summary>Primary figure, rendered large.</summary>
        public string? Value
        {
            get => (string?)GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        public static readonly DependencyProperty SecondaryValueProperty =
            DependencyProperty.Register(nameof(SecondaryValue), typeof(string), typeof(MetricTile),
                new PropertyMetadata(null));

        /// <summary>Companion figure sharing the baseline with <see cref="Value"/>.</summary>
        public string? SecondaryValue
        {
            get => (string?)GetValue(SecondaryValueProperty);
            set => SetValue(SecondaryValueProperty, value);
        }

        public static readonly DependencyProperty DetailProperty =
            DependencyProperty.Register(nameof(Detail), typeof(string), typeof(MetricTile),
                new PropertyMetadata(null));

        /// <summary>Caption below the figure. Hidden when empty.</summary>
        public string? Detail
        {
            get => (string?)GetValue(DetailProperty);
            set => SetValue(DetailProperty, value);
        }

        public static readonly DependencyProperty SecondaryDetailProperty =
            DependencyProperty.Register(nameof(SecondaryDetail), typeof(string), typeof(MetricTile),
                new PropertyMetadata(null));

        /// <summary>
        /// A second caption line under <see cref="Detail"/>. For a reading that has two
        /// halves worth stating together - how long the battery takes to fill, and how
        /// long it then lasts - rather than one replacing the other as the state flips.
        /// </summary>
        public string? SecondaryDetail
        {
            get => (string?)GetValue(SecondaryDetailProperty);
            set => SetValue(SecondaryDetailProperty, value);
        }

        public static readonly DependencyProperty SecondaryDetailIconProperty =
            DependencyProperty.Register(nameof(SecondaryDetailIcon), typeof(SymbolRegular), typeof(MetricTile),
                new PropertyMetadata(SymbolRegular.Empty));

        public SymbolRegular SecondaryDetailIcon
        {
            get => (SymbolRegular)GetValue(SecondaryDetailIconProperty);
            set => SetValue(SecondaryDetailIconProperty, value);
        }

        public static readonly DependencyProperty ProgressProperty =
            DependencyProperty.Register(nameof(Progress), typeof(double), typeof(MetricTile),
                new PropertyMetadata(0d));

        /// <summary>Bar value from 0 to 100. Only drawn when <see cref="ShowProgress"/>.</summary>
        public double Progress
        {
            get => (double)GetValue(ProgressProperty);
            set => SetValue(ProgressProperty, value);
        }

        public static readonly DependencyProperty ProgressCapProperty =
            DependencyProperty.Register(nameof(ProgressCap), typeof(double), typeof(MetricTile),
                new PropertyMetadata(0d));

        /// <summary>
        /// Draws a marker across the bar at this value, from 0 to 100. Used to show
        /// where charging stops when a battery limit is in force. Zero hides it.
        /// </summary>
        public double ProgressCap
        {
            get => (double)GetValue(ProgressCapProperty);
            set => SetValue(ProgressCapProperty, value);
        }

        public static readonly DependencyProperty DetailIconProperty =
            DependencyProperty.Register(nameof(DetailIcon), typeof(SymbolRegular), typeof(MetricTile),
                new PropertyMetadata(SymbolRegular.Empty));

        /// <summary>Optional glyph beside the caption.</summary>
        public SymbolRegular DetailIcon
        {
            get => (SymbolRegular)GetValue(DetailIconProperty);
            set => SetValue(DetailIconProperty, value);
        }

        public static readonly DependencyProperty ShowProgressProperty =
            DependencyProperty.Register(nameof(ShowProgress), typeof(bool), typeof(MetricTile),
                new PropertyMetadata(false));

        public bool ShowProgress
        {
            get => (bool)GetValue(ShowProgressProperty);
            set => SetValue(ShowProgressProperty, value);
        }
    }
}
