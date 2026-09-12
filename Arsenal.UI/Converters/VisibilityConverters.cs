using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Wpf.Ui.Controls;

namespace Arsenal.UI.Converters
{
    /// <summary>
    /// Collapses a slot when its text is missing, so optional descriptions and captions
    /// take no vertical space instead of leaving a blank line.
    /// </summary>
    public sealed class EmptyStringToCollapsedConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => System.Windows.Data.Binding.DoNothing;
    }

    /// <summary>Collapses an icon slot that was never given a glyph.</summary>
    public sealed class EmptySymbolToCollapsedConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is SymbolRegular symbol && symbol != SymbolRegular.Empty
                ? Visibility.Visible
                : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => System.Windows.Data.Binding.DoNothing;
    }

    /// <summary>
    /// Shows a slot only while a collection is empty. Used for the "nothing here yet"
    /// line so it disappears the moment real results arrive.
    /// </summary>
    public sealed class EmptyCountToVisibleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is int count && count > 0 ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => System.Windows.Data.Binding.DoNothing;
    }

    /// <summary>Shows a slot only while a flag is false. The mirror of the built-in one.</summary>
    public sealed class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is true ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => System.Windows.Data.Binding.DoNothing;
    }

    /// <summary>
    /// Hides a slot without giving up the space it takes.
    /// </summary>
    /// <remarks>
    /// For a set of states that share one cell and must not resize it as they swap.
    /// Collapsed removes an element from measurement, so the cell shrinks to whichever
    /// state happens to be showing - and where that cell is in a shared-size column, one
    /// row changing state resizes the column for every row. Hidden keeps every state
    /// measured, so the cell is the size of the largest of them at all times and nothing
    /// moves when the state changes.
    /// </remarks>
    public sealed class BoolToVisibleOrHiddenConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is true ? Visibility.Visible : Visibility.Hidden;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => System.Windows.Data.Binding.DoNothing;
    }

    /// <summary>
    /// True for any value other than zero. Used by tiles whose "on" state is a level
    /// rather than a flag, such as the keyboard backlight.
    /// </summary>
    public sealed class NonZeroToBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is int number && number != 0;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => System.Windows.Data.Binding.DoNothing;
    }

    /// <summary>Collapses a slot that was given no content at all.</summary>
    public sealed class NullToCollapsedConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is null ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => System.Windows.Data.Binding.DoNothing;
    }
}
