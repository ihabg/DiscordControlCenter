using DiscordControlCenter.Core.Bots;
using DiscordControlCenter.Core.Common;

namespace DiscordControlCenter.Application.Bots;

public interface IBotConnectionManager : IAsyncDisposable
{
    event EventHandler<BotConnectionSnapshot>? StatusChanged;

    IReadOnlyCollection<BotConnectionSnapshot> Snapshots { get; }

    Task InitializeAsync(CancellationToken cancellationToken);
    Task<OperationResult> ConnectAsync(Guid botProfileId, CancellationToken cancellationToken);
    Task<OperationResult> DisconnectAsync(Guid botProfileId, CancellationToken cancellationToken);
    Task ConnectAllAsync(CancellationToken cancellationToken);
    Task DisconnectAllAsync(CancellationToken cancellationToken);
}
