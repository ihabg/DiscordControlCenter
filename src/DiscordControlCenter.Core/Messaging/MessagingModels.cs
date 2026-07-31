using System.Collections.Immutable;

namespace DiscordControlCenter.Core.Messaging;

public enum MessageDestinationKind
{
    ServerChannel,
    IndividualDirectMessage
}

public enum MessageOperationKind
{
    ManualChannelMessage,
    IndividualDirectMessage,
    ScheduledChannelMessage,
    WelcomeChannelMessage,
    WelcomeDirectMessage
}

public enum MessageOperationState
{
    Draft,
    Planned,
    Queued,
    Delivering,
    Delivered,
    Failed,
    Cancelled,
    Uncertain,
    Skipped
}

public enum MessageDeliveryFailureKind
{
    Validation,
    BotDisconnected,
    DestinationUnavailable,
    MissingPermission,
    MemberUnavailable,
    DirectMessagesDisabled,
    CannotMessageUser,
    RateLimited,
    Transient,
    UncertainOutcome,
    DuplicateOccurrence,
    RuleDisabled,
    Internal
}

public enum ScheduledMessageRecurrence
{
    Once,
    Daily,
    Weekly
}

public enum MissedOccurrencePolicy
{
    Skip,
    SendLatestOnly,
    RequireManualApproval
}

public enum AutomationRuleState
{
    Draft,
    Enabled,
    Disabled,
    Faulted,
    NeedsAttention,
    Archived
}

public enum AutomationTrigger
{
    MemberJoinedServer
}

public enum AutomationConditionKind
{
    AccountAgeAtLeastDays,
    AccountAgeLessThanDays,
    IsBot,
    MembershipScreeningPending,
    MemberCountAtLeast,
    HasRole,
    ActiveDateTimeWindow
}

public enum AutomationActionKind
{
    Wait,
    AssignRole,
    SendWelcomeChannelMessage,
    SendWelcomeDirectMessage,
    RecordAuditEvent,
    StopWorkflow
}

public enum AutomationFailureBehavior
{
    StopWorkflow,
    ContinueToNextAction,
    MarkForManualReview
}

public enum AutomationFailureReason
{
    None,
    GuildMembersIntentUnavailable,
    MemberDataIncomplete,
    MemberLeft,
    PermissionLost,
    RoleHierarchyBlocked,
    RoleManaged,
    DestinationUnavailable,
    DirectMessagesDisabled,
    RuleDisabled,
    CircuitBreakerOpen,
    RateLimitExceeded,
    Timeout,
    DuplicateExecution,
    Validation,
    Transient,
    Unknown
}

public sealed record MessageDestination(
    MessageDestinationKind Kind,
    ulong ServerId,
    string ServerName,
    ulong? ChannelId,
    string? ChannelName,
    ulong? RecipientUserId,
    string? RecipientDisplayName)
{
    public static MessageDestination Channel(ulong serverId, string serverName, ulong channelId, string channelName) =>
        new(MessageDestinationKind.ServerChannel, serverId, serverName, channelId, channelName, null, null);

    public static MessageDestination DirectMessage(ulong serverId, string serverName, ulong userId, string displayName) =>
        new(MessageDestinationKind.IndividualDirectMessage, serverId, serverName, null, null, userId, displayName);
}

public sealed record EmbedFieldDraft(string Name, string Value, bool Inline);

public sealed record EmbedDraft(
    string? Title,
    string? Description,
    string? Url,
    uint? Color,
    string? AuthorName,
    string? AuthorUrl,
    string? AuthorIconUrl,
    string? ThumbnailUrl,
    string? ImageUrl,
    string? FooterText,
    string? FooterIconUrl,
    DateTimeOffset? Timestamp,
    ImmutableArray<EmbedFieldDraft> Fields)
{
    public static EmbedDraft Empty { get; } = new(
        null, null, null, null, null, null, null, null, null, null, null, null,
        ImmutableArray<EmbedFieldDraft>.Empty);
}

public sealed record AllowedMentionPolicy(
    bool AllowEveryoneAndHere,
    bool AllowRoleMentions,
    ImmutableArray<ulong> AllowedUserIds,
    ImmutableArray<ulong> AllowedRoleIds)
{
    public static AllowedMentionPolicy None { get; } = new(
        false,
        false,
        ImmutableArray<ulong>.Empty,
        ImmutableArray<ulong>.Empty);

    public bool HasBroadMentions => AllowEveryoneAndHere || AllowRoleMentions || AllowedUserIds.Length > 1;
}

public sealed record MessageContent(string Body, EmbedDraft? Embed, AllowedMentionPolicy AllowedMentions)
{
    public int CharacterCount => Body?.Length ?? 0;
}

public sealed record MessageAttachmentReference(string FileName, long Length, string? ContentType);

public sealed record TemplateVariableDefinition(string Name, string Description, bool IsMemberControlled);

public sealed record MessageDraft(
    Guid DraftId,
    Guid BotProfileId,
    MessageDestination Destination,
    MessageContent Content,
    ImmutableArray<MessageAttachmentReference> Attachments,
    string? AuditContext,
    DateTimeOffset CreatedAt)
{
    public Guid? TemplateId { get; init; }
}

