using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using DiscordControlCenter.Application.Operations;
using DiscordControlCenter.Core.Operations;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordControlCenter.Application.Tests;

public sealed class ChannelOperationSchedulerTests
{
    [Fact]
    public async Task DuplicatePlanSubmissionIsRejected()
    {
        var history = new MemoryHistoryRepository();
        await using var scheduler = CreateScheduler(new ControlledExecutor(), history);
        await scheduler.InitializeAsync(CancellationToken.None);
        var plan = ChannelOperationExecutionTests.CreatePlan(1);

        var first = await scheduler.EnqueueAsync(plan, CancellationToken.None);
        var duplicate = await scheduler.EnqueueAsync(plan, CancellationToken.None);

        Assert.True(first.Accepted);
        Assert.False(duplicate.Accepted);
        Assert.Contains("already", duplicate.Error, StringComparison.OrdinalIgnoreCase);
        await WaitForTerminalAsync(scheduler, plan.OperationId);
    }

    [Fact]
    public async Task SameBotAndServerOperationsNeverOverlap()
    {
        var executor = new ControlledExecutor(delay: TimeSpan.FromMilliseconds(80));
        await using var scheduler = CreateScheduler(executor);
        await scheduler.InitializeAsync(CancellationToken.None);
        var plans = Enumerable.Range(0, 3)
            .Select(_ => ChannelOperationExecutionTests.CreatePlan(1))
            .ToArray();

        foreach (var plan in plans)
        {
            Assert.True((await scheduler.EnqueueAsync(plan, CancellationToken.None)).Accepted);
        }

        await Task.WhenAll(plans.Select(plan => WaitForTerminalAsync(scheduler, plan.OperationId)));

        Assert.Equal(
            1,
            executor.MaximumFor(
                OperationTestFixture.BotId,
                OperationTestFixture.ServerId));
    }

    [Fact]
    public async Task DifferentServersCanUseSeparateExecutionStreams()
    {
        var executor = new ControlledExecutor(delay: TimeSpan.FromMilliseconds(120));
        await using var scheduler = CreateScheduler(executor);
        await scheduler.InitializeAsync(CancellationToken.None);
        var first = ChannelOperationExecutionTests.CreatePlan(1);
        var second = ChannelOperationExecutionTests.CreatePlan(1, 101);

        await scheduler.EnqueueAsync(first, CancellationToken.None);
        await scheduler.EnqueueAsync(second, CancellationToken.None);
        await Task.WhenAll(
            WaitForTerminalAsync(scheduler, first.OperationId),
            WaitForTerminalAsync(scheduler, second.OperationId));

        Assert.Equal(2, executor.MaximumOverall);
    }

    [Fact]
    public async Task CancellationWhileWaitingForServerGateSendsNoStep()
    {
        var executor = new ControlledExecutor(block: true);
        await using var scheduler = CreateScheduler(executor);
        await scheduler.InitializeAsync(CancellationToken.None);
        var first = ChannelOperationExecutionTests.CreatePlan(1);
        var second = ChannelOperationExecutionTests.CreatePlan(1);
        await scheduler.EnqueueAsync(first, CancellationToken.None);
        await scheduler.EnqueueAsync(second, CancellationToken.None);
        await WaitForStateAsync(scheduler, first.OperationId, ChannelOperationState.Running);

        Assert.True(scheduler.Cancel(second.OperationId));
        executor.Release();
        var cancelled = await WaitForTerminalAsync(scheduler, second.OperationId);
        await WaitForTerminalAsync(scheduler, first.OperationId);

        Assert.Equal(ChannelOperationState.Cancelled, cancelled.State);
        Assert.All(cancelled.Result!.StepResults, step => Assert.True(step.WasCancelled));
        Assert.Equal(1, executor.ExecutionCount);
    }

    [Fact]
    public async Task FailureOnOneServerDoesNotStopAnotherStream()
    {
        var executor = new ControlledExecutor(failedServerId: OperationTestFixture.ServerId);
        await using var scheduler = CreateScheduler(executor);
        await scheduler.InitializeAsync(CancellationToken.None);
        var failing = ChannelOperationExecutionTests.CreatePlan(1);
        var healthy = ChannelOperationExecutionTests.CreatePlan(1, 101);

        await scheduler.EnqueueAsync(failing, CancellationToken.None);
        await scheduler.EnqueueAsync(healthy, CancellationToken.None);
        var results = await Task.WhenAll(
            WaitForTerminalAsync(scheduler, failing.OperationId),
            WaitForTerminalAsync(scheduler, healthy.OperationId));

        Assert.Contains(results, snapshot => snapshot.State == ChannelOperationState.Failed);
        Assert.Contains(results, snapshot => snapshot.State == ChannelOperationState.Completed);
    }

    [Fact]
    public async Task InterruptedPersistedOperationRequiresReconciliationOnStartup()
    {
        var history = new MemoryHistoryRepository();
        var plan = ChannelOperationExecutionTests.CreatePlan(2);
        await history.AddAsync(
            History(plan, ChannelOperationState.Running),
            CancellationToken.None);
        var executor = new ControlledExecutor();
        await using var scheduler = CreateScheduler(executor, history);

        await scheduler.InitializeAsync(CancellationToken.None);

        var snapshot = Assert.Single(scheduler.Snapshots);
        Assert.Equal(ChannelOperationState.ReconciliationRequired, snapshot.State);
        Assert.Equal(
            OperationReconciliationStatus.ManualReviewRequired,
            snapshot.Result!.Reconciliation.Status);
        Assert.Equal(0, executor.ExecutionCount);
    }

