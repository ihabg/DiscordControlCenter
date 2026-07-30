namespace DiscordControlCenter.Core.Explorer;

public enum SafetyDecision
{
    Allowed,
    Denied,
    Unknown
}

public enum HierarchyReasonCode
{
    Allowed,
    MissingRequiredPermission,
    TargetAtOrAboveBot,
    TargetIsEveryone,
    TargetManagedExternally,
    BotDoesNotPossessAssignedPermission,
    TargetIsServerOwner,
    IncompleteData,
    TargetNotFound
}

public sealed record HierarchyPreflightResult(
    SafetyDecision Decision,
    HierarchyReasonCode ReasonCode,
    string Explanation,
    PermissionBits? RequiredPermission,
    int? BotHighestRolePosition,
    int? TargetHighestRolePosition,
    DataCompleteness DataCompleteness);

public enum PermissionComparisonStatus
{
    BothAllowed,
    FirstOnly,
    SecondOnly,
    BothDenied,
    Unknown,
    NotApplicable
}

public sealed record PermissionComparisonItem(
    string Group,
    string Name,
    PermissionBits Permission,
    PermissionStatus FirstStatus,
    PermissionStatus SecondStatus,
    PermissionComparisonStatus Comparison);

public sealed record PermissionComparison(
    IReadOnlyList<PermissionComparisonItem> Permissions);

public sealed record BotDiagnosticsReadModel(
    Guid BotProfileId,
    string DisplayName,
    string ConnectionState,
    int? GatewayLatencyMilliseconds,
    DateTimeOffset? LastReadyAt,
    DateTimeOffset? LastDisconnectedAt,
    DateTimeOffset? LastReconnectedAt,
    int CachedServerCount,
    int CachedChannelCount,
    int CachedRoleCount,
    int LoadedMemberCount,
    DataCompleteness MemberCompleteness,
    long LastAcceptedSequence,
    DateTimeOffset? LastSuccessfulExplorerRefresh,
    bool IsRefreshPending,
    string? RecentGatewayError,
    bool FullMemberAccessEnabled,
    bool MemberLoadingOperational,
    long VoiceStateEventCount,
    DateTimeOffset? LastVoiceStateEventAt,
    TimeSpan? CacheAge);
