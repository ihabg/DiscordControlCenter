using System.Collections.Immutable;
using DiscordControlCenter.Core.Explorer;

namespace DiscordControlCenter.Core.Operations;

public enum ChannelOperationType
{
    CreateCategory,
    CreateTextChannels,
    CreateVoiceChannels,
    EditChannel,
    BulkRename,
    MoveChannels,
    ReorderChannels,
    CloneChannel,
    CloneCategoryStructure,
    LockChannels,
    UnlockChannels,
    SynchronizePermissions,
    RecreateStructure,
    DeleteChannels,
    DeleteCategoryOnly,
    DeleteCategoryWithChildren
}

public enum OperationRiskLevel
{
    Low,
    Moderate,
    High,
    Irreversible
}

public enum ChannelOperationState
{
    Pending,
    Running,
    Waiting,
    Cancelling,
    Completed,
    PartiallyCompleted,
    Failed,
    Stale,
    Cancelled,
    ReconciliationRequired
}

public enum OperationStepKind
{
    CreateCategory,
    CreateTextChannel,
    CreateVoiceChannel,
    ModifyChannel,
    MoveChannel,
    ReorderChannel,
    SetPermissionOverwrite,
    DeletePermissionOverwrite,
    DeleteChannel
}

public enum OperationTargetKind
{
    Server,
    Category,
    Channel,
    Role,
    Member
}

public enum OperationPreconditionKind
{
    BotConnected,
    ServerAvailable,
    TargetExists,
    TargetFingerprintMatches,
    RequiredPermission,
    SupportedChannelType,
    ParentCategoryExists,
    BackupCompleted,
    NoConflictingOperation
}

public enum OperationConfirmationKind
{
    Explicit,
    TypedText,
    TypedTextAndServerName
}

public enum OperationCompensationCapability
{
    None,
    BestEffort,
    ExactWhenTargetUnchanged
}

public enum OperationFailureKind
{
    Validation,
    PermissionDenied,
    StalePlan,
    TargetNotFound,
    Unsupported,
    BackupFailed,
    Cancelled,
    Transport,
    RateLimited,
    DiscordRejected,
    UncertainOutcome,
    ReconciliationAmbiguous,
    CompensationFailed,
    Internal
}

public enum OperationOutcomeCertainty
{
    KnownSucceeded,
    KnownFailed,
    Uncertain
}

public enum OperationReconciliationStatus
{
    NotRequired,
    ConfirmedApplied,
    ConfirmedNotApplied,
    Ambiguous,
    TimedOut,
    ManualReviewRequired
}

public sealed record ChannelPermissionOverwriteSnapshot(
    ulong TargetId,
    PermissionTargetKind TargetType,
    string TargetDisplayName,
    ulong AllowedRaw,
    ulong DeniedRaw);

public sealed record ChannelOperationStateSnapshot(
    ulong? Id,
    string Name,
    ChannelKind Kind,
    int Position,
    ulong? ParentCategoryId,
    string? ParentCategoryName,
    string? Topic,
    bool? IsNsfw,
    int? SlowModeSeconds,
    int? DefaultAutoArchiveMinutes,
    int? Bitrate,
    int? UserLimit,
    string? RegionOverride,
    ImmutableArray<ChannelPermissionOverwriteSnapshot> PermissionOverwrites)
{
    public ImmutableArray<string> AvailableTags { get; init; } = ImmutableArray<string>.Empty;
    public string? DefaultReaction { get; init; }
    public string? DefaultSortOrder { get; init; }
    public string? DefaultLayout { get; init; }
}

public sealed record OperationTarget(
    ulong Id,
    string DisplayName,
    OperationTargetKind Kind,
    ulong? ParentId,
    string Fingerprint);

public sealed record PropertyChange(
    string PropertyName,
    string? BeforeValue,
    string? AfterValue);

public sealed record PermissionOverwriteChange(
    ulong TargetId,
    PermissionTargetKind TargetType,
    string TargetDisplayName,
    ChannelPermissionOverwriteSnapshot? Before,
    ChannelPermissionOverwriteSnapshot? After,
    ImmutableArray<string> AllowedPermissionChanges,
    ImmutableArray<string> DeniedPermissionChanges);

public sealed record OperationPrecondition(
    OperationPreconditionKind Kind,
    string Description,
    bool IsSatisfied,
    string? FailureCode);

public sealed record OperationConfirmationRequirement(
    OperationConfirmationKind Kind,
    string Prompt,
    string? RequiredText);

public sealed record OperationCompensation(
    OperationCompensationCapability Capability,
    OperationStepKind StepKind,
    ulong? TargetId,
    ChannelOperationStateSnapshot? RestoreState,
    ChannelPermissionOverwriteSnapshot? RestoreOverwrite,
    string Description);

public sealed record OperationStep(
    Guid StepId,
    int Order,
    OperationStepKind Kind,
    string Description,
    OperationTarget Target,
    ChannelOperationStateSnapshot? Before,
    ChannelOperationStateSnapshot? After,
    PermissionOverwriteChange? PermissionOverwriteChange,
    bool IsDestructive,
    OperationCompensation? Compensation)
{
    public Guid? ParentResultStepId { get; init; }
    public Guid? TargetResultStepId { get; init; }
    public ImmutableArray<Guid> BatchResultStepIds { get; init; } = ImmutableArray<Guid>.Empty;
    public ImmutableArray<ChannelOperationStateSnapshot> BatchBeforeStates { get; init; } =
        ImmutableArray<ChannelOperationStateSnapshot>.Empty;
    public ImmutableArray<ChannelOperationStateSnapshot> BatchAfterStates { get; init; } =
        ImmutableArray<ChannelOperationStateSnapshot>.Empty;
}

