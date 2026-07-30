using System.Windows;
using DiscordControlCenter.App.ViewModels;
using DiscordControlCenter.App.Views;
using DiscordControlCenter.Application.Bots;
using DiscordControlCenter.Core.Bots;

namespace DiscordControlCenter.App.Services;

public sealed class WpfUserDialogService(IBotProfileService botProfileService) : IUserDialogService
{
    public Task<BotProfile?> ShowAddBotAsync()
    {
        var viewModel = new AddBotDialogViewModel(botProfileService);
        var dialog = new AddBotWindow(viewModel)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        var result = dialog.ShowDialog();
        return Task.FromResult(result == true ? viewModel.CreatedProfile : null);
    }

    public Task<BotProfile?> ShowReplaceTokenAsync(BotProfile profile)
    {
        var viewModel = new ReplaceTokenDialogViewModel(botProfileService, profile);
        var dialog = new ReplaceTokenWindow(viewModel)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        var result = dialog.ShowDialog();
        return Task.FromResult(result == true ? viewModel.UpdatedProfile : null);
    }

    public bool ConfirmRemove(BotProfile profile) =>
        MessageBox.Show(
            System.Windows.Application.Current.MainWindow,
            $"Remove “{profile.DisplayName}”?\n\nThe protected token and local profile will be deleted. Audit history is retained. This does not delete the bot from Discord.",
            "Remove bot profile",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;

    public bool ConfirmFullMemberAccessChange(BotProfile profile, bool enabled) =>
        MessageBox.Show(
            System.Windows.Application.Current.MainWindow,
            enabled
                ? $"Enable full member access for “{profile.DisplayName}”?\n\nBefore continuing, enable Server Members Intent on this bot's page in the Discord Developer Portal. The application will reconnect only this bot and request the privileged GuildMembers intent. If Discord rejects it, the bot will enter a safe faulted state."
                : $"Disable full member access for “{profile.DisplayName}”?\n\nThe application will reconnect only this bot without GuildMembers. The Members page will return to clearly labeled limited mode.",
            enabled ? "Enable privileged member access" : "Disable full member access",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;

    public void ShowError(string title, string message) =>
        MessageBox.Show(
            System.Windows.Application.Current.MainWindow,
            message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Error);

    public void ShowBotError(string botName, string errorMessage) =>
        ShowError($"Connection details — {botName}", errorMessage);
}
