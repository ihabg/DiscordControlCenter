using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using System.Threading.Channels;
using DiscordControlCenter.Core.Operations;
using Microsoft.Extensions.Logging;

namespace DiscordControlCenter.Application.Operations;

public sealed class ChannelOperationScheduler : IChannelOperationScheduler
{
    private const int Capacity = 32;
    private const int WorkerCount = 2;
    private readonly IChannelOperationExecutor _executor;
    private readonly IOperationHistoryRepository _historyRepository;
    private readonly IOperationHistoryQueryRepository? _historyQueries;
    private readonly ILogger<ChannelOperationScheduler> _logger;
    private readonly Channel<QueuedItem> _queue;
    private readonly ConcurrentDictionary<Guid, QueuedItem> _items = new();
    private readonly ConcurrentDictionary<(Guid BotId, ulong ServerId), SemaphoreSlim> _serverGates = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task[] _workers;
    private int _pendingCount;
    private int _initialized;
    private int _disposed;

    public ChannelOperationScheduler(
        IChannelOperationExecutor executor,
        IOperationHistoryRepository historyRepository,
        ILogger<ChannelOperationScheduler> logger,
        IOperationHistoryQueryRepository? historyQueries = null)
    {
        _executor = executor;
        _historyRepository = historyRepository;
        _logger = logger;
        _historyQueries = historyQueries;
        _queue = Channel.CreateBounded<QueuedItem>(
            new BoundedChannelOptions(Capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
        _workers = Enumerable.Range(0, WorkerCount)
            .Select(_ => Task.Run(WorkerAsync))
            .ToArray();
    }

    public event EventHandler<QueuedOperationSnapshot>? OperationChanged;

    public IReadOnlyList<QueuedOperationSnapshot> Snapshots =>
        _items.Values
            .Select(item => item.Snapshot)
            .OrderByDescending(snapshot => snapshot.EnqueuedAt)
            .ToArray();

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
        {
            return;
        }

        var recent = await _historyRepository
            .GetRecentAsync(100, cancellationToken)
            .ConfigureAwait(false);
        foreach (var history in recent.Where(entry =>
                     entry.State is not ChannelOperationState.Pending
                         and not ChannelOperationState.Running
                         and not ChannelOperationState.Cancelling))
        {
            var hydrated = TryHydrateHistory(history);
            if (hydrated is not null)
            {
                _items.TryAdd(hydrated.Plan.OperationId, hydrated);
            }
        }

        var interrupted = await _historyRepository
            .GetInterruptedAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var history in interrupted)
        {
            OperationPlan? plan;
            try
            {
                plan = JsonSerializer.Deserialize<OperationPlan>(history.PlanJson);
            }
            catch (JsonException)
            {
                plan = null;
            }

            if (plan is null)
            {
                continue;
            }

            var failure = new OperationFailure(
                OperationFailureKind.UncertainOutcome,
                "INTERRUPTED_RECONCILIATION_REQUIRED",
                "The application stopped while this operation was active. Review Discord state and regenerate a preview before retrying.",
                null,
                false,
                OperationOutcomeCertainty.Uncertain);
            var now = DateTimeOffset.UtcNow;
            var result = new ChannelOperationResult(
                plan.OperationId,
                plan.CorrelationId,
                ChannelOperationState.ReconciliationRequired,
                history.StartedAt ?? history.CreatedAt,
                now,
                ImmutableArray<OperationStepResult>.Empty,
                history.CompletedCount,
                history.FailedCount,
                history.CancelledCount,
                failure,
                new OperationReconciliationResult(
                    OperationReconciliationStatus.ManualReviewRequired,
                    failure.SafeMessage,
                    ImmutableArray<ulong>.Empty,
                    now),
                history.BackupIdentifier,
                plan.CompensationCapability,
                "Automatic compensation was not attempted after process interruption.");
            var item = new QueuedItem(
                plan,
                history.CreatedAt,
                new CancellationTokenSource(),
                new QueuedOperationSnapshot(
                    plan,
                    ChannelOperationState.ReconciliationRequired,
                    0,
                    null,
                    result,
                    history.CreatedAt));
            _items[plan.OperationId] = item;
            await _historyRepository
                .UpdateAsync(
                    ChannelOperationExecutor.BuildHistory(
                        plan,
                        result.State,
                        result.StartedAt,
                        result.FinishedAt,
                        result,
                        result.BackupIdentifier,
                        history.DurationMilliseconds),
                    cancellationToken)
                .ConfigureAwait(false);
            Publish(item);
        }
    }

