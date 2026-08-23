using System;
using System.Globalization;
using System.Windows.Data;
using RouterPilot.ViewModels;

namespace RouterPilot.Converters;

public sealed class DataStatisticsValueConverter : IValueConverter
{
    public static DataStatisticsValueConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is long bytes ? DataStatisticsViewModel.FormatBytes(bytes) : "0 B";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
