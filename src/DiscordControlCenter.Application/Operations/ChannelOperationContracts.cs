using System.Collections.Immutable;
using DiscordControlCenter.Core.Explorer;
using DiscordControlCenter.Core.Operations;

namespace DiscordControlCenter.Application.Operations;

public sealed record OptionalChange<T>(bool IsSpecified, T? Value)
;

public static class OptionalChange
{
    public static OptionalChange<T> Unchanged<T>() => new(false, default);
    public static OptionalChange<T> To<T>(T? value) => new(true, value);
}

public sealed record ChannelCreationItem(
    string Name,
    ChannelKind Kind,
    ulong? ParentCategoryId,
    string? Topic,
    bool? IsNsfw,
    int? SlowModeSeconds,
    int? Bitrate,
    int? UserLimit,
    int? Position,
    bool SynchronizeWithParent);

public sealed record CreateChannelsRequest(
    Guid BotProfileId,
    ulong ServerId,
    ImmutableArray<ChannelCreationItem> Channels,
    string? AuditReason);

public sealed record EditChannelRequest(
    Guid BotProfileId,
    ulong ServerId,
    ulong ChannelId,
    OptionalChange<string> Name,
    OptionalChange<ulong?> ParentCategoryId,
    OptionalChange<int> Position,
    OptionalChange<string?> Topic,
    OptionalChange<bool> IsNsfw,
    OptionalChange<int> SlowModeSeconds,
    OptionalChange<int> DefaultAutoArchiveMinutes,
    OptionalChange<int> Bitrate,
    OptionalChange<int> UserLimit,
    OptionalChange<string?> RegionOverride,
    string? AuditReason);

public enum BulkRenameMode
{
    ExactReplacement,
    Prefix,
    Suffix,
    FindAndReplace,
    SequentialNumbering
}

public sealed record BulkRenameRequest(
    Guid BotProfileId,
    ulong ServerId,
    ImmutableArray<ulong> ChannelIds,
    BulkRenameMode Mode,
    string Value,
    string? Replacement,
    int StartNumber,
    int ZeroPadding,
    string? AuditReason);

public enum MovePlacement
{
    PreserveRelativeOrder,
    BeforeChannel,
    AfterChannel
}

public sealed record MoveChannelsRequest(
    Guid BotProfileId,
    ulong ServerId,
    ImmutableArray<ulong> ChannelIds,
    ulong? DestinationCategoryId,
    MovePlacement Placement,
    ulong? AnchorChannelId,
    string? AuditReason);

public sealed record CloneChannelRequest(
    Guid BotProfileId,
    ulong ServerId,
    ulong ChannelId,
    string NewName,
    ulong? ParentCategoryId,
    bool CopyPermissionOverwrites,
    string? AuditReason);

public sealed record CloneCategoryRequest(
    Guid BotProfileId,
    ulong ServerId,
    ulong CategoryId,
    string NewCategoryName,
    ImmutableArray<ulong> ChildChannelIds,
    bool CopyCategoryOverwrites,
    bool CopyChildOverwrites,
    bool SynchronizeChildren,
    string? AuditReason);

public sealed record ChannelLockRequest(
    Guid BotProfileId,
    ulong ServerId,
    ImmutableArray<ulong> ChannelIds,
    ulong TargetRoleId,
    bool IsUnlock,
    bool IncludeSecondaryPermission,
    string? AuditReason);

public sealed record SynchronizePermissionsRequest(
    Guid BotProfileId,
    ulong ServerId,
    ImmutableArray<ulong> ChannelIds,
    string? AuditReason);

public sealed record DeleteChannelsRequest(
    Guid BotProfileId,
    ulong ServerId,
    ImmutableArray<ulong> ChannelIds,
    bool DeleteCategoryOnly,
    bool IncludeAllChildren,
    ImmutableArray<ulong> ChildChannelIds,
    string? AuditReason);

public sealed record ChannelPlanResult(
    OperationPlan? Plan,
    ImmutableArray<string> Errors)
{
    public bool IsSuccess => Plan is not null && Errors.Length == 0;

    public static ChannelPlanResult Success(OperationPlan plan) =>
        new(plan, ImmutableArray<string>.Empty);

    public static ChannelPlanResult Failure(params IEnumerable<string> errors) =>
        new(null, errors.ToImmutableArray());
}