    [Fact]
    public async Task DisposingSchedulerCancelsRunningWorkers()
    {
        var executor = new ControlledExecutor(block: true);
        var scheduler = CreateScheduler(executor);
        await scheduler.InitializeAsync(CancellationToken.None);
        var plan = ChannelOperationExecutionTests.CreatePlan(1);
        await scheduler.EnqueueAsync(plan, CancellationToken.None);
        await WaitForStateAsync(scheduler, plan.OperationId, ChannelOperationState.Running);

        await scheduler.DisposeAsync();

        Assert.True(executor.ObservedCancellation);
    }

    private static ChannelOperationScheduler CreateScheduler(
        IChannelOperationExecutor executor,
        MemoryHistoryRepository? history = null) =>
        new(
            executor,
            history ?? new MemoryHistoryRepository(),
            NullLogger<ChannelOperationScheduler>.Instance);

    private static async Task<QueuedOperationSnapshot> WaitForTerminalAsync(
        ChannelOperationScheduler scheduler,
        Guid operationId)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            var snapshot = scheduler.Snapshots.Single(item =>
                item.Plan.OperationId == operationId);
            if (snapshot.State is ChannelOperationState.Completed
                or ChannelOperationState.Failed
                or ChannelOperationState.Stale
                or ChannelOperationState.Cancelled
                or ChannelOperationState.PartiallyCompleted
                or ChannelOperationState.ReconciliationRequired)
            {
                return snapshot;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Operation did not reach a terminal state.");
    }

    private static async Task WaitForStateAsync(
        ChannelOperationScheduler scheduler,
        Guid operationId,
        ChannelOperationState expected)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            if (scheduler.Snapshots.Single(item =>
                    item.Plan.OperationId == operationId).State == expected)
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"Operation did not reach {expected}.");
    }

    private static OperationHistoryEntry History(
        OperationPlan plan,
        ChannelOperationState state) =>
        new(
            plan.OperationId,
            plan.CorrelationId,
            plan.OperationType,
            plan.BotProfileId,
            plan.ServerId,
            plan.ServerNameSnapshot,
            string.Join(',', plan.ExactTargetIds),
            string.Join(", ", plan.Steps.Select(step => step.Target.DisplayName)),
            plan.CreatedAt,
            state == ChannelOperationState.Pending ? null : DateTimeOffset.UtcNow,
            null,
            state,
            0,
            0,
            0,
            "Not started",
            null,
            null,
            0,
            plan.AuditReason,
            JsonSerializer.Serialize(plan),
            null);
}

internal sealed class ControlledExecutor(
    TimeSpan? delay = null,
    bool block = false,
    ulong? failedServerId = null) : IChannelOperationExecutor
{
    private readonly ConcurrentDictionary<(Guid, ulong), int> _active = new();
    private readonly ConcurrentDictionary<(Guid, ulong), int> _maximum = new();
    private readonly TaskCompletionSource _release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _activeOverall;
    private int _maximumOverall;
    private int _executionCount;
    private int _observedCancellation;

    public int MaximumOverall => Volatile.Read(ref _maximumOverall);
    public int ExecutionCount => Volatile.Read(ref _executionCount);
    public bool ObservedCancellation => Volatile.Read(ref _observedCancellation) != 0;

    public int MaximumFor(Guid botId, ulong serverId) =>
        _maximum.GetValueOrDefault((botId, serverId));

    public void Release() => _release.TrySetResult();

    public async Task<ChannelOperationResult> ExecuteAsync(
        OperationPlan plan,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _executionCount);
        var key = (plan.BotProfileId, plan.ServerId);
        var active = _active.AddOrUpdate(key, 1, (_, value) => value + 1);
        _maximum.AddOrUpdate(key, active, (_, value) => Math.Max(value, active));
        var overall = Interlocked.Increment(ref _activeOverall);
        UpdateMaximum(ref _maximumOverall, overall);
        var started = DateTimeOffset.UtcNow;
        progress?.Report(
            new OperationProgress(
                plan.OperationId,
                ChannelOperationState.Running,
                0,
                plan.Steps.Length,
                1,
                "Controlled test execution.",
                started));
        try
        {
            if (block)
            {
                await _release.Task.WaitAsync(cancellationToken);
            }
            else if (delay is { } duration)
            {
                await Task.Delay(duration, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            Interlocked.Exchange(ref _observedCancellation, 1);
            throw;
        }
        finally
        {
            _active.AddOrUpdate(key, 0, (_, value) => value - 1);
            Interlocked.Decrement(ref _activeOverall);
        }

        var failed = failedServerId == plan.ServerId;
        var state = failed ? ChannelOperationState.Failed : ChannelOperationState.Completed;
        var failure = failed
            ? new OperationFailure(
                OperationFailureKind.DiscordRejected,
                "CONTROLLED_FAILURE",
                "Controlled failure.",
                null,
                false,
                OperationOutcomeCertainty.KnownFailed)
            : null;
        var now = DateTimeOffset.UtcNow;
        var stepResults = plan.Steps
            .Select(step => new OperationStepResult(
                step.StepId,
                step.Order,
                step.Description,
                !failed,
                false,
                null,
                started,
                now,
                1,
                failure,
                false,
                false))
            .ToImmutableArray();
        return new ChannelOperationResult(
            plan.OperationId,
            plan.CorrelationId,
            state,
            started,
            now,
            stepResults,
            failed ? 0 : stepResults.Length,
            failed ? stepResults.Length : 0,
            0,
            failure,
            new OperationReconciliationResult(
                OperationReconciliationStatus.NotRequired,
                "Controlled result.",
                [],
                now),
            null,
            plan.CompensationCapability,
            "Controlled result.");
    }

    private static void UpdateMaximum(ref int target, int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (candidate <= current
                || Interlocked.CompareExchange(ref target, candidate, current) == current)
            {
                return;
            }
        }
    }
}