public sealed record MessageTemplate(
    Guid Id,
    string Name,
    string? Description,
    MessageContent Content,
    ImmutableArray<TemplateVariableDefinition> Variables,
    ImmutableArray<string> Tags,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastUsedAt)
{
    public int Version { get; init; } = 1;
}

public sealed record TemplateRenderResult(
    bool IsSuccess,
    MessageContent? Content,
    ImmutableArray<string> Errors,
    ImmutableArray<string> UnresolvedVariables,
    ImmutableArray<string> Warnings);

public sealed record MessageOperationPlan(
    Guid OperationId,
    Guid CorrelationId,
    MessageOperationKind Kind,
    Guid BotProfileId,
    MessageDestination Destination,
    MessageContent Content,
    DateTimeOffset CreatedAt,
    long SourceExplorerSequence,
    bool RequiresStrongConfirmation,
    string ConfirmationPrompt,
    string? RequiredConfirmationText,
    string? SafeAuditContext)
{
    public Guid? TemplateId { get; init; }
    public int? TemplateVersion { get; init; }
    public Guid? ScheduledMessageId { get; init; }
    public Guid? OccurrenceId { get; init; }
    public Guid? AutomationRuleId { get; init; }
    public int? AutomationRuleVersion { get; init; }
}

public sealed record MessageDeliveryFailure(
    MessageDeliveryFailureKind Kind,
    string SafeCode,
    string SafeMessage,
    bool IsRetryable,
    bool IsUncertain);

public sealed record MessageDeliveryResult(
    Guid OperationId,
    Guid CorrelationId,
    MessageOperationState State,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    ulong? DiscordMessageId,
    int AttemptCount,
    MessageDeliveryFailure? Failure)
{
    public Guid? OccurrenceId { get; init; }
}

public sealed record ScheduledMessageDefinition(
    Guid Id,
    Guid BotProfileId,
    MessageDestination Destination,
    Guid? TemplateId,
    MessageContent? InlineContent,
    ScheduledMessageRecurrence Recurrence,
    TimeOnly LocalTime,
    string TimeZoneId,
    ImmutableArray<DayOfWeek> Weekdays,
    DateTimeOffset StartAt,
    DateTimeOffset? EndAt,
    bool IsEnabled,
    MissedOccurrencePolicy MissedOccurrencePolicy,
    int MaximumRetryCount,
    DateTimeOffset? LastRunAt,
    DateTimeOffset? NextRunAt);

public sealed record AutomationCondition(
    Guid ConditionId,
    AutomationConditionKind Kind,
    string? Value,
    int? NumericValue,
    ulong? RoleId,
    DateTimeOffset? WindowStart,
    DateTimeOffset? WindowEnd);

public sealed record RoleAssignmentAction(ulong RoleId, string RoleName);

public sealed record WelcomeMessageAction(MessageDestination Destination, Guid? TemplateId, MessageContent? InlineContent);

public sealed record DirectMessageAction(Guid? TemplateId, MessageContent? InlineContent, bool IsEnabled);

public sealed record AutomationAction(
    Guid ActionId,
    int Order,
    AutomationActionKind Kind,
    TimeSpan? WaitDuration,
    RoleAssignmentAction? RoleAssignment,
    WelcomeMessageAction? WelcomeMessage,
    DirectMessageAction? DirectMessage,
    AutomationFailureBehavior FailureBehavior,
    int MaximumRetryCount,
    TimeSpan Timeout);

public sealed record AutomationRateLimitPolicy(
    int MaximumExecutionsPerMinute,
    int MaximumExecutionsPerHour,
    int MaximumWelcomeDirectMessagesPerHour,
    int MaximumConcurrentWorkflows,
    int MaximumActionCount,
    TimeSpan MaximumWaitDuration,
    int MaximumRetryCount)
{
    public static AutomationRateLimitPolicy ConservativeDefault { get; } = new(
        5, 60, 5, 2, 8, TimeSpan.FromMinutes(30), 2);
}

public sealed record AutomationRule(
    Guid Id,
    int Version,
    string Name,
    Guid BotProfileId,
    ulong ServerId,
    string ServerName,
    AutomationTrigger Trigger,
    ImmutableArray<AutomationCondition> Conditions,
    ImmutableArray<AutomationAction> Actions,
    AutomationRuleState State,
    AutomationRateLimitPolicy RateLimitPolicy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? SafeDescription)
{
    public bool DeveloperPortalGuildMembersIntentAcknowledged { get; init; }
}

public sealed record JoinWorkflowDefinition(AutomationRule Rule);

public sealed record JoinWorkflowExecution(
    Guid Id,
    Guid RuleId,
    int RuleVersion,
    Guid BotProfileId,
    ulong ServerId,
    ulong MemberId,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    AutomationRuleState RuleState);

public sealed record AutomationExecutionResult(
    Guid ExecutionId,
    Guid CorrelationId,
    AutomationRuleState RuleState,
    bool Succeeded,
    AutomationFailureReason FailureReason,
    string SafeSummary,
    DateTimeOffset FinishedAt);
