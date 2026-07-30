using DiscordControlCenter.Core.Bots;

namespace DiscordControlCenter.Application.Bots;

public interface IDiscordBotClient : IAsyncDisposable
{
    event EventHandler<BotConnectionSnapshot>? StatusChanged;

    BotConnectionSnapshot Snapshot { get; }

    Task ConnectAsync(string token, CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
}
