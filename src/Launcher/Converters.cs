using System.Globalization;
using System.Windows.Data;

namespace PolyLab.Launcher;

// Progress 0..1 -> bar width in px (ConverterParameter = full width).
public sealed class Bar : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var frac = value is double d ? Math.Clamp(d, 0, 1) : 0;
        var max = double.TryParse(parameter as string, NumberStyles.Any, CultureInfo.InvariantCulture, out var m) ? m : 100;
        return Math.Max(0.01, frac * max);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
