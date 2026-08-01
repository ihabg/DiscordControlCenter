namespace DiscordControlCenter.App.Services;

public interface IDraftDiscardConfirmationService
{
    bool ConfirmDiscard(string actionDescription);
}