    private static QueuedItem? TryHydrateHistory(OperationHistoryEntry history)
    {
        try
        {
            var plan = JsonSerializer.Deserialize<OperationPlan>(history.PlanJson);
            if (plan is null)
            {
                return null;
            }

            var result = string.IsNullOrWhiteSpace(history.ResultJson)
                ? null
                : JsonSerializer.Deserialize<ChannelOperationResult>(history.ResultJson);
            var snapshot = new QueuedOperationSnapshot(
                plan,
                history.State,
                0,
                null,
                result,
                history.CreatedAt);
            return new QueuedItem(
                plan,
                history.CreatedAt,
                new CancellationTokenSource(),
                snapshot);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<QueueSubmissionResult> EnqueueAsync(
        OperationPlan plan,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_items.ContainsKey(plan.OperationId)
            || await _historyRepository.GetAsync(plan.OperationId, cancellationToken).ConfigureAwait(false)
                is not null)
        {
            return new QueueSubmissionResult(false, null, "This operation plan was already submitted.");
        }

        var enqueuedAt = DateTimeOffset.UtcNow;
        var position = Interlocked.Increment(ref _pendingCount);
        var item = new QueuedItem(
            plan,
            enqueuedAt,
            CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token),
            new QueuedOperationSnapshot(
                plan,
                ChannelOperationState.Pending,
                position,
                null,
                null,
                enqueuedAt));
        if (!_items.TryAdd(plan.OperationId, item))
        {
            Interlocked.Decrement(ref _pendingCount);
            item.Cancellation.Dispose();
            return new QueueSubmissionResult(false, null, "This operation plan was already submitted.");
        }

        try
        {
            await _historyRepository
                .AddAsync(
                    ChannelOperationExecutor.BuildHistory(
                        plan,
                        ChannelOperationState.Pending,
                        null,
                        null,
                        null,
                        null,
                        0),
                    cancellationToken)
                .ConfigureAwait(false);
            await RecordTransitionSafeAsync(
                    plan.OperationId,
                    ChannelOperationState.Pending,
                    "QUEUED",
                    "The immutable plan was persisted and queued.")
                .ConfigureAwait(false);
            await _queue.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
            Publish(item);
            return new QueueSubmissionResult(true, position, null);
        }
        catch (Exception exception)
        {
            _items.TryRemove(plan.OperationId, out _);
            Interlocked.Decrement(ref _pendingCount);
            item.Cancellation.Dispose();
            QueueSubmissionFailedLog(
                _logger,
                plan.OperationId,
                exception.GetType().Name,
                null);
            return new QueueSubmissionResult(
                false,
                null,
                "The operation could not be persisted and was not queued.");
        }
    }

    public bool Cancel(Guid operationId)
    {
        if (!_items.TryGetValue(operationId, out var item)
            || item.Snapshot.State is ChannelOperationState.Completed
                or ChannelOperationState.Failed
                or ChannelOperationState.Stale
                or ChannelOperationState.Cancelled
                or ChannelOperationState.PartiallyCompleted
                or ChannelOperationState.ReconciliationRequired)
        {
            return false;
        }

        item.Cancellation.Cancel();
        item.Snapshot = item.Snapshot with
        {
            State = ChannelOperationState.Cancelling,
            Progress = item.Snapshot.Progress is null
                ? null
                : item.Snapshot.Progress with
                {
                    State = ChannelOperationState.Cancelling,
                    Message = "Cancellation requested. A request already accepted by Discord cannot be undone."
                }
        };
        Publish(item);
        _ = RecordTransitionSafeAsync(
            item.Plan.OperationId,
            ChannelOperationState.Cancelling,
            "CANCELLATION_REQUESTED",
            "The user requested cancellation; an accepted Discord request is not undone.");
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.Writer.TryComplete();
        _lifetime.Cancel();
        foreach (var item in _items.Values)
        {
            item.Cancellation.Cancel();
        }

        try
        {
            await Task.WhenAll(_workers).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            QueueShutdownTimeoutLog(_logger, null);
        }
        finally
        {
            foreach (var item in _items.Values)
            {
                item.Cancellation.Dispose();
            }

            foreach (var gate in _serverGates.Values)
            {
                gate.Dispose();
            }

            _lifetime.Dispose();
        }
    }

