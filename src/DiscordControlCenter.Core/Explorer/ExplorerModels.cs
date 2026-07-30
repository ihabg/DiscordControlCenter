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

public enum DataCompleteness
{
    Unavailable,
    Limited,
    Loading,
    Partial,
    Complete,
    Cancelled,
    Failed
}

public sealed record VoiceStateReadModel(
    ulong UserId,
    string DisplayName,
    bool IsBot,
    ulong ChannelId,
    string ChannelName,
    bool IsSelfMuted,
    bool IsSelfDeafened,
    bool IsServerMuted,
    bool IsServerDeafened,
    bool IsStreaming,
    bool IsVideoing,
    bool IsSuppressed,
    DateTimeOffset? RequestToSpeakAt);

public sealed record MemberReadModel(
    ulong Id,
    string Username,
    string? GlobalDisplayName,
    string? Nickname,
    string DisplayName,
    string? AvatarUrl,
    bool IsBot,
    DateTimeOffset CreatedAt,
    DateTimeOffset? JoinedAt,
    ImmutableArray<ulong> RoleIds,
    string? HighestRoleName,
    int? HighestRolePosition,
    DateTimeOffset? BoostStartedAt,
    bool? IsPending,
    DateTimeOffset? TimedOutUntil,
    VoiceStateReadModel? VoiceState,
    bool RolesAreComplete);

public sealed record MemberCollectionReadModel(
    DataCompleteness Completeness,
    bool FullAccessEnabled,
    ImmutableArray<MemberReadModel> Members,
    int? ExpectedMemberCount,
    DateTimeOffset? LastRefreshedAt,
    string? ErrorMessage)
{
    public int LoadedMemberCount => Members.Length;
    public bool IsComplete => Completeness == DataCompleteness.Complete;

    public static MemberCollectionReadModel Limited(
        bool fullAccessEnabled,
        IEnumerable<MemberReadModel>? visibleMembers,
        int? expectedMemberCount,
        DateTimeOffset? refreshedAt = null) =>
        new(
            fullAccessEnabled ? DataCompleteness.Partial : DataCompleteness.Limited,
            fullAccessEnabled,
            visibleMembers?.DistinctBy(member => member.Id).ToImmutableArray()
                ?? ImmutableArray<MemberReadModel>.Empty,
            expectedMemberCount,
            refreshedAt,
            null);
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
    bool IsEveryone)
{
    public uint ColorRaw { get; init; }
    public bool IsHoisted { get; init; }
    public bool IsMentionable { get; init; }
    public bool IsManaged { get; init; }
    public bool IsBotManaged { get; init; }
    public string? IconUrl { get; init; }
    public string? UnicodeEmoji { get; init; }
    public string? TagsSummary { get; init; }
    public ulong PermissionRaw { get; init; }
    public ImmutableArray<string> PermissionNames { get; init; } =
        ImmutableArray<string>.Empty;
    public int? MemberCount { get; init; }
    public DataCompleteness MemberCountCompleteness { get; init; } = DataCompleteness.Unavailable;
}

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
    string? DefaultLayout)
{
    public ImmutableArray<VoiceStateReadModel> VoiceMembers { get; init; } =
        ImmutableArray<VoiceStateReadModel>.Empty;
}

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
    DateTimeOffset RefreshedAt)
{
    public MemberCollectionReadModel Members { get; init; } =
        MemberCollectionReadModel.Limited(false, null, null);
}

public sealed record BotExplorerSnapshot(
    Guid BotProfileId,
    long Version,
    ExplorerCacheState State,
    ImmutableArray<ServerReadModel> Servers,
    DateTimeOffset? RefreshedAt,
    string? ErrorMessage)
{
    public long LastAcceptedSequence { get; init; } = -1;
    public DateTimeOffset? LastSuccessfulRefreshAt { get; init; }
    public bool IsRefreshPending { get; init; }

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
