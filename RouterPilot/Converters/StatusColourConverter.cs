using System;
using System.Globalization;
using System.Windows.Data;
using RouterPilot.Models;

namespace RouterPilot.Converters;
public sealed class StatusColourConverter : IValueConverter
{
    public static StatusColourConverter Instance { get; } = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is RouterPilotStatus status ? RouterPilotStatusPresentation.Colour(status) : RouterPilotStatusPresentation.Colour(RouterPilotStatus.NotAvailable);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
