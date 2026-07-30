using System.Collections.Immutable;

namespace DiscordControlCenter.Core.Explorer;

public enum ExplorerCacheState
{
    Disconnected,
    Loading,
    Ready,
    Faulted
}

public enum ServerAvailability
{
    Available,
    Unavailable
}

public enum ChannelKind
{
    Category,
    Text,
    Announcement,
    Voice,
    Stage,
    Forum,
    Media,
    Thread,
    Other
}

public enum PermissionTargetKind
{
    Role,
    User
}

public sealed record PermissionOverwriteReadModel(
    ulong TargetId,
    PermissionTargetKind TargetType,
    ulong AllowedRaw,
    ulong DeniedRaw,
    PermissionBits Allowed,
    PermissionBits Denied);

public sealed record RoleReadModel(
    ulong Id,
    string Name,
    int Position,
    PermissionBits Permissions,
    bool IsEveryone);

public sealed record ChannelReadModel(
    ulong Id,
    string Name,
    ChannelKind Kind,
    string TypeName,
    int Position,
    DateTimeOffset CreatedAt,
    ulong? CategoryId,
    string? CategoryName,
    bool? IsPermissionSynchronized,
    ImmutableArray<PermissionOverwriteReadModel> PermissionOverwrites,
    string? Topic,
    bool? IsNsfw,
    int? SlowModeSeconds,
    int? DefaultAutoArchiveMinutes,
    int? Bitrate,
    int? UserLimit,
    string? RegionOverride,
    int? ConnectedUserCount,
    ImmutableArray<string> AvailableTags,
    string? DefaultReaction,
    string? DefaultSortOrder,
    string? DefaultLayout);

public sealed record ServerReadModel(
    ulong Id,
    string Name,
    string? IconUrl,
    string? Description,
    ulong OwnerId,
    DateTimeOffset CreatedAt,
    int? ApproximateMemberCount,
    int CategoryCount,
    int TextChannelCount,
    int VoiceChannelCount,
    int ForumChannelCount,
    int StageChannelCount,
    int RoleCount,
    int EmojiCount,
    string BoostTier,
    int? BoostCount,
    string? BotNickname,
    string? BotHighestRole,
    int? BotRolePosition,
    ulong BotUserId,
    ImmutableArray<ulong> BotRoleIds,
    ImmutableArray<RoleReadModel> Roles,
    ImmutableArray<ChannelReadModel> Channels,
    ServerAvailability Availability,
    DateTimeOffset RefreshedAt);

public sealed record BotExplorerSnapshot(
    Guid BotProfileId,
    long Version,
    ExplorerCacheState State,
    ImmutableArray<ServerReadModel> Servers,
    DateTimeOffset? RefreshedAt,
    string? ErrorMessage)
{
    public static BotExplorerSnapshot Disconnected(Guid botProfileId, long version = 0) =>
        new(
            botProfileId,
            version,
            ExplorerCacheState.Disconnected,
            ImmutableArray<ServerReadModel>.Empty,
            null,
            null);
}

public sealed record ChannelGroupReadModel(
    ulong? CategoryId,
    string Name,
    int Position,
    ImmutableArray<ChannelReadModel> Channels);
