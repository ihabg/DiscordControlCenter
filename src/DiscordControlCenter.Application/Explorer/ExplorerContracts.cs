using System.Collections.Immutable;
using DiscordControlCenter.Core.Common;
using DiscordControlCenter.Core.Explorer;

namespace DiscordControlCenter.Application.Explorer;

public enum ExplorerCacheUpdateKind
{
    Reset,
    ServerUpserted,
    ServerRemoved,
    Cleared,
    Faulted
}

public sealed record ExplorerCacheUpdate(
    Guid BotProfileId,
    long Sequence,
    ExplorerCacheUpdateKind Kind,
    ImmutableArray<ServerReadModel> Servers,
    ServerReadModel? Server,
    ulong? ServerId,
    DateTimeOffset OccurredAt,
    string? ErrorMessage)
{
    public static ExplorerCacheUpdate Reset(
        Guid botProfileId,
        long sequence,
        IEnumerable<ServerReadModel> servers,
        DateTimeOffset occurredAt) =>
        new(
            botProfileId,
            sequence,
            ExplorerCacheUpdateKind.Reset,
            servers.ToImmutableArray(),
            null,
            null,
            occurredAt,
            null);

    public static ExplorerCacheUpdate Upsert(
        Guid botProfileId,
        long sequence,
        ServerReadModel server,
        DateTimeOffset occurredAt) =>
        new(
            botProfileId,
            sequence,
            ExplorerCacheUpdateKind.ServerUpserted,
            ImmutableArray<ServerReadModel>.Empty,
            server,
            server.Id,
            occurredAt,
            null);

    public static ExplorerCacheUpdate Remove(
        Guid botProfileId,
        long sequence,
        ulong serverId,
        DateTimeOffset occurredAt) =>
        new(
            botProfileId,
            sequence,
            ExplorerCacheUpdateKind.ServerRemoved,
            ImmutableArray<ServerReadModel>.Empty,
            null,
            serverId,
            occurredAt,
            null);

    public static ExplorerCacheUpdate Clear(
        Guid botProfileId,
        long sequence,
        DateTimeOffset occurredAt) =>
        new(
            botProfileId,
            sequence,
            ExplorerCacheUpdateKind.Cleared,
            ImmutableArray<ServerReadModel>.Empty,
            null,
            null,
            occurredAt,
            null);

    public static ExplorerCacheUpdate Fault(
        Guid botProfileId,
        long sequence,
        string error,
        DateTimeOffset occurredAt) =>
        new(
            botProfileId,
            sequence,
            ExplorerCacheUpdateKind.Faulted,
            ImmutableArray<ServerReadModel>.Empty,
            null,
            null,
            occurredAt,
            error);
}

public sealed record ExplorerCacheChanged(
    Guid BotProfileId,
    ExplorerCacheUpdateKind Kind,
    ulong? ServerId,
    BotExplorerSnapshot Snapshot);

public interface IBotExplorerService
{
    event EventHandler<ExplorerCacheChanged>? CacheChanged;

    BotExplorerSnapshot GetSnapshot(Guid botProfileId);
    Task<OperationResult> RefreshAsync(Guid botProfileId, CancellationToken cancellationToken);
}

public interface IPermissionResolutionService
{
    PermissionResolution ResolveServer(
        Guid botProfileId,
        long snapshotVersion,
        ServerReadModel server);

    PermissionResolution ResolveChannel(
        Guid botProfileId,
        long snapshotVersion,
        ServerReadModel server,
        ChannelReadModel channel);

    void Invalidate(Guid botProfileId, ulong? serverId = null);
}
