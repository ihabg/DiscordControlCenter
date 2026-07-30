using DiscordControlCenter.Application.Bots;
using Microsoft.Extensions.Logging;

namespace DiscordControlCenter.Discord;

public sealed class DiscordBotClientFactory(ILoggerFactory loggerFactory) : IDiscordBotClientFactory
{
    public IDiscordBotClient Create(Guid botProfileId) =>
        new DiscordBotClient(botProfileId, loggerFactory.CreateLogger<DiscordBotClient>());
}
