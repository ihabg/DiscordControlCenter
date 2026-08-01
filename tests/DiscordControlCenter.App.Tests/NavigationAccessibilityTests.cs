using DiscordControlCenter.App.ViewModels;

namespace DiscordControlCenter.App.Tests;

public sealed class NavigationAccessibilityTests
{
    [Fact]
    public void MessagesNavigationHasAStableAutomationId()
    {
        var item = new NavigationItem("icon", "Messages");

        Assert.Equal("DiscordControlCenter.Navigation.Messages", item.AutomationId);
    }
}
