using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Cache;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace DiscordControlCenter.App.Converters;

public sealed class CachedImageConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<string, BitmapImage> Cache =
        new(StringComparer.Ordinal);

    public object? Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture)
    {
        _ = targetType;
        _ = parameter;
        _ = culture;
        if (value is not string url
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http"))
        {
            return null;
        }

        try
        {
            return Cache.GetOrAdd(
                url,
                _ =>
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.UriSource = uri;
                    image.DecodePixelWidth = 64;
                    image.CacheOption = BitmapCacheOption.OnDemand;
                    image.UriCachePolicy = new RequestCachePolicy(RequestCacheLevel.CacheIfAvailable);
                    image.EndInit();
                    return image;
                });
        }
        catch (Exception)
        {
            Cache.TryRemove(url, out _);
            return null;
        }
    }

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();
}
