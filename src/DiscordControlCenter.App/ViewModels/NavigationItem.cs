namespace DiscordControlCenter.App.ViewModels;

public sealed record NavigationItem(string Icon, string Label)
{
    public string AutomationId => $"DiscordControlCenter.Navigation.{Label.Replace(" ", string.Empty, StringComparison.Ordinal)}";
}
