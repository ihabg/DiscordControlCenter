using System.Collections.Immutable;
using DiscordControlCenter.Core.Explorer;

namespace DiscordControlCenter.Core.Operations;

public enum BackupCompatibility
{
    FullySupported,
    PartiallySupported,
    Unsupported,
    Corrupt,
    NewerSchema
}

public enum BackupSort
{
    Newest,
    Oldest,
    Server,
    ResourceCount
}

public enum OperationHistorySort
{
    Newest,
    Oldest,
    Duration,
    AffectedResources
}

public enum RecreateCompensationPolicy
{
    KeepSuccessfulResources,
    AttemptCleanupCreatedResources,
    StopForManualReview
}

public enum RoleMappingChoice
{
    ExactId,
    SuggestedName,
    Manual,
    Everyone,
    Skip
}

public enum RecoveryClassification
{
    CompletedAfterReconciliation,
    PartiallyCompleted,
    NotStarted,
    ManualReviewRequired,
    UnableToInspect,
    UnsupportedPlanSchema
}

public enum ManualReconciliationResolution
{
    MarkCompleted,
    MarkNotCompleted,
    LinkCreatedResource,
    KeepCurrentStateAndStop,
    GenerateCorrectivePlan,
    AttemptSafeCompensation,
    ArchiveWithWarning
}

