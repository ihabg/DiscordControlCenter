using System.Collections.Concurrent;
using System.Collections.Immutable;
using DiscordControlCenter.Application.Operations;
using DiscordControlCenter.Core.Explorer;
using DiscordControlCenter.Core.Operations;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordControlCenter.Application.Tests;

public sealed class ChannelOperationExecutionTests
{
    [Fact]
    public async Task PreflightFailureSendsNoDiscordRequest()
    {
        var writer = new ScriptedChannelWriter();
        var executor = CreateExecutor(
            writer,
            preflight: new StubPreflight(allowed: false, stale: true));

        var result = await executor.ExecuteAsync(
            CreatePlan(1),
            null,
            CancellationToken.None);

        Assert.Equal(ChannelOperationState.Stale, result.State);
        Assert.Equal(0, writer.CallCount);
        Assert.Equal("PLAN_STALE", result.Failure!.SafeCode);
    }

    [Fact]
    public async Task CancellationBeforeFirstStepMarksEveryStepNotStarted()
    {
        var writer = new ScriptedChannelWriter();
        var executor = CreateExecutor(writer);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await executor.ExecuteAsync(CreatePlan(3), null, cancellation.Token);

        Assert.Equal(ChannelOperationState.Cancelled, result.State);
        Assert.Equal(3, result.CancelledCount);
        Assert.All(result.StepResults, step => Assert.Equal(0, step.AttemptCount));
        Assert.Equal(0, writer.CallCount);
    }

    [Fact]
    public async Task SuccessfulStepsCompleteAndRefreshReconciliation()
    {
        var writer = new ScriptedChannelWriter(
            Success(701),
            Success(702));
        var executor = CreateExecutor(writer);

        var result = await executor.ExecuteAsync(
            CreatePlan(2),
            null,
            CancellationToken.None);

        Assert.Equal(ChannelOperationState.Completed, result.State);
        Assert.Equal(2, result.CompletedCount);
        Assert.Equal(OperationReconciliationStatus.ConfirmedApplied, result.Reconciliation.Status);
        Assert.Equal(2, writer.CallCount);
    }

    [Fact]
    public async Task NonRetryableFailureIsAttemptedOnce()
    {
        var writer = new ScriptedChannelWriter(Failed("NO_PERMISSION", retryable: false));
        var executor = CreateExecutor(writer);

        var result = await executor.ExecuteAsync(
            CreatePlan(1),
            null,
            CancellationToken.None);

        Assert.Equal(ChannelOperationState.Failed, result.State);
        Assert.Equal(1, writer.CallCount);
        Assert.Equal(1, result.StepResults[0].AttemptCount);
        Assert.Equal("NO_PERMISSION", result.Failure!.SafeCode);
    }

    [Fact]
    public async Task RetryableKnownFailureRetriesThenSucceeds()
    {
        var writer = new ScriptedChannelWriter(
            Failed("NETWORK", retryable: true),
            Success(701));
        var executor = CreateExecutor(writer);

        var result = await executor.ExecuteAsync(
            CreatePlan(1),
            null,
            CancellationToken.None);

        Assert.Equal(ChannelOperationState.Completed, result.State);
        Assert.Equal(2, writer.CallCount);
        Assert.Equal(2, result.StepResults[0].AttemptCount);
    }

    [Fact]
    public async Task RetryableFailureStopsAtBoundedAttemptCount()
    {
        var writer = new ScriptedChannelWriter(
            Failed("NETWORK", retryable: true),
            Failed("NETWORK", retryable: true),
            Failed("NETWORK", retryable: true),
            Success(701));
        var executor = CreateExecutor(writer);

        var result = await executor.ExecuteAsync(
            CreatePlan(1),
            null,
            CancellationToken.None);

        Assert.Equal(ChannelOperationState.Failed, result.State);
        Assert.Equal(3, writer.CallCount);
        Assert.Equal(3, result.StepResults[0].AttemptCount);
    }

