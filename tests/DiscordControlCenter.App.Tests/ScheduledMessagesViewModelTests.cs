using System.Collections.Immutable;
using DiscordControlCenter.App.ViewModels;
using DiscordControlCenter.Application.Messaging;
using DiscordControlCenter.Core.Messaging;

namespace DiscordControlCenter.App.Tests;

public sealed class ScheduledMessagesViewModelTests
{
    [Fact]
    public async Task NoScopeBlocksQueriesAndScopedFiltersReachTheService()
    {
        var service = new FakeScheduleQueryService();
        using var viewModel = new ScheduledMessagesViewModel(service);

        await viewModel.LoadAsync(CancellationToken.None);
        Assert.Equal(0, service.QueryCount);

        viewModel.SetContext(FakeScheduleQueryService.BotId, "Saved bot", FakeScheduleQueryService.ServerId, "Test server");
        await viewModel.LoadAsync(CancellationToken.None);
        viewModel.Search = "morning";
        viewModel.SelectedLifecycle = viewModel.Lifecycles.Single(item => item.Value == ScheduledMessageLifecycle.Enabled);
        viewModel.SelectedRecurrence = viewModel.Recurrences.Single(item => item.Value == ScheduledMessageRecurrence.Daily);
        viewModel.SelectedTemplate = viewModel.Templates.Single(item => !item.IsAny);
        viewModel.SelectedDestination = viewModel.Destinations.Single(item => !item.IsAny);
        viewModel.SelectedMissedPolicy = viewModel.MissedPolicies.Single(item => item.Value == MissedOccurrencePolicy.RequireManualApproval);
        viewModel.SelectedWarningState = viewModel.WarningStates.Single(item => item.Value == true);
        viewModel.CreatedFrom = new DateTime(2026, 1, 1);
        viewModel.CreatedTo = new DateTime(2026, 1, 2);
        viewModel.NextRunFrom = new DateTime(2026, 2, 1);
        viewModel.NextRunTo = new DateTime(2026, 2, 2);
        viewModel.SelectedSort = viewModel.Sorts.Single(item => item.Value == ScheduledMessageSort.Name);
        viewModel.PageSize = 10;

        await viewModel.LoadAsync(CancellationToken.None);

        var query = Assert.IsType<ScheduledMessageQuery>(service.LastQuery);
        Assert.Equal("morning", query.Search);
        Assert.Equal(ScheduledMessageLifecycle.Enabled, query.Lifecycle);
        Assert.Equal(ScheduledMessageRecurrence.Daily, query.Recurrence);
        Assert.NotNull(query.TemplateId); Assert.NotNull(query.ChannelId);
        Assert.Equal(MissedOccurrencePolicy.RequireManualApproval, query.MissedPolicy);
        Assert.True(query.HasWarning); Assert.NotNull(query.CreatedFrom); Assert.NotNull(query.NextRunTo);
        Assert.Equal(ScheduledMessageSort.Name, query.Sort); Assert.Equal(10, query.PageSize);
        Assert.Contains("Search: morning", viewModel.ActiveFilterSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidDateRangeDoesNotQueryAndSelectionLoadsBoundedSafeOccurrences()
    {
        var service = new FakeScheduleQueryService();
        using var viewModel = new ScheduledMessagesViewModel(service);
        viewModel.SetContext(FakeScheduleQueryService.BotId, "Saved bot", FakeScheduleQueryService.ServerId, "Test server");
        viewModel.CreatedFrom = new DateTime(2026, 3, 2);
        viewModel.CreatedTo = new DateTime(2026, 3, 1);

        await viewModel.LoadAsync(CancellationToken.None);
        Assert.Equal(0, service.QueryCount);
        Assert.False(viewModel.HasValidDateRange);
        Assert.Contains("start date", viewModel.QueryError!, StringComparison.OrdinalIgnoreCase);

        viewModel.CreatedFrom = null; viewModel.CreatedTo = null;
        await viewModel.LoadAsync(CancellationToken.None);
        viewModel.SelectedSchedule = Assert.Single(viewModel.Schedules);
        await service.DetailLoaded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.OccurrencesLoaded.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var occurrence = Assert.Single(viewModel.RecentOccurrences);
        Assert.Equal(10, service.LastOccurrenceLimit);
        Assert.Equal("Delivered", occurrence.StateLabel);
        Assert.DoesNotContain("message", string.Join(' ', occurrence.StateLabel, occurrence.ResultLabel, occurrence.CompatibilityLabel), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FriendlyPresentationNeverUsesScheduledEnumIdentifiers()
    {
        Assert.Equal("Needs attention", ScheduledMessagePresentation.LifecycleLabel(ScheduledMessageLifecycle.Faulted));
        Assert.Equal("One time", ScheduledMessagePresentation.RecurrenceLabel(ScheduledMessageRecurrence.Once));
        Assert.Equal("Outcome uncertain", ScheduledMessagePresentation.OccurrenceStateLabel(MessageOperationState.Uncertain));
        Assert.DoesNotContain("Faulted", ScheduledMessagePresentation.LifecycleLabel(ScheduledMessageLifecycle.Faulted), StringComparison.Ordinal);
    }

    private sealed class FakeScheduleQueryService : IScheduledMessageQueryService
    {
        public static readonly Guid BotId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        public const ulong ServerId = 777;
        private static readonly Guid TemplateId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        private static readonly Guid ScheduleId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        public int QueryCount { get; private set; }
        public ScheduledMessageQuery? LastQuery { get; private set; }
        public int LastOccurrenceLimit { get; private set; }
        public TaskCompletionSource DetailLoaded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource OccurrencesLoaded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ScheduledMessageFilterOptions> GetFilterOptionsAsync(Guid botProfileId, ulong serverId, CancellationToken cancellationToken) => Task.FromResult(new ScheduledMessageFilterOptions([new(TemplateId, "Saved template")], [new(12UL, "#scheduled")]));
        public Task<ScheduledMessagePage> QueryAsync(ScheduledMessageQuery query, CancellationToken cancellationToken)
        {
            QueryCount++; LastQuery = query;
            var item = new ScheduledMessageListItem(ScheduleId, "Morning briefing", ScheduledMessageLifecycle.Enabled, "Saved bot", "Test server", "#scheduled", "Saved template", ScheduledMessageRecurrence.Daily, "UTC", DateTimeOffset.UtcNow.AddDays(1), null, null, MissedOccurrencePolicy.RequireManualApproval, false, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            return Task.FromResult(new ScheduledMessagePage([item], 1, query.PageNumber, query.PageSize, DateTimeOffset.UtcNow));
        }
        public Task<ScheduledMessageDetail?> GetDetailAsync(Guid botProfileId, ulong serverId, Guid scheduleId, CancellationToken cancellationToken)
        {
            DetailLoaded.TrySetResult();
            var definition = new ScheduledMessageDefinition(ScheduleId, BotId, MessageDestination.Channel(ServerId, "Test server", 12, "scheduled"), TemplateId, null, ScheduledMessageRecurrence.Daily, new TimeOnly(9, 0), "UTC", ImmutableArray<DayOfWeek>.Empty, DateTimeOffset.UtcNow, null, true, MissedOccurrencePolicy.RequireManualApproval, 0, null, null) { Name = "Morning briefing" };
            return Task.FromResult<ScheduledMessageDetail?>(new(ScheduleId, definition.Name, ScheduledMessageLifecycle.Enabled, "Eligible for future occurrence reservation.", "Every day at 09:00", "UTC", null, definition));
        }
        public Task<ScheduledMessageOccurrencePage> GetRecentOccurrencesAsync(Guid botProfileId, ulong serverId, Guid scheduleId, int limit, CancellationToken cancellationToken)
        {
            LastOccurrenceLimit = limit; OccurrencesLoaded.TrySetResult();
            return Task.FromResult(new ScheduledMessageOccurrencePage([new(Guid.NewGuid(), 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, MessageOperationState.Delivered, null, "Approved", Guid.NewGuid(), SnapshotCompatibility.Supported)], limit));
        }
    }
}