public sealed record PagedResult<T>(
    ImmutableArray<T> Items,
    int PageNumber,
    int PageSize,
    int TotalCount)
{
    public int TotalPages => TotalCount == 0
        ? 0
        : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

public sealed record BackupQuery(
    string? SearchText,
    Guid? BotProfileId,
    ulong? ServerId,
    DateTimeOffset? CreatedFrom,
    DateTimeOffset? CreatedTo,
    ChannelOperationType? SourceOperationType,
    BackupCompatibility? Compatibility,
    BackupSort Sort,
    int PageNumber,
    int PageSize);

public sealed record BackupCatalogItem(
    string BackupIdentifier,
    Guid OperationId,
    Guid CorrelationId,
    Guid BotProfileId,
    ulong ServerId,
    string ServerName,
    DateTimeOffset CreatedAt,
    string BackupReason,
    ChannelOperationType SourceOperationType,
    int CategoryCount,
    int ChannelCount,
    int PermissionOverwriteCount,
    long ExplorerSequence,
    int SchemaVersion,
    bool IsPinned,
    long SizeBytes,
    BackupCompatibility Compatibility,
    bool IsServerAccessible,
    bool AllReferencedRolesExist,
    string? SafeIssue);

public sealed record BackupDetail(
    BackupCatalogItem Catalog,
    ServerStructureBackup? Backup,
    ImmutableArray<string> UnsupportedProperties,
    ImmutableArray<ulong> MissingRoleIds,
    ImmutableArray<string> UnrecoverableData,
    string? SafeTechnicalJson);

public sealed record OperationHistoryQuery(
    string? SearchText,
    Guid? BotProfileId,
    ulong? ServerId,
    ChannelOperationType? OperationType,
    ChannelOperationState? State,
    OperationRiskLevel? RiskLevel,
    DateTimeOffset? CreatedFrom,
    DateTimeOffset? CreatedTo,
    bool? HasBackup,
    bool? RequiresManualReconciliation,
    OperationHistorySort Sort,
    int PageNumber,
    int PageSize);

public sealed record OperationHistoryDetail(
    OperationHistoryEntry Entry,
    OperationPlan? Plan,
    ChannelOperationResult? Result,
    ImmutableArray<OperationStateTransition> Timeline,
    ImmutableArray<ManualReconciliationDecision> ManualDecisions,
    string? SafeIssue);

public sealed record OperationStateTransition(
    long Id,
    Guid OperationId,
    ChannelOperationState State,
    DateTimeOffset Timestamp,
    string ReasonCode,
    string SafeSummary);

public sealed record ManualReconciliationDecision(
    long Id,
    Guid OperationId,
    Guid CorrelationId,
    Guid StepId,
    ManualReconciliationResolution Resolution,
    DateTimeOffset Timestamp,
    string SafeExplanation,
    ImmutableArray<ulong> RelevantResourceIds);

public sealed record RecoveryAssessment(
    Guid OperationId,
    Guid CorrelationId,
    RecoveryClassification Classification,
    string SafeSummary,
    ImmutableArray<OperationStepResult> ReconciledSteps,
    bool RequiresUserApproval,
    bool CanGenerateCorrectivePlan);

public sealed record RoleMapping(
    ulong OriginalTargetId,
    PermissionTargetKind TargetType,
    string OriginalDisplayName,
    ulong? TargetId,
    string? TargetDisplayName,
    RoleMappingChoice Choice,
    bool IsCritical,
    bool IsResolved);

public sealed record RecreateResourceSelection(
    int BackupIndex,
    bool Include,
    string ProposedName,
    ulong? ExistingCategoryId,
    bool RecreateUncategorized);

public sealed record RecreateStructureRequest(
    Guid BotProfileId,
    ulong ServerId,
    string BackupIdentifier,
    ServerStructureBackup Backup,
    ImmutableArray<RecreateResourceSelection> Resources,
    ImmutableArray<RoleMapping> RoleMappings,
    bool IncludePermissionOverwrites,
    RecreateCompensationPolicy CompensationPolicy,
    string? AuditReason);

public sealed record BackupRetentionPolicy(
    bool KeepIndefinitely,
    int? MaximumAgeDays,
    int? NewestPerServer,
    bool PreserveFailedOperationBackups,
    long? MaximumStorageBytes);

public sealed record BackupCleanupCandidate(
    string BackupIdentifier,
    string ServerName,
    DateTimeOffset CreatedAt,
    long SizeBytes,
    string Reason);

public sealed record BackupCleanupPreview(
    ImmutableArray<BackupCleanupCandidate> Candidates,
    long EstimatedBytesReclaimed,
    DateTimeOffset EvaluatedAt);

public sealed record VoiceChannelCapabilities(
    int MinimumBitrate,
    int? MaximumBitrate,
    int MaximumUserLimit,
    ImmutableArray<string> SupportedRegions,
    bool IsBitrateCapabilityCertain,
    string Source);

public sealed record VoiceChannelValidationResult(
    bool IsValid,
    ImmutableArray<string> Errors,
    ImmutableArray<string> Warnings,
    VoiceChannelCapabilities Capabilities);

public sealed record SafeOperationExportRow(
    Guid OperationId,
    Guid CorrelationId,
    string Title,
    ChannelOperationType OperationType,
    Guid BotProfileId,
    ulong ServerId,
    string ServerName,
    OperationRiskLevel Risk,
    ChannelOperationState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    long DurationMilliseconds,
    int AffectedResourceCount,
    int CompletedCount,
    int FailedCount,
    int CancelledCount,
    string? BackupIdentifier,
    OperationReconciliationStatus ReconciliationStatus,
    string? SafeErrorCodes,
    string? AuditReason);

public sealed record SafeBackupExportRow(
    string BackupIdentifier,
    Guid CorrelationId,
    Guid OperationId,
    Guid BotProfileId,
    ulong ServerId,
    string ServerName,
    DateTimeOffset CreatedAt,
    ChannelOperationType SourceOperationType,
    int CategoryCount,
    int ChannelCount,
    int PermissionOverwriteCount,
    int SchemaVersion,
    bool IsPinned,
    long SizeBytes);

public sealed record SafeOperationExport(
    int SchemaVersion,
    DateTimeOffset ExportedAt,
    ImmutableArray<string> ExcludedSensitiveFields,
    ImmutableArray<SafeOperationExportRow> Operations,
    ImmutableArray<SafeBackupExportRow> Backups);