    private async Task WorkerAsync()
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                Interlocked.Decrement(ref _pendingCount);
                var key = (item.Plan.BotProfileId, item.Plan.ServerId);
                var gate = _serverGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
                try
                {
                    await gate.WaitAsync(item.Cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await ExecuteCancelledAsync(item).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    if (item.Cancellation.IsCancellationRequested)
                    {
                        await ExecuteCancelledAsync(item).ConfigureAwait(false);
                        continue;
                    }

                    item.Snapshot = item.Snapshot with
                    {
                        State = ChannelOperationState.Running,
                        QueuePosition = 0
                    };
                    await RecordTransitionSafeAsync(
                            item.Plan.OperationId,
                            ChannelOperationState.Running,
                            "WORKER_STARTED",
                            "A bounded queue worker started the approved plan.")
                        .ConfigureAwait(false);
                    Publish(item);
                    var progress = new InlineProgress<OperationProgress>(
                        update =>
                        {
                            var previousState = item.Snapshot.State;
                            item.Snapshot = item.Snapshot with
                            {
                                State = update.State,
                                Progress = update
                            };
                            Publish(item);
                            if (previousState != update.State)
                            {
                                _ = RecordTransitionSafeAsync(
                                    item.Plan.OperationId,
                                    update.State,
                                    "EXECUTION_PROGRESS",
                                    update.Message);
                            }
                        });
                    var result = await _executor
                        .ExecuteAsync(item.Plan, progress, item.Cancellation.Token)
                        .ConfigureAwait(false);
                    item.Snapshot = item.Snapshot with
                    {
                        State = result.State,
                        Progress = item.Snapshot.Progress is null
                            ? null
                            : item.Snapshot.Progress with { State = result.State },
                        Result = result
                    };
                    await RecordTransitionSafeAsync(
                            item.Plan.OperationId,
                            result.State,
                            "EXECUTION_FINISHED",
                            result.Failure?.SafeMessage ?? "The operation reached a terminal state.")
                        .ConfigureAwait(false);
                    Publish(item);
                }
                catch (OperationCanceledException)
                {
                    await ExecuteCancelledAsync(item).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    WorkerFailedLog(
                        _logger,
                        item.Plan.OperationId,
                        exception.GetType().Name,
                        null);
                    var result = BuildUnexpectedFailure(item.Plan, exception.GetType().Name);
                    item.Snapshot = item.Snapshot with
                    {
                        State = result.State,
                        Result = result
                    };
                    try
                    {
                        await _historyRepository
                            .UpdateAsync(
                                ChannelOperationExecutor.BuildHistory(
                                    item.Plan,
                                    result.State,
                                    result.StartedAt,
                                    result.FinishedAt,
                                    result,
                                    null,
                                    0),
                                CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception historyException)
                    {
                        HistoryRecoveryFailedLog(
                            _logger,
                            item.Plan.OperationId,
                            historyException.GetType().Name,
                            null);
                    }

                    await RecordTransitionSafeAsync(
                            item.Plan.OperationId,
                            result.State,
                            "WORKER_FAILURE",
                            result.Failure?.SafeMessage ?? "The worker stopped for manual review.")
                        .ConfigureAwait(false);
                    Publish(item);
                }
                finally
                {
                    gate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task ExecuteCancelledAsync(QueuedItem item)
    {
        var now = DateTimeOffset.UtcNow;
        var steps = item.Plan.Steps
            .Select(
                step => new OperationStepResult(
                    step.StepId,
                    step.Order,
                    step.Description,
                    false,
                    true,
                    null,
                    now,
                    now,
                    0,
                    new OperationFailure(
                        OperationFailureKind.Cancelled,
                        "CANCELLED_NOT_STARTED",
                        "The step was not started.",
                        null,
                        false,
                        OperationOutcomeCertainty.KnownFailed),
                    false,
                    false))
            .ToImmutableArray();
        var result = new ChannelOperationResult(
            item.Plan.OperationId,
            item.Plan.CorrelationId,
            ChannelOperationState.Cancelled,
            now,
            now,
            steps,
            0,
            0,
            steps.Length,
            steps.FirstOrDefault()?.Failure,
            new OperationReconciliationResult(
                OperationReconciliationStatus.NotRequired,
                "No request was sent.",
                ImmutableArray<ulong>.Empty,
                now),
            null,
            item.Plan.CompensationCapability,
            "No compensation was required.");
        item.Snapshot = item.Snapshot with
        {
            State = result.State,
            QueuePosition = 0,
            Result = result
        };
        await _historyRepository
            .UpdateAsync(
                ChannelOperationExecutor.BuildHistory(
                    item.Plan,
                    result.State,
                    result.StartedAt,
                    result.FinishedAt,
                    result,
                    null,
                    0),
                CancellationToken.None)
            .ConfigureAwait(false);
        await RecordTransitionSafeAsync(
                item.Plan.OperationId,
                result.State,
                "CANCELLED_BEFORE_EXECUTION",
                "The queued operation was cancelled before a Discord request was sent.")
            .ConfigureAwait(false);
        Publish(item);
    }

    private async Task RecordTransitionSafeAsync(
        Guid operationId,
        ChannelOperationState state,
        string reasonCode,
        string safeSummary)
    {
        if (_historyQueries is null)
        {
            return;
        }

        try
        {
            await _historyQueries.AddTransitionAsync(
                    new OperationStateTransition(
                        0,
                        operationId,
                        state,
                        DateTimeOffset.UtcNow,
                        reasonCode,
                        safeSummary),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            HistoryRecoveryFailedLog(
                _logger,
                operationId,
                exception.GetType().Name,
                null);
        }
    }

    private void Publish(QueuedItem item) =>
        OperationChanged?.Invoke(this, item.Snapshot);

    private static ChannelOperationResult BuildUnexpectedFailure(
        OperationPlan plan,
        string exceptionType)
    {
        var now = DateTimeOffset.UtcNow;
        var failure = new OperationFailure(
            OperationFailureKind.Internal,
            "OPERATION_WORKER_FAILURE",
            "The operation worker encountered an unexpected internal error.",
            exceptionType,
            false,
            OperationOutcomeCertainty.Uncertain);
        return new ChannelOperationResult(
            plan.OperationId,
            plan.CorrelationId,
            ChannelOperationState.ReconciliationRequired,
            now,
            now,
            ImmutableArray<OperationStepResult>.Empty,
            0,
            1,
            0,
            failure,
            new OperationReconciliationResult(
                OperationReconciliationStatus.ManualReviewRequired,
                "Review Discord state before creating another plan.",
                ImmutableArray<ulong>.Empty,
                now),
            null,
            plan.CompensationCapability,
            "Automatic compensation was not attempted after an internal worker failure.");
    }

    private sealed class QueuedItem(
        OperationPlan plan,
        DateTimeOffset enqueuedAt,
        CancellationTokenSource cancellation,
        QueuedOperationSnapshot snapshot)
    {
        public OperationPlan Plan { get; } = plan;
        public DateTimeOffset EnqueuedAt { get; } = enqueuedAt;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public QueuedOperationSnapshot Snapshot { get; set; } = snapshot;
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static readonly Action<ILogger, Guid, string, Exception?> QueueSubmissionFailedLog =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Warning,
            new EventId(2401, nameof(QueueSubmissionFailedLog)),
            "Operation {OperationId} queue submission failed with {ExceptionType}");

    private static readonly Action<ILogger, Guid, string, Exception?> WorkerFailedLog =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Error,
            new EventId(2402, nameof(WorkerFailedLog)),
            "Operation {OperationId} worker failed with {ExceptionType}");

    private static readonly Action<ILogger, Guid, string, Exception?> HistoryRecoveryFailedLog =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Warning,
            new EventId(2403, nameof(HistoryRecoveryFailedLog)),
            "Operation {OperationId} recovery history update failed with {ExceptionType}");

    private static readonly Action<ILogger, Exception?> QueueShutdownTimeoutLog =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(2404, nameof(QueueShutdownTimeoutLog)),
            "Channel operation queue workers did not terminate within the shutdown timeout");
}