public sealed record OperationPlan(
    Guid OperationId,
    Guid CorrelationId,
    Guid BotProfileId,
    ulong ServerId,
    string ServerNameSnapshot,
    long SourceExplorerSequence,
    DateTimeOffset CreatedAt,
    ChannelOperationType OperationType,
    string Title,
    ImmutableArray<ulong> ExactTargetIds,
    ImmutableArray<ChannelOperationStateSnapshot> ExactBeforeState,
    ImmutableArray<ChannelOperationStateSnapshot> ProposedAfterState,
    ImmutableArray<PermissionBits> RequiredBotPermissions,
    ImmutableArray<OperationPrecondition> Preconditions,
    OperationRiskLevel RiskLevel,
    ImmutableArray<OperationStep> Steps,
    int EstimatedRequestCount,
    OperationConfirmationRequirement ConfirmationRequirement,
    OperationCompensationCapability CompensationCapability,
    string? AuditReason)
{
    public int SchemaVersion { get; init; } = 2;
    public string? SourceBackupIdentifier { get; init; }
    public ImmutableArray<string> CompatibilityWarnings { get; init; } = ImmutableArray<string>.Empty;
    public RecreateCompensationPolicy RecreateCompensationPolicy { get; init; } =
        RecreateCompensationPolicy.KeepSuccessfulResources;
    public bool IsDestructive =>
        RiskLevel is OperationRiskLevel.High or OperationRiskLevel.Irreversible
        || Steps.Any(step => step.IsDestructive);
}

public sealed record OperationPreview(
    Guid OperationId,
    Guid CorrelationId,
    string Title,
    string BotDisplayName,
    string ServerName,
    OperationRiskLevel RiskLevel,
    int AffectedResourceCount,
    int EstimatedRequestCount,
    ImmutableArray<string> PermissionRequirements,
    ImmutableArray<PropertyChange> PropertyChanges,
    ImmutableArray<PermissionOverwriteChange> PermissionOverwriteChanges,
    ImmutableArray<string> Consequences,
    OperationConfirmationRequirement ConfirmationRequirement,
    string? AuditReason);

public sealed record OperationProgress(
    Guid OperationId,
    ChannelOperationState State,
    int CompletedSteps,
    int TotalSteps,
    int? CurrentStep,
    string Message,
    DateTimeOffset Timestamp);

public sealed record OperationFailure(
    OperationFailureKind Kind,
    string SafeCode,
    string SafeMessage,
    string? ExceptionType,
    bool IsRetryable,
    OperationOutcomeCertainty OutcomeCertainty);

public sealed record OperationStepResult(
    Guid StepId,
    int Order,
    string Description,
    bool Succeeded,
    bool WasCancelled,
    ulong? ResultResourceId,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    int AttemptCount,
    OperationFailure? Failure,
    bool CompensationAttempted,
    bool CompensationSucceeded);

public sealed record OperationReconciliationResult(
    OperationReconciliationStatus Status,
    string SafeSummary,
    ImmutableArray<ulong> MatchingResourceIds,
    DateTimeOffset CheckedAt);

public sealed record OperationPreflightIssue(
    string SafeCode,
    string Message,
    bool IsStale,
    ulong? TargetId);

public sealed record ChannelOperationPreflightResult(
    bool IsAllowed,
    bool IsStale,
    ImmutableArray<OperationPreflightIssue> Issues,
    ImmutableArray<OperationPrecondition> EvaluatedPreconditions,
    DateTimeOffset CheckedAt);

public sealed record ChannelOperationResult(
    Guid OperationId,
    Guid CorrelationId,
    ChannelOperationState State,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    ImmutableArray<OperationStepResult> StepResults,
    int CompletedCount,
    int FailedCount,
    int CancelledCount,
    OperationFailure? Failure,
    OperationReconciliationResult Reconciliation,
    string? BackupIdentifier,
    OperationCompensationCapability CompensationCapability,
    string CompensationSummary);

public sealed record ServerStructureBackup(
    string BackupIdentifier,
    Guid OperationId,
    Guid CorrelationId,
    Guid BotProfileId,
    ulong ServerId,
    string ServerName,
    long ExplorerSequence,
    DateTimeOffset CreatedAt,
    ImmutableArray<ChannelOperationStateSnapshot> Channels)
{
    public int SchemaVersion { get; init; } = 2;
    public string BackupReason { get; init; } = "Pre-operation structural backup";
    public ChannelOperationType SourceOperationType { get; init; } = ChannelOperationType.DeleteChannels;
}

public sealed record OperationHistoryEntry(
    Guid OperationId,
    Guid CorrelationId,
    ChannelOperationType OperationType,
    Guid BotProfileId,
    ulong ServerId,
    string ServerName,
    string TargetIds,
    string SafeDisplayNames,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    ChannelOperationState State,
    int CompletedCount,
    int FailedCount,
    int CancelledCount,
    string CompensationSummary,
    string? BackupIdentifier,
    string? SafeErrorCodes,
    long DurationMilliseconds,
    string? AuditReason,
    string PlanJson,
    string? ResultJson)
{
    public string Title { get; init; } = string.Empty;
    public OperationRiskLevel RiskLevel { get; init; }
    public int AffectedResourceCount { get; init; }
    public OperationReconciliationStatus ReconciliationStatus { get; init; }
}
