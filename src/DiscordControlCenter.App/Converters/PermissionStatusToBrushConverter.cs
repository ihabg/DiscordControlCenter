using System.Globalization;
using System.Windows;
using System.Windows.Data;
using DiscordControlCenter.Core.Explorer;

namespace DiscordControlCenter.App.Converters;

public sealed class PermissionStatusToBrushConverter : IValueConverter
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
        var resourceKey = value switch
        {
            PermissionStatus.Allowed => "SuccessBrush",
            PermissionStatus.AllowedThroughAdministrator => "WarningBrush",
            PermissionStatus.NotApplicable => "TextMutedBrush",
            _ => "DangerTextBrush"
        };
        return System.Windows.Application.Current.FindResource(resourceKey);
    }

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();
}
