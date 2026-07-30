using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DiscordControlCenter.Core.Bots;

namespace DiscordControlCenter.App.Converters;

public sealed class StateToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush FallbackSuccess = CreateFrozenBrush(75, 214, 146);
    private static readonly SolidColorBrush FallbackWarning = CreateFrozenBrush(255, 190, 92);
    private static readonly SolidColorBrush FallbackDanger = CreateFrozenBrush(255, 105, 130);
    private static readonly SolidColorBrush FallbackMuted = CreateFrozenBrush(152, 164, 183);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        _ = targetType;
        _ = parameter;
        _ = culture;
        return value switch
        {
            BotConnectionState.Connected => FindBrush("SuccessBrush", FallbackSuccess),
            BotConnectionState.Connecting or BotConnectionState.Reconnecting =>
                FindBrush("WarningBrush", FallbackWarning),
            BotConnectionState.Faulted => FindBrush("DangerBrush", FallbackDanger),
            _ => FindBrush("TextMutedBrush", FallbackMuted)
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Brush FindBrush(string resourceKey, Brush fallback) =>
        System.Windows.Application.Current?.TryFindResource(resourceKey) as Brush ?? fallback;

    private static SolidColorBrush CreateFrozenBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
