using DiscordControlCenter.Core.Messaging;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DiscordControlCenter.Application.Messaging;

public interface IScheduledMessageScheduler : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A single bounded polling worker. SQLite owns occurrence uniqueness, so a wake-up,
/// restart, or a second process cannot cause the same scheduled instant to be sent twice.
/// </summary>
public sealed class ScheduledMessageScheduler(
    IScheduledMessageRepository schedules,
    IMessageTemplateRepository templates,
    ITemplateRenderer renderer,
    IMessagePlanBuilder planner,
    IMessageDeliveryExecutor delivery,
    IScheduledMessageService scheduleMath,
    ILogger<ScheduledMessageScheduler> logger) : IScheduledMessageScheduler
{
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _worker;
    private int _initialized;
    private int _disposed;

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
        {
            return Task.CompletedTask;
        }

        cancellationToken.ThrowIfCancellationRequested();
        _worker = Task.Run(WorkerAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        if (_worker is not null)
        {
            try { await _worker.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (TimeoutException) { ShutdownTimeoutLog(logger, null); }
        }

        _lifetime.Dispose();
    }

    private async Task WorkerAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        try
        {
            do
            {
                await ProcessDueAsync(_lifetime.Token).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task ProcessDueAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ScheduledMessageDefinition> enabled;
        try { enabled = await schedules.ListEnabledAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception exception)
        {
            SchedulerReadFailedLog(logger, exception.GetType().Name, null);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var definition in enabled)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var dueAt in scheduleMath.GetDueOccurrences(definition, now).Take(1))
                {
                    if (definition.MissedOccurrencePolicy == MissedOccurrencePolicy.RequireManualApproval
                        && dueAt < now.AddMinutes(-1))
                    {
                        await ReserveManualApprovalAsync(definition, dueAt, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await DeliverOccurrenceAsync(definition, dueAt, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                ScheduleFailedLog(logger, definition.Id, exception.GetType().Name, null);
            }
        }
    }

    private async Task DeliverOccurrenceAsync(
        ScheduledMessageDefinition definition,
        DateTimeOffset dueAt,
        CancellationToken cancellationToken)
    {
        var snapshot = await CreateSnapshotAsync(definition, cancellationToken).ConfigureAwait(false);
        var occurrence = new ScheduledMessageOccurrence(
            Guid.NewGuid(), definition.Id, dueAt, MessageOperationState.Delivering,
            Guid.NewGuid(), null, null)
        {
            ImmutableDeliverySnapshotJson = JsonSerializer.Serialize(snapshot)
        };
        if (!await schedules.TryReserveOccurrenceAsync(occurrence, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        MessageDeliveryResult? result = null;
        try
        {
            var content = definition.InlineContent;
            if (definition.TemplateId is Guid templateId)
            {
                var template = await templates.GetAsync(templateId, cancellationToken).ConfigureAwait(false);
                if (template is null)
                {
                    result = InvalidResult(occurrence, "TEMPLATE_UNAVAILABLE", "The scheduled template is unavailable.");
                }
                else
                {
                    var rendered = renderer.Render(template, new Dictionary<string, string?>());
                    content = rendered.IsSuccess ? rendered.Content : null;
                    if (content is null)
                    {
                        result = InvalidResult(occurrence, "TEMPLATE_INVALID", "The scheduled template could not be rendered safely.");
                    }
                }
            }

            if (result is null && content is not null)
            {
                var draft = new MessageDraft(
                    Guid.NewGuid(), definition.BotProfileId, definition.Destination, content,
                    [], "Scheduled delivery", DateTimeOffset.UtcNow)
                {
                    TemplateId = definition.TemplateId
                };
                var planned = planner.Build(draft, MessageOperationKind.ScheduledChannelMessage);
                result = planned.Plan is null
                    ? InvalidResult(occurrence, "SCHEDULE_MESSAGE_INVALID", "The scheduled message no longer meets delivery limits.")
                    : await delivery.DeliverAsync(
                        planned.Plan with
                        {
                            ScheduledMessageId = definition.Id,
                            OccurrenceId = occurrence.Id
                        },
                        cancellationToken).ConfigureAwait(false);
            }

            result ??= InvalidResult(occurrence, "SCHEDULE_CONTENT_MISSING", "The scheduled message has no deliverable content.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            ScheduleFailedLog(logger, definition.Id, exception.GetType().Name, null);
            result = InvalidResult(
                occurrence,
                "SCHEDULE_RUNTIME_FAILURE",
                "Scheduled delivery stopped before Discord confirmed an outcome.",
                uncertain: true);
        }
        finally
        {
            if (result is not null)
            {
                var completed = occurrence with
                {
                    State = result.State,
                    FinishedAt = result.FinishedAt,
                    SafeFailureCode = result.Failure?.SafeCode
                };
                await schedules.CompleteOccurrenceAsync(completed, CancellationToken.None).ConfigureAwait(false);
                await schedules.SaveAsync(definition with
                {
                    LastRunAt = dueAt,
                    NextRunAt = scheduleMath.GetNextOccurrence(definition, DateTimeOffset.UtcNow)
                }, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task ReserveManualApprovalAsync(
        ScheduledMessageDefinition definition,
        DateTimeOffset dueAt,
        CancellationToken cancellationToken)
    {
        var snapshot = await CreateSnapshotAsync(definition, cancellationToken).ConfigureAwait(false);
        var pending = new ScheduledMessageOccurrence(
            Guid.NewGuid(), definition.Id, dueAt, MessageOperationState.PendingApproval,
            Guid.NewGuid(), null, null)
        {
            ImmutableDeliverySnapshotJson = JsonSerializer.Serialize(snapshot)
        };
        if (await schedules.TryReserveOccurrenceAsync(pending, cancellationToken).ConfigureAwait(false))
        {
            await schedules.SaveAsync(definition with
            {
                LastRunAt = dueAt,
                NextRunAt = scheduleMath.GetNextOccurrence(definition, DateTimeOffset.UtcNow)
            }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<ScheduledDeliverySnapshot> CreateSnapshotAsync(ScheduledMessageDefinition definition, CancellationToken cancellationToken)
    {
        var content = definition.InlineContent;
        int? templateVersion = null;
        if (definition.TemplateId is Guid templateId)
        {
            var template = await templates.GetAsync(templateId, cancellationToken).ConfigureAwait(false);
            content = template?.Content;
            templateVersion = template?.Version;
        }

        return new ScheduledDeliverySnapshot(1, definition, content ?? new MessageContent(string.Empty, null, AllowedMentionPolicy.None), definition.TemplateId, templateVersion, DateTimeOffset.UtcNow);
    }

    private static MessageDeliveryResult InvalidResult(
        ScheduledMessageOccurrence occurrence,
        string code,
        string message,
        bool uncertain = false) =>
        new(
            occurrence.Id,
            occurrence.CorrelationId,
            uncertain ? MessageOperationState.Uncertain : MessageOperationState.Failed,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, 0,
            new MessageDeliveryFailure(
                uncertain ? MessageDeliveryFailureKind.UncertainOutcome : MessageDeliveryFailureKind.Validation,
                code,
                message,
                false,
                uncertain))
        {
            OccurrenceId = occurrence.Id
        };

    private static readonly Action<ILogger, string, Exception?> SchedulerReadFailedLog = LoggerMessage.Define<string>(LogLevel.Warning, new EventId(5201, nameof(SchedulerReadFailedLog)), "Scheduled message query failed with {ExceptionType}");
    private static readonly Action<ILogger, Guid, string, Exception?> ScheduleFailedLog = LoggerMessage.Define<Guid, string>(LogLevel.Warning, new EventId(5202, nameof(ScheduleFailedLog)), "Scheduled message {ScheduleId} failed with {ExceptionType}");
    private static readonly Action<ILogger, Exception?> ShutdownTimeoutLog = LoggerMessage.Define(LogLevel.Warning, new EventId(5203, nameof(ShutdownTimeoutLog)), "Scheduled message worker did not terminate during shutdown");
}
