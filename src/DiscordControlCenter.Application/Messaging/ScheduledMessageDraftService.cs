using DiscordControlCenter.Core.Messaging;

namespace DiscordControlCenter.Application.Messaging;

public sealed class ScheduledMessageDraftService(IScheduledMessageRepository schedules, IMessageTemplateRepository templates, IScheduledMessageService recurrence) : IScheduledMessageDraftService
{
    public ScheduledMessageDefinition CreateDraft(Guid botProfileId, MessageDestination destination) => new(Guid.NewGuid(), botProfileId, destination, null, null, ScheduledMessageRecurrence.Daily, new TimeOnly(9, 0), TimeZoneInfo.Utc.Id, [], DateTimeOffset.UtcNow, null, false, MissedOccurrencePolicy.RequireManualApproval, 0, null, null) { Name = "New draft", SavedLifecycle = ScheduledMessageLifecycle.Draft, Revision = 1 };

    public async Task<ScheduledMessageDefinition?> LoadAsync(Guid botProfileId, ulong serverId, Guid scheduleId, CancellationToken cancellationToken)
    {
        var item = await schedules.GetScheduleAsync(scheduleId, cancellationToken).ConfigureAwait(false);
        return item is not null && item.BotProfileId == botProfileId && item.Destination.ServerId == serverId ? item : null;
    }

    public async Task<ScheduledDraftValidation> ValidateAsync(ScheduledMessageDefinition definition, CancellationToken cancellationToken)
    {
        var errors = new List<string>(); var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(definition.Name)) errors.Add("Schedule name is required.");
        if (definition.BotProfileId == Guid.Empty) errors.Add("Select a bot before saving.");
        if (definition.Destination.ServerId == 0 || definition.Destination.ChannelId is null) errors.Add("Select a text-channel destination.");
        if (definition.TemplateId is null && definition.InlineContent is null) errors.Add("Select a template or supported message source.");
        if (definition.TemplateId is Guid templateId && await templates.GetAsync(templateId, cancellationToken).ConfigureAwait(false) is null) errors.Add("The selected template is unavailable.");
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(definition.TimeZoneId); } catch { errors.Add("Select a valid time zone."); }
        if (!Enum.IsDefined(definition.Recurrence)) errors.Add("Select a supported recurrence.");
        if (definition.Recurrence == ScheduledMessageRecurrence.Weekly && definition.Weekdays.Length == 0) errors.Add("Select at least one weekday for weekly recurrence.");
        if (definition.EndAt is { } end && end < definition.StartAt) errors.Add("The end date cannot be before the start date.");
        var summary = ScheduledMessageQueryService.RecurrenceSummary(definition with { IsEnabled = true }, recurrence);
        var next = errors.Any(error => error.Contains("time zone", StringComparison.OrdinalIgnoreCase)) ? null : recurrence.GetNextOccurrence(definition with { IsEnabled = true }, DateTimeOffset.UtcNow);
        if (next is null) warnings.Add("This draft has no future occurrence with its current dates and recurrence.");
        return new(errors, warnings, summary, next is null ? [] : [next.Value]);
    }

    public async Task<ScheduledDraftSaveResult> SaveAsync(ScheduledMessageDefinition definition, int expectedRevision, CancellationToken cancellationToken)
    {
        var validation = await ValidateAsync(definition, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid) return new(false, false, null, validation, "Correct the highlighted draft fields before saving.");
        var saved = definition with { IsEnabled = false, SavedLifecycle = ScheduledMessageLifecycle.Draft, Revision = expectedRevision + 1 };
        var succeeded = await schedules.TrySaveAsync(saved, expectedRevision, cancellationToken).ConfigureAwait(false);
        return succeeded ? new(true, false, saved, validation, "Draft saved. It cannot execute until a later lifecycle command enables it.") : new(false, true, null, validation, "This schedule changed elsewhere. Reload the latest version before saving.");
    }
}
