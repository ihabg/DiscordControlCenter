using DiscordControlCenter.Core.Bots;

namespace DiscordControlCenter.App.Services;

public interface IUserDialogService
{
    Task<BotProfile?> ShowAddBotAsync();
    Task<BotProfile?> ShowReplaceTokenAsync(BotProfile profile);
    bool ConfirmRemove(BotProfile profile);
    void ShowError(string title, string message);
    void ShowBotError(string botName, string errorMessage);
}
