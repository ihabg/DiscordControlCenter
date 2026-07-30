using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DiscordControlCenter.Application.Explorer;
using DiscordControlCenter.Core.Operations;
using Microsoft.Extensions.Logging;

namespace DiscordControlCenter.Application.Operations;

public sealed class ChannelOperationExecutor(
    IChannelOperationPreflightService preflight,
    IDiscordChannelWriter writer,
    IOperationReconciliationService reconciliation,
    IBotExplorerService explorer,
    IOperationHistoryRepository historyRepository,
    IOperationBackupRepository backupRepository,
    ILogger<ChannelOperationExecutor> logger) : IChannelOperationExecutor
{
    private const int MaximumAttempts = 3;

    public async Task<ChannelOperationResult> ExecuteAsync(
        OperationPlan plan,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var timer = Stopwatch.StartNew();
        var results = new List<OperationStepResult>();
        var createdResourceIds = new Dictionary<Guid, ulong>();
        string? backupIdentifier = null;
        var reconciliationResult = NotRequired();
        Report(ChannelOperationState.Running, 0, null, "Revalidating the approved preview.");

        var preflightResult = preflight.Validate(plan);
        if (!preflightResult.IsAllowed)
        {
            var failure = new OperationFailure(
                preflightResult.IsStale
                    ? OperationFailureKind.StalePlan
                    : OperationFailureKind.PermissionDenied,
                preflightResult.IsStale ? "PLAN_STALE" : "PREFLIGHT_REJECTED",
                string.Join(" ", preflightResult.Issues.Select(issue => issue.Message)),
                null,
                false,
                OperationOutcomeCertainty.KnownFailed);
            var rejected = BuildResult(
                preflightResult.IsStale ? ChannelOperationState.Stale : ChannelOperationState.Failed,
                failure,
                "No compensation was required because no Discord request was sent.");
            await PersistFinalAsync(plan, rejected, timer.ElapsedMilliseconds).ConfigureAwait(false);
            return rejected;
        }

        if (plan.IsDestructive)
        {
            Report(ChannelOperationState.Waiting, 0, null, "Saving the required local structure backup.");
            try
            {
                var backup = BuildBackup(plan);
                await backupRepository.SaveAsync(backup, cancellationToken).ConfigureAwait(false);
                backupIdentifier = backup.BackupIdentifier;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return await FinishCancelledBeforeExecutionAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                BackupFailedLog(logger, plan.OperationId, exception.GetType().Name, null);
                var failure = new OperationFailure(
                    OperationFailureKind.BackupFailed,
                    "BACKUP_FAILED",
                    "The required local structure backup could not be saved. No Discord request was sent.",
                    exception.GetType().Name,
                    false,
                    OperationOutcomeCertainty.KnownFailed);
                var failed = BuildResult(
                    ChannelOperationState.Failed,
                    failure,
                    "No compensation was required because execution was blocked.");
                await PersistFinalAsync(plan, failed, timer.ElapsedMilliseconds).ConfigureAwait(false);
                return failed;
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return await FinishCancelledBeforeExecutionAsync().ConfigureAwait(false);
        }

        await historyRepository
            .UpdateAsync(
                BuildHistory(
                    plan,
                    ChannelOperationState.Running,
                    startedAt,
                    null,
                    null,
                    backupIdentifier,
                    timer.ElapsedMilliseconds),
                CancellationToken.None)
            .ConfigureAwait(false);

        OperationFailure? terminalFailure = null;
        var uncertainRequiresReview = false;
        foreach (var originalStep in plan.Steps.OrderBy(step => step.Order))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                AddNotStartedResults(originalStep.Order);
                break;
            }

            var step = BindCreatedReferences(originalStep, createdResourceIds);
            Report(
                ChannelOperationState.Running,
                results.Count(result => result.Succeeded),
                step.Order,
                step.Description);
            var stepStartedAt = DateTimeOffset.UtcNow;
            AttemptOutcome outcome;
            try
            {
                outcome = await ExecuteWithRetryAsync(plan, step, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                AddNotStartedResults(step.Order);
                break;
            }

            if (outcome.Outcome.OutcomeCertainty == OperationOutcomeCertainty.Uncertain)
            {
                Report(
                    ChannelOperationState.Waiting,
                    results.Count(result => result.Succeeded),
                    step.Order,
                    "The request outcome is uncertain; reconciling before any retry.");
                try
                {
                    reconciliationResult = await reconciliation
                        .ReconcileAsync(plan, step, outcome.Outcome, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    AddNotStartedResults(step.Order);
                    break;
                }
                if (reconciliationResult.Status == OperationReconciliationStatus.ConfirmedApplied)
                {
                    outcome = outcome with
                    {
                        Outcome = new ChannelWriteOutcome(
                            true,
                            reconciliationResult.MatchingResourceIds.SingleOrDefault() is var reconciledId
                                && reconciledId != 0
                                    ? reconciledId
                                    : outcome.Outcome.ResourceId,
                            null,
                            OperationOutcomeCertainty.KnownSucceeded)
                    };
                }
                else if (reconciliationResult.Status == OperationReconciliationStatus.ConfirmedNotApplied
                    && outcome.Outcome.Failure?.IsRetryable == true)
                {
                    outcome = await ExecuteWithRetryAsync(plan, step, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    uncertainRequiresReview = true;
                }
            }

            var succeeded = outcome.Outcome.Succeeded && !uncertainRequiresReview;
            if (succeeded && outcome.Outcome.ResourceId is ulong createdId && createdId != 0)
            {
                createdResourceIds[step.StepId] = createdId;
            }

            var stepResult = new OperationStepResult(
                step.StepId,
                step.Order,
                step.Description,
                succeeded,
                false,
                outcome.Outcome.ResourceId,
                stepStartedAt,
                DateTimeOffset.UtcNow,
                outcome.AttemptCount,
                succeeded ? null : outcome.Outcome.Failure,
                false,
                false);
            results.Add(stepResult);
            if (!succeeded)
            {
                terminalFailure = outcome.Outcome.Failure
                    ?? new OperationFailure(
                        OperationFailureKind.ReconciliationAmbiguous,
                        "RECONCILIATION_REQUIRED",
                        reconciliationResult.SafeSummary,
                        null,
                        false,
                        OperationOutcomeCertainty.Uncertain);
            }

            if (!await PersistCheckpointAsync(terminalFailure).ConfigureAwait(false))
            {
                terminalFailure ??= new OperationFailure(
                    OperationFailureKind.UncertainOutcome,
                    "HISTORY_CHECKPOINT_FAILED",
                    "The completed step could not be journaled locally. Execution stopped for manual review.",
                    null,
                    false,
                    OperationOutcomeCertainty.Uncertain);
                uncertainRequiresReview = true;
            }

            if (!succeeded || uncertainRequiresReview)
            {
                break;
            }
        }

        if (terminalFailure is not null
            && plan.OperationType == ChannelOperationType.RecreateStructure
            && plan.RecreateCompensationPolicy == RecreateCompensationPolicy.StopForManualReview)
        {
            uncertainRequiresReview = true;
        }

        var compensationSummary = "No compensation was required.";
        if (terminalFailure is not null
            && !cancellationToken.IsCancellationRequested
            && !uncertainRequiresReview
            && plan.CompensationCapability != OperationCompensationCapability.None)
        {
            compensationSummary = await CompensateAsync(
                    plan,
                    results,
                    createdResourceIds,
                    writer,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (results.Any(result => result.Succeeded))
        {
            try
            {
                await explorer.RefreshAsync(plan.BotProfileId, CancellationToken.None).ConfigureAwait(false);
                if (reconciliationResult.Status == OperationReconciliationStatus.NotRequired)
                {
                    reconciliationResult = new OperationReconciliationResult(
                        OperationReconciliationStatus.ConfirmedApplied,
                        "The explorer cache was refreshed after the completed requests.",
                        results.Where(result => result.ResultResourceId is not null)
                            .Select(result => result.ResultResourceId!.Value)
                            .ToImmutableArray(),
                        DateTimeOffset.UtcNow);
                }
            }
            catch (Exception exception)
            {
                ReconciliationRefreshFailedLog(
                    logger,
                    plan.OperationId,
                    exception.GetType().Name,
                    null);
                reconciliationResult = new OperationReconciliationResult(
                    OperationReconciliationStatus.ManualReviewRequired,
                    "Discord requests completed, but the explorer cache could not be refreshed. Manual review is required.",
                    ImmutableArray<ulong>.Empty,
                    DateTimeOffset.UtcNow);
            }
        }

        var state = DetermineFinalState(terminalFailure, uncertainRequiresReview);
        if (state == ChannelOperationState.Cancelled && results.Count < plan.Steps.Length)
        {
            AddNotStartedResults(results.Count + 1);
        }

        var result = BuildResult(state, terminalFailure, compensationSummary);
        Report(state, result.CompletedCount, null, FinalMessage(result));
        await PersistFinalAsync(plan, result, timer.ElapsedMilliseconds).ConfigureAwait(false);
        return result;

        async Task<ChannelOperationResult> FinishCancelledBeforeExecutionAsync()
        {
            AddNotStartedResults(1);
            var cancelled = BuildResult(
                ChannelOperationState.Cancelled,
                CancelledFailure(),
                "No compensation was required because no Discord request was sent.");
            await PersistFinalAsync(plan, cancelled, timer.ElapsedMilliseconds).ConfigureAwait(false);
            return cancelled;
        }

        async Task<bool> PersistCheckpointAsync(OperationFailure? failure)
        {
            try
            {
                var checkpoint = BuildResult(
                    ChannelOperationState.Running,
                    failure,
                    "Compensation has not run.");
                await historyRepository
                    .UpdateAsync(
                        BuildHistory(
                            plan,
                            ChannelOperationState.Running,
                            startedAt,
                            null,
                            checkpoint,
                            backupIdentifier,
                            timer.ElapsedMilliseconds),
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return true;
            }
            catch (Exception exception)
            {
                HistoryWriteFailedLog(logger, plan.OperationId, exception.GetType().Name, null);
                return false;
            }
        }

        ChannelOperationResult BuildResult(
            ChannelOperationState state,
            OperationFailure? failure,
            string compensationSummary) =>
            new(
                plan.OperationId,
                plan.CorrelationId,
                state,
                startedAt,
                DateTimeOffset.UtcNow,
                results.OrderBy(result => result.Order).ToImmutableArray(),
                results.Count(result => result.Succeeded),
                results.Count(result => !result.Succeeded && !result.WasCancelled),
                results.Count(result => result.WasCancelled),
                failure,
                reconciliationResult,
                backupIdentifier,
                plan.CompensationCapability,
                compensationSummary);

        void AddNotStartedResults(int startingOrder)
        {
            foreach (var remaining in plan.Steps
                         .Where(step => step.Order >= startingOrder)
                         .Where(step => results.All(result => result.StepId != step.StepId)))
            {
                var now = DateTimeOffset.UtcNow;
                results.Add(
                    new OperationStepResult(
                        remaining.StepId,
                        remaining.Order,
                        remaining.Description,
                        false,
                        true,
                        null,
                        now,
                        now,
                        0,
                        CancelledFailure(),
                        false,
                        false));
            }
        }

        ChannelOperationState DetermineFinalState(
            OperationFailure? failure,
            bool reconciliationRequired)
        {
            if (reconciliationRequired)
            {
                return ChannelOperationState.ReconciliationRequired;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return results.Any(result => result.Succeeded)
                    ? ChannelOperationState.PartiallyCompleted
                    : ChannelOperationState.Cancelled;
            }

            if (failure is not null)
            {
                return results.Any(result => result.Succeeded)
                    ? ChannelOperationState.PartiallyCompleted
                    : ChannelOperationState.Failed;
            }

            return ChannelOperationState.Completed;
        }

        void Report(
            ChannelOperationState state,
            int completed,
            int? currentStep,
            string message) =>
            progress?.Report(
                new OperationProgress(
                    plan.OperationId,
                    state,
                    completed,
                    plan.Steps.Length,
                    currentStep,
                    message,
                    DateTimeOffset.UtcNow));
    }

    private async Task<AttemptOutcome> ExecuteWithRetryAsync(
        OperationPlan plan,
        OperationStep step,
        CancellationToken cancellationToken)
    {
        ChannelWriteOutcome outcome = default!;
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            outcome = await ExecuteStepAsync(plan, step, cancellationToken).ConfigureAwait(false);
            if (outcome.Succeeded
                || outcome.OutcomeCertainty == OperationOutcomeCertainty.Uncertain
                || outcome.Failure?.IsRetryable != true
                || attempt == MaximumAttempts)
            {
                return new AttemptOutcome(outcome, attempt);
            }

            var baseDelay = 250 * (1 << (attempt - 1));
            var jitter = Random.Shared.Next(25, 126);
            await Task.Delay(TimeSpan.FromMilliseconds(baseDelay + jitter), cancellationToken)
                .ConfigureAwait(false);
        }

        return new AttemptOutcome(outcome, MaximumAttempts);
    }

    private Task<ChannelWriteOutcome> ExecuteStepAsync(
        OperationPlan plan,
        OperationStep step,
        CancellationToken cancellationToken) =>
        step.Kind switch
        {
            OperationStepKind.CreateCategory => writer.CreateCategoryAsync(
                plan.BotProfileId,
                plan.ServerId,
                step.After!,
                plan.AuditReason,
                cancellationToken),
            OperationStepKind.CreateTextChannel => writer.CreateTextChannelAsync(
                plan.BotProfileId,
                plan.ServerId,
                step.After!,
                plan.AuditReason,
                cancellationToken),
            OperationStepKind.CreateVoiceChannel => writer.CreateVoiceChannelAsync(
                plan.BotProfileId,
                plan.ServerId,
                step.After!,
                plan.AuditReason,
                cancellationToken),
            OperationStepKind.ModifyChannel or OperationStepKind.MoveChannel => writer.ModifyChannelAsync(
                plan.BotProfileId,
                plan.ServerId,
                step.Target.Id,
                step.Before!,
                step.After!,
                plan.AuditReason,
                cancellationToken),
            OperationStepKind.ReorderChannel => writer.ReorderChannelsAsync(
                plan.BotProfileId,
                plan.ServerId,
                step.BatchAfterStates.Select(state =>
                    new ChannelPositionUpdate(state.Id!.Value, state.Position)).ToArray(),
                plan.AuditReason,
                cancellationToken),
            OperationStepKind.SetPermissionOverwrite => writer.SetPermissionOverwriteAsync(
                plan.BotProfileId,
                plan.ServerId,
                step.Target.Id,
                step.PermissionOverwriteChange!.After!,
                plan.AuditReason,
                cancellationToken),
            OperationStepKind.DeletePermissionOverwrite => writer.DeletePermissionOverwriteAsync(
                plan.BotProfileId,
                plan.ServerId,
                step.Target.Id,
                step.PermissionOverwriteChange!.TargetId,
                step.PermissionOverwriteChange.TargetType,
                plan.AuditReason,
                cancellationToken),
            OperationStepKind.DeleteChannel => writer.DeleteChannelAsync(
                plan.BotProfileId,
                plan.ServerId,
                step.Target.Id,
                plan.AuditReason,
                cancellationToken),
            _ => Task.FromResult(
                FailedOutcome(
                    OperationFailureKind.Unsupported,
                    "STEP_UNSUPPORTED",
                    "The operation step is not supported.",
                    retryable: false))
        };

    private static async Task<string> CompensateAsync(
        OperationPlan plan,
        List<OperationStepResult> results,
        Dictionary<Guid, ulong> createdResourceIds,
        IDiscordChannelWriter compensationWriter,
        CancellationToken cancellationToken)
    {
        var attempted = 0;
        var succeeded = 0;
        foreach (var result in results.Where(item => item.Succeeded).OrderByDescending(item => item.Order))
        {
            var step = plan.Steps.First(item => item.StepId == result.StepId);
            var compensation = step.Compensation;
            if (compensation is null || compensation.Capability == OperationCompensationCapability.None)
            {
                continue;
            }

            attempted++;
            var outcome = await ExecuteCompensationAsync(
                    plan,
                    step,
                    compensation,
                    createdResourceIds,
                    compensationWriter,
                    cancellationToken)
                .ConfigureAwait(false);
            if (outcome.Succeeded)
            {
                succeeded++;
            }

            var index = results.FindIndex(item => item.StepId == result.StepId);
            results[index] = result with
            {
                CompensationAttempted = true,
                CompensationSucceeded = outcome.Succeeded
            };
        }

        return attempted switch
        {
            0 => "No completed step had an accurate compensating action.",
            _ when attempted == succeeded =>
                $"All {attempted} attempted compensating action{(attempted == 1 ? string.Empty : "s")} succeeded; cache reconciliation is still authoritative.",
            _ =>
                $"{succeeded} of {attempted} compensating actions succeeded. Manual recovery is required."
        };
    }

    private static Task<ChannelWriteOutcome> ExecuteCompensationAsync(
        OperationPlan plan,
        OperationStep original,
        OperationCompensation compensation,
        Dictionary<Guid, ulong> createdResourceIds,
        IDiscordChannelWriter compensationWriter,
        CancellationToken cancellationToken)
    {
        ulong? targetId = compensation.TargetId;
        if (targetId is null && createdResourceIds.TryGetValue(original.StepId, out var createdId))
        {
            targetId = createdId;
        }

        return compensation.StepKind switch
        {
            OperationStepKind.DeleteChannel when targetId is not null and not 0 =>
                compensationWriter.DeleteChannelAsync(
                    plan.BotProfileId,
                    plan.ServerId,
                    targetId.Value,
                    "Compensating action",
                    cancellationToken),
            OperationStepKind.ModifyChannel when targetId is not null && compensation.RestoreState is not null =>
                compensationWriter.ModifyChannelAsync(
                    plan.BotProfileId,
                    plan.ServerId,
                    targetId.Value,
                    original.After!,
                    compensation.RestoreState,
                    "Compensating action",
                    cancellationToken),
            OperationStepKind.SetPermissionOverwrite
                when targetId is not null && compensation.RestoreOverwrite is not null =>
                compensationWriter.SetPermissionOverwriteAsync(
                    plan.BotProfileId,
                    plan.ServerId,
                    targetId.Value,
                    compensation.RestoreOverwrite,
                    "Compensating action",
                    cancellationToken),
            OperationStepKind.DeletePermissionOverwrite when targetId is not null =>
                compensationWriter.DeletePermissionOverwriteAsync(
                    plan.BotProfileId,
                    plan.ServerId,
                    targetId.Value,
                    original.PermissionOverwriteChange!.TargetId,
                    original.PermissionOverwriteChange.TargetType,
                    "Compensating action",
                    cancellationToken),
            OperationStepKind.ReorderChannel when original.BatchBeforeStates.Length > 0 =>
                compensationWriter.ReorderChannelsAsync(
                    plan.BotProfileId,
                    plan.ServerId,
                    original.BatchBeforeStates.Select(state =>
                        new ChannelPositionUpdate(state.Id!.Value, state.Position)).ToArray(),
                    "Compensating action",
                    cancellationToken),
            _ => Task.FromResult(
                FailedOutcome(
                    OperationFailureKind.CompensationFailed,
                    "COMPENSATION_UNAVAILABLE",
                    "An exact compensating action is unavailable.",
                    retryable: false))
        };
    }

    private static OperationStep BindCreatedReferences(
        OperationStep step,
        Dictionary<Guid, ulong> createdResourceIds)
    {
        var bound = step;
        if (step.ParentResultStepId is Guid parentStepId
            && createdResourceIds.TryGetValue(parentStepId, out var parentId)
            && step.After is not null)
        {
            bound = bound with
            {
                After = step.After with { ParentCategoryId = parentId }
            };
        }

        if (step.TargetResultStepId is Guid targetStepId
            && createdResourceIds.TryGetValue(targetStepId, out var targetId))
        {
            bound = bound with
            {
                Target = bound.Target with { Id = targetId },
                Before = bound.Before is null ? null : bound.Before with { Id = targetId },
                After = bound.After is null ? null : bound.After with { Id = targetId }
            };
        }

        if (!step.BatchResultStepIds.IsDefaultOrEmpty
            && step.BatchResultStepIds.Length == step.BatchAfterStates.Length)
        {
            var states = ImmutableArray.CreateBuilder<ChannelOperationStateSnapshot>(
                step.BatchAfterStates.Length);
            for (var index = 0; index < step.BatchAfterStates.Length; index++)
            {
                if (!createdResourceIds.TryGetValue(step.BatchResultStepIds[index], out var resourceId))
                {
                    return bound;
                }

                states.Add(step.BatchAfterStates[index] with { Id = resourceId });
            }

            bound = bound with { BatchAfterStates = states.ToImmutable() };
        }

        return bound;
    }

    private static ServerStructureBackup BuildBackup(OperationPlan plan) =>
        new(
            $"backup-{plan.OperationId:N}",
            plan.OperationId,
            plan.CorrelationId,
            plan.BotProfileId,
            plan.ServerId,
            plan.ServerNameSnapshot,
            plan.SourceExplorerSequence,
            DateTimeOffset.UtcNow,
            plan.ExactBeforeState)
        {
            BackupReason = $"Pre-operation backup for {plan.Title}",
            SourceOperationType = plan.OperationType
        };

    internal static OperationHistoryEntry BuildHistory(
        OperationPlan plan,
        ChannelOperationState state,
        DateTimeOffset? startedAt,
        DateTimeOffset? finishedAt,
        ChannelOperationResult? result,
        string? backupIdentifier,
        long durationMilliseconds) =>
        new(
            plan.OperationId,
            plan.CorrelationId,
            plan.OperationType,
            plan.BotProfileId,
            plan.ServerId,
            plan.ServerNameSnapshot,
            string.Join(',', plan.ExactTargetIds.Select(id => id.ToString(CultureInfo.InvariantCulture))),
            string.Join(", ", plan.Steps.Select(step => step.Target.DisplayName).Distinct(StringComparer.Ordinal)),
            plan.CreatedAt,
            startedAt,
            finishedAt,
            state,
            result?.CompletedCount ?? 0,
            result?.FailedCount ?? 0,
            result?.CancelledCount ?? 0,
            result?.CompensationSummary ?? "Not started",
            backupIdentifier,
            SafeErrorCodes(result),
            durationMilliseconds,
            plan.AuditReason,
            JsonSerializer.Serialize(plan),
            result is null ? null : JsonSerializer.Serialize(result))
        {
            Title = plan.Title,
            RiskLevel = plan.RiskLevel,
            AffectedResourceCount = plan.Steps.Length,
            ReconciliationStatus =
                result?.Reconciliation.Status ?? OperationReconciliationStatus.NotRequired
        };

    private static string? SafeErrorCodes(ChannelOperationResult? result)
    {
        if (result is null)
        {
            return null;
        }

        var codes = result.StepResults
            .Select(step => step.Failure?.SafeCode)
            .Where(code => code is not null)
            .Select(code => code!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return codes.Length == 0 ? null : string.Join(',', codes);
    }

    private async Task PersistFinalAsync(
        OperationPlan plan,
        ChannelOperationResult result,
        long durationMilliseconds)
    {
        try
        {
            await historyRepository
                .UpdateAsync(
                    BuildHistory(
                        plan,
                        result.State,
                        result.StartedAt,
                        result.FinishedAt,
                        result,
                        result.BackupIdentifier,
                        durationMilliseconds),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            HistoryWriteFailedLog(logger, plan.OperationId, exception.GetType().Name, null);
        }
    }

    private static ChannelWriteOutcome FailedOutcome(
        OperationFailureKind kind,
        string code,
        string message,
        bool retryable) =>
        new(
            false,
            null,
            new OperationFailure(
                kind,
                code,
                message,
                null,
                retryable,
                OperationOutcomeCertainty.KnownFailed),
            OperationOutcomeCertainty.KnownFailed);

    private static OperationFailure CancelledFailure() =>
        new(
            OperationFailureKind.Cancelled,
            "CANCELLED_NOT_STARTED",
            "The step was not started because cancellation was requested.",
            null,
            false,
            OperationOutcomeCertainty.KnownFailed);

    private static OperationReconciliationResult NotRequired() =>
        new(
            OperationReconciliationStatus.NotRequired,
            "No uncertain outcome required reconciliation.",
            ImmutableArray<ulong>.Empty,
            DateTimeOffset.UtcNow);

    private static string FinalMessage(ChannelOperationResult result) =>
        result.State switch
        {
            ChannelOperationState.Completed => "The operation completed and the cache was reconciled.",
            ChannelOperationState.PartiallyCompleted =>
                "The operation stopped after partial completion. Review every step result.",
            ChannelOperationState.Cancelled => "The operation was cancelled before any step completed.",
            ChannelOperationState.ReconciliationRequired =>
                "The request outcome is uncertain. Manual reconciliation is required.",
            _ => result.Failure?.SafeMessage ?? "The operation failed."
        };

    private sealed record AttemptOutcome(
        ChannelWriteOutcome Outcome,
        int AttemptCount);

    private static readonly Action<ILogger, Guid, string, Exception?> BackupFailedLog =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Warning,
            new EventId(2301, nameof(BackupFailedLog)),
            "Operation {OperationId} backup failed with {ExceptionType}");

    private static readonly Action<ILogger, Guid, string, Exception?> HistoryWriteFailedLog =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Warning,
            new EventId(2302, nameof(HistoryWriteFailedLog)),
            "Operation {OperationId} history update failed with {ExceptionType}");

    private static readonly Action<ILogger, Guid, string, Exception?> ReconciliationRefreshFailedLog =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Warning,
            new EventId(2303, nameof(ReconciliationRefreshFailedLog)),
            "Operation {OperationId} reconciliation refresh failed with {ExceptionType}");
}
