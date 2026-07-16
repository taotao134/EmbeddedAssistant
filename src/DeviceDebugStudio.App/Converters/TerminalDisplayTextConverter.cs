using System.Globalization;
using System.Windows;
using System.Windows.Data;
using DeviceDebugStudio.App.ViewModels;

namespace DeviceDebugStudio.App.Converters;

public sealed class TerminalDisplayTextConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length >= 2 && values[0] is TerminalRecordItem item && values[1] is bool displayHex)
        {
            return item.GetDisplayContent(displayHex);
        }

        return string.Empty;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
