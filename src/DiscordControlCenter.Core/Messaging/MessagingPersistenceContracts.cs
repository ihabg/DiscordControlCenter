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