    [Fact]
    public async Task UncertainAppliedCreateIsNotRetriedBlindly()
    {
        var writer = new ScriptedChannelWriter(Uncertain("TIMEOUT", retryable: true));
        var reconciliation = new StubReconciliation(
            OperationReconciliationStatus.ConfirmedApplied,
            [777]);
        var executor = CreateExecutor(writer, reconciliation: reconciliation);

        var result = await executor.ExecuteAsync(
            CreatePlan(1),
            null,
            CancellationToken.None);

        Assert.Equal(ChannelOperationState.Completed, result.State);
        Assert.Equal(1, writer.CallCount);
        Assert.Equal((ulong)777, result.StepResults[0].ResultResourceId);
    }

    [Fact]
    public async Task UncertainNotAppliedMayRetryAfterReconciliation()
    {
        var writer = new ScriptedChannelWriter(
            Uncertain("TIMEOUT", retryable: true),
            Success(778));
        var reconciliation = new StubReconciliation(
            OperationReconciliationStatus.ConfirmedNotApplied,
            []);
        var executor = CreateExecutor(writer, reconciliation: reconciliation);

        var result = await executor.ExecuteAsync(
            CreatePlan(1),
            null,
            CancellationToken.None);

        Assert.Equal(ChannelOperationState.Completed, result.State);
        Assert.Equal(2, writer.CallCount);
    }

    [Fact]
    public async Task AmbiguousUncertainOutcomeRequiresManualReview()
    {
        var writer = new ScriptedChannelWriter(Uncertain("TIMEOUT", retryable: true));
        var reconciliation = new StubReconciliation(
            OperationReconciliationStatus.Ambiguous,
            [701, 702]);
        var executor = CreateExecutor(writer, reconciliation: reconciliation);

        var result = await executor.ExecuteAsync(
            CreatePlan(1),
            null,
            CancellationToken.None);

        Assert.Equal(ChannelOperationState.ReconciliationRequired, result.State);
        Assert.Equal(1, writer.CallCount);
        Assert.Equal(OperationOutcomeCertainty.Uncertain, result.Failure!.OutcomeCertainty);
    }

    [Fact]
    public async Task BackupFailureBlocksDestructiveRequest()
    {
        var writer = new ScriptedChannelWriter(Success());
        var backups = new MemoryBackupRepository { FailSaves = true };
        var executor = CreateExecutor(writer, backups: backups);

        var result = await executor.ExecuteAsync(
            DeletePlan(),
            null,
            CancellationToken.None);

        Assert.Equal(ChannelOperationState.Failed, result.State);
        Assert.Equal("BACKUP_FAILED", result.Failure!.SafeCode);
        Assert.Equal(0, writer.CallCount);
        Assert.Empty(backups.Items);
    }

