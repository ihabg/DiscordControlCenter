using DiscordControlCenter.Core.Messaging;

namespace DiscordControlCenter.Application.Messaging;

public sealed class ScheduledMessageService : IScheduledMessageService
{
    public IReadOnlyList<DateTimeOffset> GetDueOccurrences(ScheduledMessageDefinition definition, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!definition.IsEnabled || definition.EndAt is { } end && now > end)
        {
            return [];
        }

        var timeZone = FindTimeZone(definition.TimeZoneId);
        var cursor = definition.LastRunAt ?? definition.StartAt;
        if (cursor > now)
        {
            return [];
        }

        var candidates = new List<DateTimeOffset>();
        var localStart = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(cursor, timeZone).DateTime);
        var localNow = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, timeZone).DateTime);
        for (var day = localStart; day <= localNow && candidates.Count < 366; day = day.AddDays(1))
        {
            if (!Matches(definition, day))
            {
                continue;
            }

            var local = day.ToDateTime(definition.LocalTime, DateTimeKind.Unspecified);
            if (timeZone.IsInvalidTime(local))
            {
                continue;
            }

            var offset = timeZone.GetUtcOffset(local);
            var occurrence = new DateTimeOffset(local, offset);
            if (occurrence >= definition.StartAt && occurrence <= now && (definition.EndAt is null || occurrence <= definition.EndAt))
            {
                candidates.Add(occurrence);
            }
        }

        return definition.MissedOccurrencePolicy switch
        {
            MissedOccurrencePolicy.Skip => [],
            MissedOccurrencePolicy.SendLatestOnly => candidates.TakeLast(1).ToArray(),
            MissedOccurrencePolicy.RequireManualApproval => [],
            _ => []
        };
    }

    private static bool Matches(ScheduledMessageDefinition definition, DateOnly day) =>
        definition.Recurrence switch
        {
            ScheduledMessageRecurrence.Once => day == DateOnly.FromDateTime(definition.StartAt.Date),
            ScheduledMessageRecurrence.Daily => true,
            ScheduledMessageRecurrence.Weekly => definition.Weekdays.Contains(day.DayOfWeek),
            _ => false
        };

    private static TimeZoneInfo FindTimeZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }
}
