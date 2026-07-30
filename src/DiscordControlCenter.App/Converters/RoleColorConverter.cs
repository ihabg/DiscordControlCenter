using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace DiscordControlCenter.App.Converters;

public sealed class RoleColorConverter : IValueConverter
{
    public object Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture)
    {
        _ = targetType;
        _ = parameter;
        _ = culture;
        var raw = value is uint color ? color : 0u;
        return new SolidColorBrush(
            Color.FromRgb(
                (byte)((raw >> 16) & 0xff),
                (byte)((raw >> 8) & 0xff),
                (byte)(raw & 0xff)));
    }

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();
}
