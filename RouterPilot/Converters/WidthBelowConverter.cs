using System;
using System.Globalization;
using System.Windows.Data;

namespace RouterPilot.Converters;

public sealed class WidthBelowConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double width && double.TryParse(parameter?.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out double limit) && width < limit;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
