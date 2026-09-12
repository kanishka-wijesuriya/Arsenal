using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Arsenal.UI.Converters
{
    public sealed class IntEqualsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not int current || !int.TryParse(parameter?.ToString(), out int expected))
                return false;
            return current == expected;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is true && int.TryParse(parameter?.ToString(), out int expected))
                return expected;
            return DependencyProperty.UnsetValue;
        }
    }
}
