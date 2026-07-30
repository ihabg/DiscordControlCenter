using System.Globalization;
using System.Windows.Data;
using DiscordControlCenter.Core.Explorer;

namespace DiscordControlCenter.App.Converters;

public sealed class PermissionStatusToTextConverter : IValueConverter
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
        return value switch
        {
            PermissionStatus.Allowed => "Allowed",
            PermissionStatus.Denied => "Denied",
            PermissionStatus.NotApplicable => "Not applicable",
            PermissionStatus.AllowedThroughAdministrator => "Allowed through Administrator",
            PermissionStatus.Unknown => "Unknown due to incomplete data",
            _ => "Unavailable"
        };
    }

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();
}
