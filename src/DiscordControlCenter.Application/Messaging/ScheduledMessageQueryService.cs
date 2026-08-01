using System.Globalization;
using DiscordControlCenter.Core.Messaging;

namespace DiscordControlCenter.Application.Messaging;

public sealed class ScheduledMessageQueryService(
    IScheduledMessageRepository repository,
    IScheduledMessageService recurrence) : IScheduledMessageQueryService
{
    public Task<ScheduledMessagePage> QueryAsync(ScheduledMessageQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.BotProfileId == Guid.Empty) throw new ArgumentException("Select a bot before viewing schedules.", nameof(query));
        if (query.ServerId == 0) throw new ArgumentException("Select a server before viewing schedules.", nameof(query));
        return repository.QuerySchedulesAsync(query with { PageNumber = Math.Max(1, query.PageNumber), PageSize = Math.Clamp(query.PageSize, 1, 200) }, cancellationToken);
    }

    public async Task<ScheduledMessageDetail?> GetDetailAsync(Guid botProfileId, ulong serverId, Guid scheduleId, CancellationToken cancellationToken)
    {
        if (botProfileId == Guid.Empty || serverId == 0 || scheduleId == Guid.Empty) return null;
        var definition = await repository.GetScheduleAsync(scheduleId, cancellationToken).ConfigureAwait(false);
        if (definition is null || definition.BotProfileId != botProfileId || definition.Destination.ServerId != serverId) return null;
        var lifecycle = definition.EndAt is { } end && end < DateTimeOffset.UtcNow ? ScheduledMessageLifecycle.Expired : definition.IsEnabled ? ScheduledMessageLifecycle.Enabled : ScheduledMessageLifecycle.Disabled;
        var zone = TryGetZone(definition.TimeZoneId, out var warning);
        return new ScheduledMessageDetail(definition.Id, definition.Name, lifecycle, LifecycleExplanation(lifecycle), RecurrenceSummary(definition, recurrence), zone, warning, definition);
    }

    internal static string LifecycleExplanation(ScheduledMessageLifecycle lifecycle) => lifecycle switch
    {
        ScheduledMessageLifecycle.Draft => "Not currently executable. Editing will be available in a future increment.",
        ScheduledMessageLifecycle.Enabled => "Eligible for future occurrence reservation.",
        ScheduledMessageLifecycle.Disabled => "No new occurrences will be reserved; existing history is retained.",
        ScheduledMessageLifecycle.Faulted => "Paused because a safe schedule fault needs attention.",
        ScheduledMessageLifecycle.Expired => "No valid future occurrence remains.",
        ScheduledMessageLifecycle.Archived => "Read-only historical configuration; no future execution.",
        _ => "Schedule state is unavailable."
    };

    internal static string RecurrenceSummary(ScheduledMessageDefinition definition, IScheduledMessageService recurrence)
    {
        try
        {
            _ = recurrence.GetNextOccurrence(definition, DateTimeOffset.UtcNow);
            var time = definition.LocalTime.ToString("HH:mm", CultureInfo.InvariantCulture);
            return definition.Recurrence switch
            {
                ScheduledMessageRecurrence.Once => $"One time on {definition.StartAt:dd MMMM yyyy} at {time}",
                ScheduledMessageRecurrence.Daily => $"Every day at {time}",
                ScheduledMessageRecurrence.Weekly when definition.Weekdays.Length == 0 => "Weekly recurrence has no selected weekdays.",
                ScheduledMessageRecurrence.Weekly => $"{string.Join(", ", definition.Weekdays.Select(day => CultureInfo.InvariantCulture.DateTimeFormat.GetDayName(day)))} at {time}",
                _ => "Saved recurrence is unavailable."
            };
        }
        catch { return "Saved recurrence data is unavailable."; }
    }

    private static string TryGetZone(string zoneId, out string? warning)
    {
        try { warning = null; return TimeZoneInfo.FindSystemTimeZoneById(zoneId).DisplayName; }
        catch { warning = "Invalid or unavailable time zone."; return zoneId; }
    }
}
