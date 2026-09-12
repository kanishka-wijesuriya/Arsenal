using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace Arsenal.UI.Controls
{
    /// <summary>
    /// One line of a settings card: an optional icon, a label, an optional
    /// description and the control that changes the value.
    ///
    /// Every settings line in the app uses this so label baselines, row heights and
    /// the control column line up without any page hand-rolling a Grid.
    /// </summary>
    public class SettingsRow : ContentControl
    {
        public static readonly DependencyProperty HeaderProperty =
            DependencyProperty.Register(nameof(Header), typeof(string), typeof(SettingsRow),
                new PropertyMetadata(null));

        /// <summary>Primary label for the row.</summary>
        public string? Header
        {
            get => (string?)GetValue(HeaderProperty);
            set => SetValue(HeaderProperty, value);
        }

        public static readonly DependencyProperty DescriptionProperty =
            DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingsRow),
                new PropertyMetadata(null));

        /// <summary>Secondary line under the header. Hidden when empty.</summary>
        public string? Description
        {
            get => (string?)GetValue(DescriptionProperty);
            set => SetValue(DescriptionProperty, value);
        }

        public static readonly DependencyProperty IconProperty =
            DependencyProperty.Register(nameof(Icon), typeof(SymbolRegular), typeof(SettingsRow),
                new PropertyMetadata(SymbolRegular.Empty));

        /// <summary>Optional leading glyph. Left out entirely when Empty.</summary>
        public SymbolRegular Icon
        {
            get => (SymbolRegular)GetValue(IconProperty);
            set => SetValue(IconProperty, value);
        }

        public static readonly DependencyProperty ShowDividerProperty =
            DependencyProperty.Register(nameof(ShowDivider), typeof(bool), typeof(SettingsRow),
                new PropertyMetadata(false));

        /// <summary>
        /// Hairline above the row. Set by <see cref="SettingsGroup"/> so that only the
        /// gaps between visible rows are drawn.
        /// </summary>
        public bool ShowDivider
        {
            get => (bool)GetValue(ShowDividerProperty);
            set => SetValue(ShowDividerProperty, value);
        }

        public static readonly DependencyProperty ContentBelowProperty =
            DependencyProperty.Register(nameof(ContentBelow), typeof(object), typeof(SettingsRow),
                new PropertyMetadata(null));

        /// <summary>
        /// Extra content laid out on its own line under the label, full row width.
        /// Used for choice groups and editors that will not fit in the right column.
        /// </summary>
        public object? ContentBelow
        {
            get => GetValue(ContentBelowProperty);
            set => SetValue(ContentBelowProperty, value);
        }
    }
}