public sealed record ChannelWriteOutcome(
    bool Succeeded,
    ulong? ResourceId,
    OperationFailure? Failure,
    OperationOutcomeCertainty OutcomeCertainty);

public sealed record ChannelPositionUpdate(
    ulong ChannelId,
    int Position);

public interface IDiscordChannelWriter
{
    Task<ChannelWriteOutcome> CreateCategoryAsync(
        Guid botProfileId,
        ulong serverId,
        ChannelOperationStateSnapshot after,
        string? auditReason,
        CancellationToken cancellationToken);

    Task<ChannelWriteOutcome> CreateTextChannelAsync(
        Guid botProfileId,
        ulong serverId,
        ChannelOperationStateSnapshot after,
        string? auditReason,
        CancellationToken cancellationToken);

    Task<ChannelWriteOutcome> CreateVoiceChannelAsync(
        Guid botProfileId,
        ulong serverId,
        ChannelOperationStateSnapshot after,
        string? auditReason,
        CancellationToken cancellationToken);

    Task<ChannelWriteOutcome> ModifyChannelAsync(
        Guid botProfileId,
        ulong serverId,
        ulong channelId,
        ChannelOperationStateSnapshot before,
        ChannelOperationStateSnapshot after,
        string? auditReason,
        CancellationToken cancellationToken);

    Task<ChannelWriteOutcome> ReorderChannelsAsync(
        Guid botProfileId,
        ulong serverId,
        IReadOnlyList<ChannelPositionUpdate> positions,
        string? auditReason,
        CancellationToken cancellationToken);

    Task<ChannelWriteOutcome> SetPermissionOverwriteAsync(
        Guid botProfileId,
        ulong serverId,
        ulong channelId,
        ChannelPermissionOverwriteSnapshot overwrite,
        string? auditReason,
        CancellationToken cancellationToken);

    Task<ChannelWriteOutcome> DeletePermissionOverwriteAsync(
        Guid botProfileId,
        ulong serverId,
        ulong channelId,
        ulong targetId,
        PermissionTargetKind targetType,
        string? auditReason,
        CancellationToken cancellationToken);

    Task<ChannelWriteOutcome> DeleteChannelAsync(
        Guid botProfileId,
        ulong serverId,
        ulong channelId,
        string? auditReason,
        CancellationToken cancellationToken);
}

public interface IChannelOperationPlanner
{
    ChannelPlanResult PlanCreate(CreateChannelsRequest request);
    ChannelPlanResult PlanEdit(EditChannelRequest request);
    ChannelPlanResult PlanBulkRename(BulkRenameRequest request);
    ChannelPlanResult PlanMove(MoveChannelsRequest request);
    ChannelPlanResult PlanClone(CloneChannelRequest request);
    ChannelPlanResult PlanCloneCategory(CloneCategoryRequest request);
    ChannelPlanResult PlanLock(ChannelLockRequest request);
    ChannelPlanResult PlanSynchronizePermissions(SynchronizePermissionsRequest request);
    ChannelPlanResult PlanDelete(DeleteChannelsRequest request);
    OperationPreview BuildPreview(OperationPlan plan, string botDisplayName);
}

public interface IChannelOperationPreflightService
{
    ChannelOperationPreflightResult Validate(OperationPlan plan);
}

public interface IOperationReconciliationService
{
    Task<OperationReconciliationResult> ReconcileAsync(
        OperationPlan plan,
        OperationStep operationStep,
        ChannelWriteOutcome uncertainOutcome,
        CancellationToken cancellationToken);
}

public interface IChannelOperationExecutor
{
    Task<ChannelOperationResult> ExecuteAsync(
        OperationPlan plan,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);
}

public interface IChannelOperationScheduler : IAsyncDisposable
{
    event EventHandler<QueuedOperationSnapshot>? OperationChanged;

    IReadOnlyList<QueuedOperationSnapshot> Snapshots { get; }

    Task InitializeAsync(CancellationToken cancellationToken);

    Task<QueueSubmissionResult> EnqueueAsync(
        OperationPlan plan,
        CancellationToken cancellationToken);

    bool Cancel(Guid operationId);
}

public sealed record QueueSubmissionResult(
    bool Accepted,
    int? QueuePosition,
    string? Error);

public sealed record QueuedOperationSnapshot(
    OperationPlan Plan,
    ChannelOperationState State,
    int QueuePosition,
    OperationProgress? Progress,
    ChannelOperationResult? Result,
    DateTimeOffset EnqueuedAt);
