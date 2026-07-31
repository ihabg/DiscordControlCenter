using System.Collections.Immutable;
using DiscordControlCenter.Core.Messaging;

namespace DiscordControlCenter.Application.Messaging;

public interface IScheduledApprovalService
{
    Task<MessageDeliveryResult?> ApproveAsync(Guid occurrenceId, CancellationToken cancellationToken);
    Task<bool> SkipAsync(Guid occurrenceId, CancellationToken cancellationToken);
    Task<bool> ArchiveAsync(Guid occurrenceId, CancellationToken cancellationToken);
}

public sealed class ScheduledApprovalService(
    IScheduledMessageRepository repository,
    IMessagePlanBuilder planner,
    IMessageDeliveryExecutor delivery) : IScheduledApprovalService
{
    public async Task<MessageDeliveryResult?> ApproveAsync(Guid occurrenceId, CancellationToken cancellationToken)
    {
        var approval = await repository.GetApprovalAsync(occurrenceId, cancellationToken).ConfigureAwait(false);
        if (approval is null || approval.Occurrence.State != MessageOperationState.PendingApproval
            || !await repository.TryClaimApprovalAsync(occurrenceId, Guid.NewGuid(), cancellationToken).ConfigureAwait(false)) return null;

        if (approval.ImmutableContent is null)
        {
            await repository.TryDecideApprovalAsync(occurrenceId, MessageOperationState.Failed, "Approved", "IMMUTABLE_TEMPLATE_SNAPSHOT_REQUIRED", CancellationToken.None).ConfigureAwait(false);
            return null;
        }

        var draft = new MessageDraft(Guid.NewGuid(), approval.Snapshot.BotProfileId, approval.Snapshot.Destination, approval.ImmutableContent, ImmutableArray<MessageAttachmentReference>.Empty, "Approved missed schedule", DateTimeOffset.UtcNow);
        var planned = planner.Build(draft, MessageOperationKind.ScheduledChannelMessage);
        if (planned.Plan is null)
        {
            await repository.TryDecideApprovalAsync(occurrenceId, MessageOperationState.Failed, "Approved", "APPROVAL_VALIDATION_FAILED", CancellationToken.None).ConfigureAwait(false);
            return null;
        }

        var result = await delivery.DeliverAsync(planned.Plan with { ScheduledMessageId = approval.Snapshot.Id, OccurrenceId = occurrenceId }, cancellationToken).ConfigureAwait(false);
        var state = result.State == MessageOperationState.Uncertain ? MessageOperationState.Uncertain : result.State;
        await repository.TryDecideApprovalAsync(occurrenceId, state, "Approved", result.Failure?.SafeCode, CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    public Task<bool> SkipAsync(Guid occurrenceId, CancellationToken cancellationToken) => repository.TryDecideApprovalAsync(occurrenceId, MessageOperationState.Skipped, "Skipped", null, cancellationToken);
    public Task<bool> ArchiveAsync(Guid occurrenceId, CancellationToken cancellationToken) => repository.TryDecideApprovalAsync(occurrenceId, MessageOperationState.Archived, "Archived", null, cancellationToken);
}
