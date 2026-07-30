using System.Collections.Immutable;
using DiscordControlCenter.Application.Explorer;
using DiscordControlCenter.Core.Operations;

namespace DiscordControlCenter.Application.Operations;

public sealed class ChannelOperationReconciliationService(
    IBotExplorerService explorer) : IOperationReconciliationService
{
    public async Task<OperationReconciliationResult> ReconcileAsync(
        OperationPlan plan,
        OperationStep operationStep,
        ChannelWriteOutcome uncertainOutcome,
        CancellationToken cancellationToken)
    {
        _ = uncertainOutcome;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attempt > 0)
            {
                await Task.Delay(
                        TimeSpan.FromMilliseconds(250 * (1 << (attempt - 1))),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await explorer.RefreshAsync(plan.BotProfileId, cancellationToken).ConfigureAwait(false);
            var snapshot = explorer.GetSnapshot(plan.BotProfileId);
            var server = snapshot.Servers.FirstOrDefault(item => item.Id == plan.ServerId);
            if (server is null)
            {
                continue;
            }

            var result = ReconcileCurrent(plan, operationStep, server);
            if (result.Status is not OperationReconciliationStatus.TimedOut)
            {
                return result;
            }
        }

        return new OperationReconciliationResult(
            OperationReconciliationStatus.TimedOut,
            "The cache did not become current enough to determine the request outcome.",
            ImmutableArray<ulong>.Empty,
            DateTimeOffset.UtcNow);
    }

    private static OperationReconciliationResult ReconcileCurrent(
        OperationPlan plan,
        OperationStep step,
        Core.Explorer.ServerReadModel server)
    {
        if (step.Kind == OperationStepKind.ReorderChannel)
        {
            var afterMatches = BatchMatches(step.BatchAfterStates, server);
            if (afterMatches)
            {
                return Result(
                    OperationReconciliationStatus.ConfirmedApplied,
                    "Every reordered channel matches the planned final position.",
                    step.BatchAfterStates.Select(state => state.Id!.Value).ToImmutableArray());
            }

            if (BatchMatches(step.BatchBeforeStates, server))
            {
                return Result(
                    OperationReconciliationStatus.ConfirmedNotApplied,
                    "Every reordered channel still matches its captured position.",
                    step.BatchBeforeStates.Select(state => state.Id!.Value).ToImmutableArray());
            }

            return Result(
                OperationReconciliationStatus.Ambiguous,
                "The current order matches neither the captured before state nor the planned order.",
                ImmutableArray<ulong>.Empty);
        }

        if (step.Kind is OperationStepKind.CreateCategory
            or OperationStepKind.CreateTextChannel
            or OperationStepKind.CreateVoiceChannel)
        {
            var expected = step.After!;
            var matches = server.Channels
                .Where(channel =>
                    channel.Kind == expected.Kind
                    && channel.CategoryId == expected.ParentCategoryId
                    && channel.CreatedAt >= plan.CreatedAt.AddMinutes(-2)
                    && string.Equals(channel.Name, expected.Name, StringComparison.OrdinalIgnoreCase))
                .Select(channel => channel.Id)
                .ToImmutableArray();
            return matches.Length switch
            {
                0 => Result(
                    OperationReconciliationStatus.ConfirmedNotApplied,
                    "No matching created resource was found.",
                    matches),
                1 => Result(
                    OperationReconciliationStatus.ConfirmedApplied,
                    "Exactly one matching created resource was found.",
                    matches),
                _ => Result(
                    OperationReconciliationStatus.Ambiguous,
                    "Multiple resources match the expected create result. Manual review is required.",
                    matches)
            };
        }

        var target = server.Channels.FirstOrDefault(channel => channel.Id == step.Target.Id);
        if (step.Kind == OperationStepKind.DeleteChannel)
        {
            return target is null
                ? Result(
                    OperationReconciliationStatus.ConfirmedApplied,
                    "The target no longer exists.",
                    ImmutableArray<ulong>.Empty)
                : Result(
                    OperationReconciliationStatus.ConfirmedNotApplied,
                    "The target still exists.",
                    [target.Id]);
        }

        if (target is null)
        {
            return Result(
                OperationReconciliationStatus.Ambiguous,
                "The target disappeared while reconciling a non-delete operation.",
                ImmutableArray<ulong>.Empty);
        }

        var current = ChannelOperationPlanner.ToState(target, server);
        if (step.After is not null
            && ChannelOperationPlanner.Fingerprint(current)
                == ChannelOperationPlanner.Fingerprint(step.After))
        {
            return Result(
                OperationReconciliationStatus.ConfirmedApplied,
                "The current target state matches the planned after state.",
                [target.Id]);
        }

        if (step.Before is not null
            && ChannelOperationPlanner.Fingerprint(current)
                == ChannelOperationPlanner.Fingerprint(step.Before))
        {
            return Result(
                OperationReconciliationStatus.ConfirmedNotApplied,
                "The current target state still matches the captured before state.",
                [target.Id]);
        }

        return Result(
            OperationReconciliationStatus.Ambiguous,
            "The target matches neither the captured before state nor the planned after state.",
            [target.Id]);
    }

    private static bool BatchMatches(
        ImmutableArray<ChannelOperationStateSnapshot> expected,
        Core.Explorer.ServerReadModel server) =>
        expected.Length > 0
        && expected.All(state =>
            state.Id is ulong id
            && server.Channels.FirstOrDefault(channel => channel.Id == id) is { } channel
            && ChannelOperationPlanner.Fingerprint(
                ChannelOperationPlanner.ToState(channel, server))
                == ChannelOperationPlanner.Fingerprint(state));

    private static OperationReconciliationResult Result(
        OperationReconciliationStatus status,
        string summary,
        ImmutableArray<ulong> matches) =>
        new(status, summary, matches, DateTimeOffset.UtcNow);
}
