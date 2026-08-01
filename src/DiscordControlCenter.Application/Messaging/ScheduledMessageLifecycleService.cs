using DiscordControlCenter.Core.Messaging;

namespace DiscordControlCenter.Application.Messaging;

public sealed class ScheduledMessageLifecycleService(IScheduledMessageRepository schedules, IScheduledMessageDraftService drafts, IScheduledMessageService recurrence) : IScheduledMessageLifecycleService
{
    public Task<ScheduledLifecycleResult> ValidateEnableAsync(ScheduledLifecycleRequest request, CancellationToken cancellationToken) => ValidateActivationAsync(request, ScheduledMessageLifecycle.Draft, cancellationToken);
    public async Task<ScheduledLifecycleResult> EnableAsync(ScheduledLifecycleRequest request, CancellationToken cancellationToken) => await ActivateAsync(request, ScheduledMessageLifecycle.Draft, cancellationToken).ConfigureAwait(false);
    public Task<ScheduledLifecycleResult> ValidateReEnableAsync(ScheduledLifecycleRequest request, CancellationToken cancellationToken) => ValidateActivationAsync(request, null, cancellationToken);
    public async Task<ScheduledLifecycleResult> ReEnableAsync(ScheduledLifecycleRequest request, CancellationToken cancellationToken) => await ActivateAsync(request, null, cancellationToken).ConfigureAwait(false);
    public Task<ScheduledLifecycleResult> DisableAsync(ScheduledLifecycleRequest request, CancellationToken cancellationToken) => TransitionAsync(request, [ScheduledMessageLifecycle.Enabled], ScheduledMessageLifecycle.Disabled, cancellationToken);
    public Task<ScheduledLifecycleResult> ArchiveAsync(ScheduledLifecycleRequest request, CancellationToken cancellationToken) => TransitionAsync(request, [ScheduledMessageLifecycle.Draft, ScheduledMessageLifecycle.Enabled, ScheduledMessageLifecycle.Disabled, ScheduledMessageLifecycle.Faulted, ScheduledMessageLifecycle.Expired], ScheduledMessageLifecycle.Archived, cancellationToken);
    public async Task<ScheduledDeleteEligibility> GetDeleteEligibilityAsync(ScheduledLifecycleRequest request, CancellationToken cancellationToken)
    {
        var definition = await LoadScopedAsync(request, cancellationToken).ConfigureAwait(false);
        if (definition is null) return new(false, ScheduledMessageLifecycle.Archived, new(0, 0, 0, 0), "This schedule is unavailable in the current bot and server scope.", null);
        var lifecycle = ScheduledMessageQueryService.ResolveLifecycle(definition);
        var dependencies = await schedules.GetDependencySummaryAsync(definition.Id, cancellationToken).ConfigureAwait(false);
        var canDelete = lifecycle == ScheduledMessageLifecycle.Draft && !dependencies.HasDependencies;
        return new(canDelete, lifecycle, dependencies, canDelete ? "This local Draft has no retained dependencies and can be deleted." : lifecycle != ScheduledMessageLifecycle.Draft ? "Only a local Draft without retained dependencies can be deleted." : "This Draft has retained occurrence or approval history and cannot be deleted.", canDelete ? null : "Archive preserves retained history safely.");
    }
    public async Task<ScheduledLifecycleResult> DeleteAsync(ScheduledLifecycleRequest request, CancellationToken cancellationToken)
    {
        var eligibility = await GetDeleteEligibilityAsync(request, cancellationToken).ConfigureAwait(false);
        if (!eligibility.CanDelete) return new(false, false, eligibility.Lifecycle, null, request.ExpectedRevision, null, null, "DELETE_BLOCKED", eligibility.Explanation, eligibility);
        var deleted = await schedules.TryDeleteAsync(request.ScheduleId, request.ExpectedRevision, cancellationToken).ConfigureAwait(false);
        return deleted ? new(true, false, ScheduledMessageLifecycle.Draft, null, request.ExpectedRevision, null, null, null, "The local Draft was deleted. No Discord message, template, or history was deleted.", eligibility) : new(false, true, ScheduledMessageLifecycle.Draft, null, request.ExpectedRevision, null, null, "REVISION_CONFLICT", "This schedule changed elsewhere. Reload latest before deleting.", eligibility);
    }
    private async Task<ScheduledLifecycleResult> ValidateActivationAsync(ScheduledLifecycleRequest request, ScheduledMessageLifecycle? required, CancellationToken cancellationToken)
    {
        var definition = await LoadScopedAsync(request, cancellationToken).ConfigureAwait(false);
        if (definition is null) return Missing(request);
        var lifecycle = ScheduledMessageQueryService.ResolveLifecycle(definition);
        var allowed = required is not null ? lifecycle == required : lifecycle is ScheduledMessageLifecycle.Disabled or ScheduledMessageLifecycle.Faulted;
        if (!allowed) return Invalid(definition, lifecycle, "This lifecycle transition is not allowed.");
        var validation = await drafts.ValidateAsync(definition, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid || recurrence.GetNextOccurrence(definition with { IsEnabled = true }, DateTimeOffset.UtcNow) is null) return new(false, false, lifecycle, null, definition.Revision, null, validation, "VALIDATION_BLOCKED", validation.IsValid ? "This schedule has no valid future occurrence." : string.Join(" ", validation.Errors));
        return new(true, false, lifecycle, ScheduledMessageLifecycle.Enabled, definition.Revision, definition.Revision + 1, validation, null, "Validation passed. Enabling remains a local lifecycle transition and does not send a message.");
    }
    private async Task<ScheduledLifecycleResult> ActivateAsync(ScheduledLifecycleRequest request, ScheduledMessageLifecycle? required, CancellationToken cancellationToken)
    {
        var validation = await ValidateActivationAsync(request, required, cancellationToken).ConfigureAwait(false);
        if (!validation.Success) return validation;
        return await TransitionAsync(request, required is null ? [ScheduledMessageLifecycle.Disabled, ScheduledMessageLifecycle.Faulted] : [required.Value], ScheduledMessageLifecycle.Enabled, cancellationToken).ConfigureAwait(false);
    }
    private async Task<ScheduledLifecycleResult> TransitionAsync(ScheduledLifecycleRequest request, IReadOnlyList<ScheduledMessageLifecycle> allowed, ScheduledMessageLifecycle target, CancellationToken cancellationToken)
    {
        var definition = await LoadScopedAsync(request, cancellationToken).ConfigureAwait(false); if (definition is null) return Missing(request);
        var current = ScheduledMessageQueryService.ResolveLifecycle(definition);
        if (!allowed.Contains(current)) return Invalid(definition, current, "This lifecycle transition is not allowed.");
        if (definition.Revision != request.ExpectedRevision) return new(false, true, current, null, definition.Revision, null, null, "REVISION_CONFLICT", "This schedule changed elsewhere. Reload latest before continuing.");
        var updated = definition with { IsEnabled = target == ScheduledMessageLifecycle.Enabled, SavedLifecycle = target, Revision = definition.Revision + 1 };
        var saved = await schedules.TrySaveAsync(updated, request.ExpectedRevision, cancellationToken).ConfigureAwait(false);
        return saved ? new(true, false, current, target, definition.Revision, updated.Revision, null, null, $"Schedule is now {ScheduledMessagePresentation.LifecycleLabel(target)}. Existing occurrences and history were preserved.") : new(false, true, current, null, definition.Revision, null, null, "REVISION_CONFLICT", "This schedule changed elsewhere. Reload latest before continuing.");
    }
    private async Task<ScheduledMessageDefinition?> LoadScopedAsync(ScheduledLifecycleRequest request, CancellationToken token) { var definition = await schedules.GetScheduleAsync(request.ScheduleId, token).ConfigureAwait(false); return definition is not null && definition.BotProfileId == request.BotProfileId && definition.Destination.ServerId == request.ServerId ? definition : null; }
    private static ScheduledLifecycleResult Missing(ScheduledLifecycleRequest request) => new(false, false, ScheduledMessageLifecycle.Archived, null, request.ExpectedRevision, null, null, "SCOPE_UNAVAILABLE", "This schedule is unavailable in the current bot and server scope.");
    private static ScheduledLifecycleResult Invalid(ScheduledMessageDefinition definition, ScheduledMessageLifecycle lifecycle, string explanation) => new(false, false, lifecycle, null, definition.Revision, null, null, "INVALID_TRANSITION", explanation);
}
