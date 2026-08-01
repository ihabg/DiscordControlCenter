using DiscordControlCenter.Core.Messaging;

namespace DiscordControlCenter.Application.Messaging;

public sealed record MessagePlanResult(MessageOperationPlan? Plan, IReadOnlyList<string> Errors)
{
    public bool IsSuccess => Plan is not null && Errors.Count == 0;
    public static MessagePlanResult Success(MessageOperationPlan plan) => new(plan, []);
    public static MessagePlanResult Failure(params string[] errors) => new(null, errors);
}

public sealed record MessagePreflightIssue(string SafeCode, string Message);

public sealed record MessagePreflightResult(
    bool IsAllowed,
    bool IsStale,
    IReadOnlyList<MessagePreflightIssue> Issues,
    DateTimeOffset CheckedAt);

public enum ScheduledApprovalPreflightCheckId
{
    SnapshotCompatibility,
    PlainMessageLimits,
    EmbedLimits,
    AllowedMentionPolicy,
    BotProfileExists,
    BotConnected,
    ServerAccessible,
    ChannelExists,
    ChannelSupportsMessageSending,
    ViewChannel,
    SendMessages,
    EmbedLinks,
    AttachFiles,
    MentionEveryone
}

public enum ScheduledApprovalPreflightState
{
    Allowed,
    Blocked,
    Unavailable,
    Unknown,
    NotRequired
}

public sealed record ScheduledApprovalPreflightCheck(
    ScheduledApprovalPreflightCheckId Id,
    string Label,
    ScheduledApprovalPreflightState State,
    bool IsRequired,
    bool BlocksApproval,
    string Explanation,
    string? Remediation,
    string? TechnicalCategory);

public sealed record ScheduledApprovalPreflightResult(
    bool CanSend,
    ScheduledApprovalPreflightState OverallState,
    string Summary,
    DateTimeOffset CheckedAt,
    IReadOnlyList<ScheduledApprovalPreflightCheck> Checks,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> BlockingReasons);

public enum ContentUsageState
{
    NotApplicable,
    WithinLimit,
    NearLimit,
    OverLimit
}

public sealed record ContentUsageRow(
    string Id,
    string Label,
    int Used,
    int Maximum,
    int Remaining,
    ContentUsageState State,
    bool BlocksApproval,
    string Summary,
    string? Warning);

public sealed record ContentUsageResult(
    IReadOnlyList<ContentUsageRow> PlainMessageRows,
    IReadOnlyList<ContentUsageRow> EmbedRows,
    IReadOnlyList<string> ValidationWarnings);

public sealed record MentionPolicyUsageRow(string Id, string Label, bool IsAllowed, string Summary);

public sealed record MessagePreview(
    string BotDisplayName,
    MessageOperationPlan Plan,
    IReadOnlyList<string> Warnings,
    string ApproximationLabel);

public sealed record MessageWriteOutcome(
    bool Succeeded,
    ulong? MessageId,
    MessageDeliveryFailure? Failure);

public interface IMessagePlanBuilder
{
    MessagePlanResult Build(MessageDraft draft, MessageOperationKind kind);
    MessagePreview BuildPreview(MessageOperationPlan plan, string botDisplayName);
}

public interface IMessagePreflightService
{
    MessagePreflightResult Validate(MessageOperationPlan plan);
}

/// <summary>
/// Produces a safe, immutable-occurrence-specific approval read model. This is advisory UI
/// state; <see cref="IMessagePreflightService"/> remains the delivery executor's authority.
/// </summary>
public interface IScheduledApprovalPreflightService
{
    Task<ScheduledApprovalPreflightResult> EvaluateAsync(
        ScheduledMessageApproval approval,
        CancellationToken cancellationToken);

    ContentUsageResult GetUsage(MessageContent? content);
    IReadOnlyList<MentionPolicyUsageRow> GetMentionPolicyUsage(MessageContent? content);
}

public interface IDiscordMessageWriter
{
    Task<MessageWriteOutcome> SendChannelMessageAsync(MessageOperationPlan plan, CancellationToken cancellationToken);
    Task<MessageWriteOutcome> SendDirectMessageAsync(MessageOperationPlan plan, CancellationToken cancellationToken);
}

public interface IMessageDeliveryExecutor
{
    Task<MessageDeliveryResult> DeliverAsync(MessageOperationPlan plan, CancellationToken cancellationToken);
}

public interface ITemplateRenderer
{
    TemplateRenderResult Render(MessageTemplate messageTemplate, IReadOnlyDictionary<string, string?> values);
    IReadOnlyList<TemplateVariableDefinition> BuiltInVariables { get; }
}

