using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Nodepad.Desktop.Converters;

public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string hex || string.IsNullOrWhiteSpace(hex))
        {
            return System.Windows.Media.Brushes.Transparent;
        }

        return (System.Windows.Media.Brush)new BrushConverter().ConvertFromString(hex)!;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
