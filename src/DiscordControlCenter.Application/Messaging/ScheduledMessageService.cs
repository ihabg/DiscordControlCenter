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
        var cursor = definition.LastRunAt ?? definition.StartAt.AddMinutes(-1);
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
            if (occurrence >= definition.StartAt && occurrence > cursor && occurrence <= now && (definition.EndAt is null || occurrence <= definition.EndAt))
            {
                candidates.Add(occurrence);
            }
        }

        var onTime = candidates.Where(candidate => candidate >= now.AddMinutes(-1)).ToArray();
        var missed = candidates.Where(candidate => candidate < now.AddMinutes(-1)).ToArray();
        return definition.MissedOccurrencePolicy switch
        {
            MissedOccurrencePolicy.Skip => onTime,
            MissedOccurrencePolicy.SendLatestOnly => onTime.Length > 0 ? onTime : missed.TakeLast(1).ToArray(),
            MissedOccurrencePolicy.RequireManualApproval => missed.TakeLast(1).ToArray(),
            _ => []
        };
    }

    public DateTimeOffset? GetNextOccurrence(ScheduledMessageDefinition definition, DateTimeOffset now)
    {
        if (!definition.IsEnabled || definition.EndAt is { } end && now >= end)
        {
            return null;
        }

        var timeZone = FindTimeZone(definition.TimeZoneId);
        var localNow = TimeZoneInfo.ConvertTime(now, timeZone);
        var firstDay = DateOnly.FromDateTime(localNow.DateTime);
        for (var offsetDays = 0; offsetDays <= 366; offsetDays++)
        {
            var day = firstDay.AddDays(offsetDays);
            if (!Matches(definition, day)) continue;
            var local = day.ToDateTime(definition.LocalTime, DateTimeKind.Unspecified);
            if (timeZone.IsInvalidTime(local)) continue;
            var occurrence = new DateTimeOffset(local, timeZone.GetUtcOffset(local));
            if (occurrence > now && occurrence >= definition.StartAt && (definition.EndAt is null || occurrence <= definition.EndAt)) return occurrence;
        }

        return null;
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
