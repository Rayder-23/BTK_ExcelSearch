using System.Globalization;
using System.Windows.Data;

namespace ExcelSearch.Views;

/// <summary>Returns the bound number minus the number in ConverterParameter (never below 0). Used to size the row details to the visible grid.</summary>
public sealed class OffsetConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var offset = parameter is null ? 0 : double.Parse(parameter.ToString()!, CultureInfo.InvariantCulture);
        return value is double d ? Math.Max(0, d - offset) : 0d;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
