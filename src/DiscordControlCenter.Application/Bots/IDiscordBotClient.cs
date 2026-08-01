using DiscordControlCenter.Core.Bots;
using DiscordControlCenter.Application.Explorer;

namespace DiscordControlCenter.Application.Bots;

public interface IDiscordBotClient : IAsyncDisposable, IDiscordChannelWriterClient, IDiscordMessageWriterClient
{
    event EventHandler<BotConnectionSnapshot>? StatusChanged;
    event EventHandler<ExplorerCacheUpdate>? ExplorerChanged;

    BotConnectionSnapshot Snapshot { get; }

    Task ConnectAsync(string token, CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
    Task<ExplorerCacheUpdate> RefreshExplorerAsync(CancellationToken cancellationToken);
    Task LoadMembersAsync(ulong serverId, CancellationToken cancellationToken);
    Task<bool> MessageExistsAsync(ulong channelId, ulong messageId, CancellationToken cancellationToken);
}
