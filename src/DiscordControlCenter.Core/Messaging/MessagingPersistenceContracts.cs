namespace DiscordControlCenter.Core.Messaging;

public interface IMessageTemplateRepository
{
    Task<IReadOnlyList<MessageTemplate>> SearchAsync(string? search, CancellationToken cancellationToken);
    Task<MessageTemplate?> GetAsync(Guid templateId, CancellationToken cancellationToken);
    Task SaveAsync(MessageTemplate messageTemplate, CancellationToken cancellationToken);
    Task DeleteAsync(Guid templateId, CancellationToken cancellationToken);
}

public interface IAutomationRuleRepository
{
    Task<IReadOnlyList<AutomationRule>> ListAsync(Guid? botProfileId, ulong? serverId, CancellationToken cancellationToken);
    Task<AutomationRule?> GetAsync(Guid ruleId, CancellationToken cancellationToken);
    Task SaveVersionAsync(AutomationRule rule, CancellationToken cancellationToken);
}

public interface IAutomationExecutionRepository
{
    Task<bool> HasCompletedAsync(Guid ruleId, int version, ulong memberId, CancellationToken cancellationToken);
    Task SaveAsync(JoinWorkflowExecution execution, AutomationExecutionResult result, CancellationToken cancellationToken);
}

public interface IDeliveryHistoryRepository
{
    Task RecordAsync(MessageOperationPlan plan, MessageDeliveryResult result, CancellationToken cancellationToken);
}

public interface IScheduledMessageRepository
{
    Task<IReadOnlyList<ScheduledMessageDefinition>> ListEnabledAsync(CancellationToken cancellationToken);
    Task<ScheduledMessagePage> QuerySchedulesAsync(ScheduledMessageQuery query, CancellationToken cancellationToken);
    Task<ScheduledMessageDefinition?> GetScheduleAsync(Guid scheduleId, CancellationToken cancellationToken);
    Task SaveAsync(ScheduledMessageDefinition definition, CancellationToken cancellationToken);
    Task<bool> TryReserveOccurrenceAsync(ScheduledMessageOccurrence occurrence, CancellationToken cancellationToken);
    Task CompleteOccurrenceAsync(ScheduledMessageOccurrence occurrence, CancellationToken cancellationToken);
    Task<IReadOnlyList<ScheduledMessageApproval>> ListPendingApprovalsAsync(Guid? botProfileId, ulong? serverId, CancellationToken cancellationToken);
    Task<ScheduledApprovalPage> QueryApprovalsAsync(ScheduledApprovalQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyList<ScheduledApprovalScheduleOption>> ListApprovalSchedulesAsync(Guid? botProfileId, ulong? serverId, CancellationToken cancellationToken);
    Task<ScheduledMessageApproval?> GetApprovalAsync(Guid occurrenceId, CancellationToken cancellationToken);
    Task<bool> TryClaimApprovalAsync(Guid occurrenceId, Guid correlationId, CancellationToken cancellationToken);
    Task<bool> TryDecideApprovalAsync(Guid occurrenceId, MessageOperationState terminalState, string decision, string? safeFailureCode, CancellationToken cancellationToken);
}
