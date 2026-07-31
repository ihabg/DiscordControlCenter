using DiscordControlCenter.Core.Messaging;

namespace DiscordControlCenter.Application.Messaging;

public sealed class MessageDeliveryExecutor(
    IMessagePreflightService preflight,
    IDiscordMessageWriter writer,
    IDeliveryHistoryRepository deliveryHistory) : IMessageDeliveryExecutor
{
    public async Task<MessageDeliveryResult> DeliverAsync(MessageOperationPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var startedAt = DateTimeOffset.UtcNow;
        var check = preflight.Validate(plan);
        if (!check.IsAllowed)
        {
            var firstIssue = check.Issues.Count > 0 ? check.Issues[0] : null;
            return await CompleteAsync(plan, Failure(plan, startedAt, 0, new MessageDeliveryFailure(
                check.IsStale ? MessageDeliveryFailureKind.DestinationUnavailable : MessageDeliveryFailureKind.MissingPermission,
                firstIssue?.SafeCode ?? "PREFLIGHT_FAILED",
                firstIssue?.Message ?? "Message delivery preflight failed.",
                false,
                false))).ConfigureAwait(false);
        }

        const int maximumAttempts = 2;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = plan.Destination.Kind == MessageDestinationKind.ServerChannel
                ? await writer.SendChannelMessageAsync(plan, cancellationToken).ConfigureAwait(false)
                : await writer.SendDirectMessageAsync(plan, cancellationToken).ConfigureAwait(false);
            if (outcome.Succeeded)
            {
                return await CompleteAsync(plan, new MessageDeliveryResult(
                    plan.OperationId,
                    plan.CorrelationId,
                    MessageOperationState.Delivered,
                    startedAt,
                    DateTimeOffset.UtcNow,
                    outcome.MessageId,
                    attempt,
                    null)
                {
                    OccurrenceId = plan.OccurrenceId
                }).ConfigureAwait(false);
            }

            if (outcome.Failure is not { IsRetryable: true, IsUncertain: false } failure || attempt == maximumAttempts)
            {
                return await CompleteAsync(plan, Failure(plan, startedAt, attempt, outcome.Failure ?? new MessageDeliveryFailure(
                    MessageDeliveryFailureKind.Internal,
                    "MESSAGE_DELIVERY_FAILED",
                    "Discord did not confirm message delivery.",
                    false,
                    true))).ConfigureAwait(false);
            }

            await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("Message delivery retry loop terminated unexpectedly.");
    }

    private static MessageDeliveryResult Failure(
        MessageOperationPlan plan,
        DateTimeOffset startedAt,
        int attempts,
        MessageDeliveryFailure failure) =>
        new(
            plan.OperationId,
            plan.CorrelationId,
            failure.IsUncertain ? MessageOperationState.Uncertain : MessageOperationState.Failed,
            startedAt,
            DateTimeOffset.UtcNow,
            null,
            attempts,
            failure)
        {
            OccurrenceId = plan.OccurrenceId
        };

    private async Task<MessageDeliveryResult> CompleteAsync(
        MessageOperationPlan plan,
        MessageDeliveryResult result)
    {
        try
        {
            // The outcome is already known here. Do not let shutdown cancellation erase its audit trail.
            await deliveryHistory.RecordAsync(plan, result, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Delivery state takes precedence: audit persistence must never trigger a second send or disguise its outcome.
        }

        return result;
    }
}
