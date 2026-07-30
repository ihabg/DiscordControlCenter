namespace DiscordControlCenter.Application.Bots;

public interface IDiscordBotClientFactory
{
    IDiscordBotClient Create(Guid botProfileId, bool enableFullMemberAccess);
}
