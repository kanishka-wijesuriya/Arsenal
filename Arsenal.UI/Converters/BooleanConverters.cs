using System.Globalization;
using System.Windows.Data;

namespace Arsenal.UI.Converters
{
    public sealed class BoolToTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string[] labels = (parameter?.ToString() ?? "On|Off").Split('|');
            return value is true ? labels[0] : labels.ElementAtOrDefault(1) ?? labels[0];
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
    }

    public sealed class InverseBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    }
}
