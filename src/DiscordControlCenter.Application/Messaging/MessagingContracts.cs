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
