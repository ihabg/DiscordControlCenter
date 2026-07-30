using DiscordControlCenter.Core.Bots;

namespace DiscordControlCenter.Application.Bots;

public interface IDiscordTokenValidator
{
    Task<BotIdentity> ValidateAsync(string token, CancellationToken cancellationToken);
}
