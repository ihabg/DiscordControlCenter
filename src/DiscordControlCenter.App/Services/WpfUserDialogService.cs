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
