using System.Collections.Immutable;
using DiscordControlCenter.Core.Common;
using DiscordControlCenter.Core.Explorer;

namespace DiscordControlCenter.Application.Explorer;

public enum ExplorerCacheUpdateKind
{
    Reset,
    ServerUpserted,
    ServerRemoved,
    MembersLoading,
    MembersBatchUpserted,
    MemberUpserted,
    MemberRemoved,
    MembersStateChanged,
    VoiceStatesChanged,
    Cleared,
    Faulted
}

public sealed record MemberCacheStateChange(
    ulong ServerId,
    ulong? MemberId,
    DataCompleteness Completeness,
    bool FullAccessEnabled,
    ImmutableArray<MemberReadModel> Members,
    int? ExpectedMemberCount,
    DateTimeOffset? RefreshedAt,
    string? ErrorMessage);

public sealed record VoiceStateCacheChange(
    ulong ServerId,
    ulong UserId,
    MemberReadModel? Member,
    VoiceStateReadModel? VoiceState);

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
    public MemberCacheStateChange? MemberChange { get; init; }
    public ImmutableArray<VoiceStateCacheChange> VoiceChanges { get; init; } =
        ImmutableArray<VoiceStateCacheChange>.Empty;

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

    public static ExplorerCacheUpdate Members(
        Guid botProfileId,
        long sequence,
        ExplorerCacheUpdateKind kind,
        MemberCacheStateChange change,
        DateTimeOffset occurredAt) =>
        new(
            botProfileId,
            sequence,
            kind,
            ImmutableArray<ServerReadModel>.Empty,
            null,
            change.ServerId,
            occurredAt,
            change.ErrorMessage)
        {
            MemberChange = change
        };

    public static ExplorerCacheUpdate Voice(
        Guid botProfileId,
        long sequence,
        IEnumerable<VoiceStateCacheChange> changes,
        DateTimeOffset occurredAt) =>
        new(
            botProfileId,
            sequence,
            ExplorerCacheUpdateKind.VoiceStatesChanged,
            ImmutableArray<ServerReadModel>.Empty,
            null,
            null,
            occurredAt,
            null)
        {
            VoiceChanges = changes.ToImmutableArray()
        };

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
    Task<OperationResult> LoadMembersAsync(
        Guid botProfileId,
        ulong serverId,
        CancellationToken cancellationToken);
    IReadOnlyList<BotDiagnosticsReadModel> GetDiagnostics();
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

    PermissionResolution ResolveMember(
        Guid botProfileId,
        long snapshotVersion,
        ServerReadModel server,
        MemberReadModel member,
        ChannelReadModel? channel);

    PermissionResolution ResolveRole(
        Guid botProfileId,
        long snapshotVersion,
        ServerReadModel server,
        RoleReadModel role,
        ChannelReadModel? channel);

    PermissionComparison Compare(
        PermissionResolution first,
        PermissionResolution second);

    void Invalidate(Guid botProfileId, ulong? serverId = null);
}

public interface IRoleHierarchySafetyService
{
    HierarchyPreflightResult CanManageRole(ServerReadModel server, RoleReadModel targetRole);
    HierarchyPreflightResult CanAssignRole(ServerReadModel server, RoleReadModel targetRole);
    HierarchyPreflightResult CanRemoveRole(ServerReadModel server, RoleReadModel targetRole);
    HierarchyPreflightResult CanModerateMember(ServerReadModel server, MemberReadModel targetMember);
    HierarchyPreflightResult CanChangeNickname(ServerReadModel server, MemberReadModel targetMember);
}
