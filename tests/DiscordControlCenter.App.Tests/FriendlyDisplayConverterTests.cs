using System.Globalization;
using DiscordControlCenter.App.Converters;
using DiscordControlCenter.Core.Operations;

namespace DiscordControlCenter.App.Tests;

public sealed class FriendlyDisplayConverterTests
{
    [Theory]
    [InlineData(ChannelOperationType.CreateCategory, "Create category")]
    [InlineData(ManualReconciliationResolution.KeepCurrentStateAndStop, "Keep current state and stop")]
    [InlineData(OperationHistorySort.Newest, "Newest")]
    public void ConvertFormatsIdentifiersForDisplay(object value, string expected)
    {
        var converter = new FriendlyDisplayConverter();

        var display = converter.Convert(value, typeof(string), null!, CultureInfo.InvariantCulture);

        Assert.Equal(expected, display);
    }

    [Theory]
    [InlineData(true, "Completed")]
    [InlineData(false, "Not completed")]
    public void ConvertFormatsBooleanStatus(object value, string expected)
    {
        var converter = new FriendlyDisplayConverter();

        Assert.Equal(expected, converter.Convert(value, typeof(string), null!, CultureInfo.InvariantCulture));
    }
}