public interface IScheduledMessageService
{
    IReadOnlyList<DateTimeOffset> GetDueOccurrences(ScheduledMessageDefinition definition, DateTimeOffset now);
    DateTimeOffset? GetNextOccurrence(ScheduledMessageDefinition definition, DateTimeOffset now);
}

public sealed record ScheduledMessageDetail(
    Guid Id,
    string Name,
    ScheduledMessageLifecycle Lifecycle,
    string LifecycleExplanation,
    string RecurrenceSummary,
    string TimeZoneDisplay,
    string? TimeZoneWarning,
    ScheduledMessageDefinition Definition);

public sealed record ScheduledMessageOccurrencePage(IReadOnlyList<ScheduledMessageOccurrenceListItem> Items, int Limit);

public interface IScheduledMessageQueryService
{
    Task<ScheduledMessagePage> QueryAsync(ScheduledMessageQuery query, CancellationToken cancellationToken);
    Task<ScheduledMessageFilterOptions> GetFilterOptionsAsync(Guid botProfileId, ulong serverId, CancellationToken cancellationToken);
    Task<ScheduledMessageDetail?> GetDetailAsync(Guid botProfileId, ulong serverId, Guid scheduleId, CancellationToken cancellationToken);
    Task<ScheduledMessageOccurrencePage> GetRecentOccurrencesAsync(Guid botProfileId, ulong serverId, Guid scheduleId, int limit, CancellationToken cancellationToken);
}

public sealed record ScheduledDraftValidation(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings, string RecurrenceSummary, IReadOnlyList<DateTimeOffset> Upcoming)
{
    public bool IsValid => Errors.Count == 0;
}

public sealed record ScheduledDraftSaveResult(bool Saved, bool Conflict, ScheduledMessageDefinition? Definition, ScheduledDraftValidation Validation, string? Message);
public sealed record ScheduledDraftTemplateOption(Guid Id, string Name);

public interface IScheduledMessageDraftService
{
    ScheduledMessageDefinition CreateDraft(Guid botProfileId, MessageDestination destination);
    Task<IReadOnlyList<ScheduledDraftTemplateOption>> GetTemplateOptionsAsync(Guid botProfileId, ulong serverId, CancellationToken cancellationToken);
    Task<ScheduledMessageDefinition?> LoadAsync(Guid botProfileId, ulong serverId, Guid scheduleId, CancellationToken cancellationToken);
    Task<ScheduledDraftValidation> ValidateAsync(ScheduledMessageDefinition definition, CancellationToken cancellationToken);
    Task<ScheduledDraftSaveResult> SaveAsync(ScheduledMessageDefinition definition, int expectedRevision, CancellationToken cancellationToken);
}

public enum ScheduledLifecycleOperation { Enable, Disable, ReEnable, Archive, Delete }
public sealed record ScheduledLifecycleRequest(Guid BotProfileId, ulong ServerId, Guid ScheduleId, int ExpectedRevision);
public sealed record ScheduledDeleteEligibility(bool CanDelete, ScheduledMessageLifecycle Lifecycle, ScheduledMessageDependencySummary Dependencies, string Explanation, string? SafeAlternative);
public sealed record ScheduledLifecycleResult(bool Success, bool Conflict, ScheduledMessageLifecycle CurrentLifecycle, ScheduledMessageLifecycle? NewLifecycle, int CurrentRevision, int? NewRevision, ScheduledDraftValidation? Validation, string? FailureCategory, string Explanation, ScheduledDeleteEligibility? DeleteEligibility = null);
public interface IScheduledMessageLifecycleService
{
    Task<ScheduledLifecycleResult> ValidateEnableAsync(ScheduledLifecycleRequest request, CancellationToken cancellationToken);
    Task<ScheduledLifecycleResult> EnableAsync(ScheduledLifecycleRequest request, CancellationToken cancellationToken);
    Task<ScheduledLifecycleResult> DisableAsync(ScheduledLifecycleRequest request, CancellationToken cancellationToken);
    Task<ScheduledLifecycleResult> ValidateReEnableAsync(ScheduledLifecycleRequest request, CancellationToken cancellationToken);
    Task<ScheduledLifecycleResult> ReEnableAsync(ScheduledLifecycleRequest request, CancellationToken cancellationToken);
    Task<ScheduledLifecycleResult> ArchiveAsync(ScheduledLifecycleRequest request, CancellationToken cancellationToken);
    Task<ScheduledDeleteEligibility> GetDeleteEligibilityAsync(ScheduledLifecycleRequest request, CancellationToken cancellationToken);
    Task<ScheduledLifecycleResult> DeleteAsync(ScheduledLifecycleRequest request, CancellationToken cancellationToken);
}
