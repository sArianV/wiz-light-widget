using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace WizLightWidget.Converters;

/// <summary>
/// Compara el color de fondo de un swatch contra el color actual del foco (bulb.Color)
/// para mostrar un anillo indicando cuál está activo.
/// </summary>
public class SwatchActiveConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length == 2 && values[0] is SolidColorBrush swatchBrush && values[1] is Color current)
            return swatchBrush.Color == current ? Visibility.Visible : Visibility.Collapsed;

        return Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