    [Fact]
    public async Task DeletionBackupIsSavedBeforeWriteAndNeverClaimsRollback()
    {
        var order = new List<string>();
        var writer = new ScriptedChannelWriter(Success())
        {
            BeforeCall = _ => order.Add("write")
        };
        var backups = new MemoryBackupRepository
        {
            OnSave = _ => order.Add("backup")
        };
        var executor = CreateExecutor(writer, backups: backups);

        var result = await executor.ExecuteAsync(
            DeletePlan(),
            null,
            CancellationToken.None);

        Assert.Equal(["backup", "write"], order);
        Assert.NotNull(result.BackupIdentifier);
        Assert.DoesNotContain("rolled back", result.CompensationSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(OperationCompensationCapability.None, result.CompensationCapability);
    }

    [Fact]
    public async Task LaterFailureProducesHonestPartialResultAndCompensation()
    {
        var writer = new ScriptedChannelWriter(
            Success(701),
            Failed("REJECTED", retryable: false),
            Success());
        var executor = CreateExecutor(writer);

        var result = await executor.ExecuteAsync(
            CreatePlan(2),
            null,
            CancellationToken.None);

        Assert.Equal(ChannelOperationState.PartiallyCompleted, result.State);
        Assert.Equal(1, result.CompletedCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Contains("compensating action", result.CompensationSummary, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.StepResults[0].CompensationAttempted);
        Assert.Equal(3, writer.CallCount);
    }

    [Fact]
    public async Task CancellationAfterCompletedStepLeavesHonestPartialResult()
    {
        using var cancellation = new CancellationTokenSource();
        var writer = new ScriptedChannelWriter(Success(701))
        {
            BeforeCall = call =>
            {
                if (call == 1)
                {
                    cancellation.Cancel();
                }
            }
        };
        var executor = CreateExecutor(writer);

        var result = await executor.ExecuteAsync(
            CreatePlan(3),
            null,
            cancellation.Token);

        Assert.Equal(ChannelOperationState.PartiallyCompleted, result.State);
        Assert.Equal(1, result.CompletedCount);
        Assert.Equal(2, result.CancelledCount);
        Assert.Equal(1, writer.CallCount);
        Assert.DoesNotContain("rolled back", result.CompensationSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FinalHistoryContainsSafeCodesAndNoExceptionMessage()
    {
        var history = new MemoryHistoryRepository();
        var writer = new ScriptedChannelWriter(Failed("SAFE_CODE", retryable: false));
        var executor = CreateExecutor(writer, history: history);
        var plan = CreatePlan(1);

        await executor.ExecuteAsync(plan, null, CancellationToken.None);

        var entry = await history.GetAsync(plan.OperationId, CancellationToken.None);
        Assert.NotNull(entry);
        Assert.Equal("SAFE_CODE", entry.SafeErrorCodes);
        Assert.DoesNotContain("authorization", entry.PlanJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", entry.PlanJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckpointFailureStopsBeforeAnotherDiscordMutation()
    {
        var history = new FailingCheckpointHistoryRepository(failOnUpdate: 2);
        var writer = new ScriptedChannelWriter(Success(701), Success(702));
        var executor = CreateExecutor(writer, history: history);
        var plan = CreatePlan(2);

        var result = await executor.ExecuteAsync(plan, null, CancellationToken.None);

        Assert.Equal(ChannelOperationState.ReconciliationRequired, result.State);
        Assert.Equal("HISTORY_CHECKPOINT_FAILED", result.Failure!.SafeCode);
        Assert.Equal(1, writer.CallCount);
        var persisted = await history.GetAsync(plan.OperationId, CancellationToken.None);
        Assert.Equal(ChannelOperationState.ReconciliationRequired, persisted!.State);
    }

    private static ChannelOperationExecutor CreateExecutor(
        IDiscordChannelWriter writer,
        IChannelOperationPreflightService? preflight = null,
        IOperationReconciliationService? reconciliation = null,
        IOperationHistoryRepository? history = null,
        MemoryBackupRepository? backups = null) =>
        new(
            preflight ?? new StubPreflight(),
            writer,
            reconciliation ?? new StubReconciliation(),
            OperationTestFixture.Explorer(),
            history ?? new MemoryHistoryRepository(),
            backups ?? new MemoryBackupRepository(),
            NullLogger<ChannelOperationExecutor>.Instance);

    internal static OperationPlan CreatePlan(int count, ulong? serverId = null)
    {
        var explorer = OperationTestFixture.Explorer();
        var planner = new ChannelOperationPlanner(explorer);
        var request = new CreateChannelsRequest(
            OperationTestFixture.BotId,
            serverId ?? OperationTestFixture.ServerId,
            Enumerable.Range(1, count)
                .Select(index => new ChannelCreationItem(
                    $"queue-{index}",
                    ChannelKind.Text,
                    OperationTestFixture.CategoryId,
                    null,
                    false,
                    0,
                    null,
                    null,
                    null,
                    false))
                .ToImmutableArray(),
            null);
        if (serverId is null || serverId == OperationTestFixture.ServerId)
        {
            return planner.PlanCreate(request).Plan!;
        }

        var basePlan = planner.PlanCreate(request with
        {
            ServerId = OperationTestFixture.ServerId
        }).Plan!;
        return basePlan with
        {
            OperationId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            ServerId = serverId.Value,
            ServerNameSnapshot = $"Server {serverId.Value}"
        };
    }

    private static OperationPlan DeletePlan() =>
        new ChannelOperationPlanner(OperationTestFixture.Explorer())
            .PlanDelete(
                new DeleteChannelsRequest(
                    OperationTestFixture.BotId,
                    OperationTestFixture.ServerId,
                    [OperationTestFixture.TextId],
                    false,
                    false,
                    [],
                    null))
            .Plan!;

    private static ChannelWriteOutcome Success(ulong? id = null) =>
        new(true, id, null, OperationOutcomeCertainty.KnownSucceeded);

    private static ChannelWriteOutcome Failed(string code, bool retryable) =>
        new(
            false,
            null,
            new OperationFailure(
                OperationFailureKind.Transport,
                code,
                "A safe test failure occurred.",
                "TestException",
                retryable,
                OperationOutcomeCertainty.KnownFailed),
            OperationOutcomeCertainty.KnownFailed);

    private static ChannelWriteOutcome Uncertain(string code, bool retryable) =>
        new(
            false,
            null,
            new OperationFailure(
                OperationFailureKind.UncertainOutcome,
                code,
                "The request outcome is uncertain.",
                "TimeoutException",
                retryable,
                OperationOutcomeCertainty.Uncertain),
            OperationOutcomeCertainty.Uncertain);
}

internal sealed class StubPreflight(bool allowed = true, bool stale = false)
    : IChannelOperationPreflightService
{
    public ChannelOperationPreflightResult Validate(OperationPlan plan) =>
        new(
            allowed,
            stale,
            allowed
                ? []
                :
                [
                    new OperationPreflightIssue(
                        stale ? "TARGET_CHANGED" : "MISSING_PERMISSION",
                        stale ? "The target changed." : "Permission is missing.",
                        stale,
                        plan.ExactTargetIds.FirstOrDefault())
                ],
            [],
            DateTimeOffset.UtcNow);
}

internal sealed class StubReconciliation(
    OperationReconciliationStatus status = OperationReconciliationStatus.ConfirmedApplied,
    ImmutableArray<ulong>? matches = null) : IOperationReconciliationService
{
    public Task<OperationReconciliationResult> ReconcileAsync(
        OperationPlan plan,
        OperationStep operationStep,
        ChannelWriteOutcome uncertainOutcome,
        CancellationToken cancellationToken) =>
        Task.FromResult(
            new OperationReconciliationResult(
                status,
                status.ToString(),
                matches ?? [],
                DateTimeOffset.UtcNow));
}

internal sealed class ScriptedChannelWriter(
    params ChannelWriteOutcome[] outcomes) : IDiscordChannelWriter
{
    private readonly Queue<ChannelWriteOutcome> _outcomes = new(outcomes);
    private int _callCount;

    public int CallCount => Volatile.Read(ref _callCount);
    public Action<int>? BeforeCall { get; init; }

    public Task<ChannelWriteOutcome> CreateCategoryAsync(
        Guid botProfileId, ulong serverId, ChannelOperationStateSnapshot after,
        string? auditReason, CancellationToken cancellationToken) => Next(cancellationToken);
    public Task<ChannelWriteOutcome> CreateTextChannelAsync(
        Guid botProfileId, ulong serverId, ChannelOperationStateSnapshot after,
        string? auditReason, CancellationToken cancellationToken) => Next(cancellationToken);
    public Task<ChannelWriteOutcome> CreateVoiceChannelAsync(
        Guid botProfileId, ulong serverId, ChannelOperationStateSnapshot after,
        string? auditReason, CancellationToken cancellationToken) => Next(cancellationToken);
    public Task<ChannelWriteOutcome> ModifyChannelAsync(
        Guid botProfileId, ulong serverId, ulong channelId,
        ChannelOperationStateSnapshot before, ChannelOperationStateSnapshot after,
        string? auditReason, CancellationToken cancellationToken) => Next(cancellationToken);
    public Task<ChannelWriteOutcome> ReorderChannelsAsync(
        Guid botProfileId, ulong serverId, IReadOnlyList<ChannelPositionUpdate> positions,
        string? auditReason, CancellationToken cancellationToken) => Next(cancellationToken);
    public Task<ChannelWriteOutcome> SetPermissionOverwriteAsync(
        Guid botProfileId, ulong serverId, ulong channelId,
        ChannelPermissionOverwriteSnapshot overwrite, string? auditReason,
        CancellationToken cancellationToken) => Next(cancellationToken);
    public Task<ChannelWriteOutcome> DeletePermissionOverwriteAsync(
        Guid botProfileId, ulong serverId, ulong channelId, ulong targetId,
        PermissionTargetKind targetType, string? auditReason,
        CancellationToken cancellationToken) => Next(cancellationToken);
    public Task<ChannelWriteOutcome> DeleteChannelAsync(
        Guid botProfileId, ulong serverId, ulong channelId,
        string? auditReason, CancellationToken cancellationToken) => Next(cancellationToken);

    private Task<ChannelWriteOutcome> Next(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var call = Interlocked.Increment(ref _callCount);
        BeforeCall?.Invoke(call);
        var outcome = _outcomes.Count == 0
            ? new ChannelWriteOutcome(true, null, null, OperationOutcomeCertainty.KnownSucceeded)
            : _outcomes.Dequeue();
        return Task.FromResult(outcome);
    }
}

internal sealed class MemoryHistoryRepository : IOperationHistoryRepository
{
    private readonly ConcurrentDictionary<Guid, OperationHistoryEntry> _items = new();
    public IReadOnlyDictionary<Guid, OperationHistoryEntry> Items => _items;

    public Task AddAsync(OperationHistoryEntry entry, CancellationToken cancellationToken)
    {
        if (!_items.TryAdd(entry.OperationId, entry))
        {
            throw new InvalidOperationException("duplicate");
        }

        return Task.CompletedTask;
    }

    public Task UpdateAsync(OperationHistoryEntry entry, CancellationToken cancellationToken)
    {
        _items[entry.OperationId] = entry;
        return Task.CompletedTask;
    }

    public Task<OperationHistoryEntry?> GetAsync(Guid operationId, CancellationToken cancellationToken)
    {
        _items.TryGetValue(operationId, out var entry);
        return Task.FromResult(entry);
    }

    public Task<IReadOnlyList<OperationHistoryEntry>> GetRecentAsync(
        int count,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OperationHistoryEntry>>(
            _items.Values.OrderByDescending(entry => entry.CreatedAt).Take(count).ToArray());

    public Task<IReadOnlyList<OperationHistoryEntry>> GetInterruptedAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OperationHistoryEntry>>(
            _items.Values.Where(entry =>
                entry.State is ChannelOperationState.Pending
                    or ChannelOperationState.Running
                    or ChannelOperationState.Cancelling).ToArray());
}

internal sealed class MemoryBackupRepository : IOperationBackupRepository
{
    private readonly ConcurrentDictionary<string, ServerStructureBackup> _items = new();
    public bool FailSaves { get; init; }
    public Action<ServerStructureBackup>? OnSave { get; init; }
    public IReadOnlyDictionary<string, ServerStructureBackup> Items => _items;

    public Task SaveAsync(ServerStructureBackup backup, CancellationToken cancellationToken)
    {
        if (FailSaves)
        {
            throw new IOException("simulated");
        }

        OnSave?.Invoke(backup);
        _items[backup.BackupIdentifier] = backup;
        return Task.CompletedTask;
    }

    public Task<ServerStructureBackup?> GetAsync(
        string backupIdentifier,
        CancellationToken cancellationToken)
    {
        _items.TryGetValue(backupIdentifier, out var backup);
        return Task.FromResult(backup);
    }
}

internal sealed class FailingCheckpointHistoryRepository(int failOnUpdate) :
    IOperationHistoryRepository
{
    private readonly ConcurrentDictionary<Guid, OperationHistoryEntry> _items = new();
    private int _updateCount;

    public Task AddAsync(OperationHistoryEntry entry, CancellationToken cancellationToken)
    {
        _items[entry.OperationId] = entry;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(OperationHistoryEntry entry, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _updateCount) == failOnUpdate)
        {
            throw new IOException("simulated checkpoint failure");
        }

        _items[entry.OperationId] = entry;
        return Task.CompletedTask;
    }

    public Task<OperationHistoryEntry?> GetAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        _items.TryGetValue(operationId, out var entry);
        return Task.FromResult(entry);
    }

    public Task<IReadOnlyList<OperationHistoryEntry>> GetRecentAsync(
        int count,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OperationHistoryEntry>>(
            _items.Values.Take(count).ToArray());

    public Task<IReadOnlyList<OperationHistoryEntry>> GetInterruptedAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OperationHistoryEntry>>(
            _items.Values.Where(entry =>
                entry.State is ChannelOperationState.Pending
                    or ChannelOperationState.Running
                    or ChannelOperationState.Waiting
                    or ChannelOperationState.Cancelling).ToArray());
}
