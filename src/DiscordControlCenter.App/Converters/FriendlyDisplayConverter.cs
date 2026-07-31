using System.Globalization;
using System.Windows.Data;

namespace DiscordControlCenter.App.Converters;

/// <summary>Turns persisted enum-like identifiers into readable UI text without changing their value.</summary>
public sealed class FriendlyDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        _ = targetType;
        _ = parameter;
        _ = culture;
        if (value is null)
        {
            return "Not available";
        }

        if (value is bool completed)
        {
            return completed ? "Completed" : "Not completed";
        }

        var text = value.ToString() ?? string.Empty;
        if (string.Equals(text, "All", StringComparison.Ordinal))
        {
            return "All";
        }

        var spaced = string.Concat(text.Select((character, index) =>
            index > 0 && char.IsUpper(character) && !char.IsUpper(text[index - 1])
                ? $" {character}"
                : character.ToString()));
        return string.Join(
            " ",
            spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select((word, index) => index == 0 ? word : word.ToLowerInvariant()));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
