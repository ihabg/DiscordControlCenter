using System.Collections.Immutable;
using DiscordControlCenter.App.Services;
using DiscordControlCenter.App.ViewModels;
using DiscordControlCenter.Application.Messaging;
using DiscordControlCenter.Core.Messaging;

namespace DiscordControlCenter.App.Tests;

public sealed class ScheduledDraftEditorTests
{
    private static readonly Guid BotOne = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid BotTwo = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task CurrentScopeLoadsOnlyItsFriendlyTemplateNamesAndStaleResultCannotReplaceIt()
    {
        var drafts = new FakeDraftService();
        using var viewModel = new ScheduledMessagesViewModel(new EmptyQueryService(), drafts, new KeepEditingConfirmation());
        viewModel.SetContext(BotOne, "One", 1, "Server one");
        await WaitUntilAsync(() => drafts.TemplateRequests.Count == 1);
        viewModel.SetContext(BotTwo, "Two", 2, "Server two");
        await WaitUntilAsync(() => drafts.TemplateRequests.Count == 2);

        drafts.TemplateRequests[1].Completion.SetResult([new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), "Two template")]);
        await WaitUntilAsync(() => viewModel.DraftTemplates.Count == 1);
        drafts.TemplateRequests[0].Completion.SetResult([new(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), "One template")]);
        await Task.Delay(25);

        var option = Assert.Single(viewModel.DraftTemplates);
        Assert.Equal("Two template", option.Label);
        Assert.DoesNotContain("message", option.Label, StringComparison.OrdinalIgnoreCase);
        Assert.False(viewModel.IsDraftDirty);
    }

    [Fact]
    public async Task PersistedMissingTemplateIsRepresentedAndSaveIsBlockedByDraftService()
    {
        var drafts = new FakeDraftService { Loaded = FakeDraftService.Create(BotOne, 1, Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc")) with { TemplateId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"), InlineContent = null } };
        using var viewModel = new ScheduledMessagesViewModel(new QueryWithDraft(), drafts, new KeepEditingConfirmation());
        viewModel.SetContext(BotOne, "One", 1, "Server one");
        await WaitUntilAsync(() => drafts.TemplateRequests.Count == 1);
        drafts.TemplateRequests[0].Completion.SetResult([]);
        await WaitUntilAsync(() => !viewModel.IsTemplateLoading);
        viewModel.SelectedSchedule = new ScheduledMessageListItem(drafts.Loaded.Id, "Draft", ScheduledMessageLifecycle.Draft, "One", "Server one", "#one", "", ScheduledMessageRecurrence.Daily, "UTC", null, null, null, MissedOccurrencePolicy.RequireManualApproval, false, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        viewModel.EditDraftCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.HasMissingDraftTemplate);

        Assert.Equal("Deleted or unavailable template", viewModel.SelectedDraftTemplate!.Label);
        viewModel.SaveDraftCommand.Execute(null);
        await WaitUntilAsync(() => drafts.SaveCalls == 1);
        Assert.Equal("The selected template is unavailable for this bot and server.", viewModel.DraftMessage);
    }

    [Fact]
    public async Task PersistedEditorChangesMarkDirtyAndSuccessfulSaveEstablishesNewBaseline()
    {
        var drafts = new FakeDraftService();
        using var viewModel = new ScheduledMessagesViewModel(new EmptyQueryService(), drafts, new KeepEditingConfirmation());
        viewModel.SetContext(BotOne, "One", 1, "Server one");
        await WaitUntilAsync(() => drafts.TemplateRequests.Count == 1);
        drafts.TemplateRequests[0].Completion.SetResult([]);
        viewModel.NewDraftCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Draft is not null);
        Assert.False(viewModel.IsDraftDirty);

        viewModel.DraftName = "Changed";
        Assert.True(viewModel.IsDraftDirty);
        viewModel.SaveDraftCommand.Execute(null);
        await WaitUntilAsync(() => drafts.SaveCalls == 1);
        Assert.False(viewModel.IsDraftDirty);
    }

    [Fact]
    public async Task KeepEditingBlocksNewDraftAndSelectionUntilExplicitDiscard()
    {
        var confirmation = new KeepEditingConfirmation();
        var drafts = new FakeDraftService();
        using var viewModel = new ScheduledMessagesViewModel(new EmptyQueryService(), drafts, confirmation);
        viewModel.SetContext(BotOne, "One", 1, "Server one");
        await WaitUntilAsync(() => drafts.TemplateRequests.Count == 1);
        drafts.TemplateRequests[0].Completion.SetResult([]);
        viewModel.NewDraftCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Draft is not null);
        viewModel.DraftName = "Unsaved";
        var before = viewModel.Draft!.Id;
        viewModel.NewDraftCommand.Execute(null);
        await Task.Delay(25);
        Assert.Equal(before, viewModel.Draft!.Id);
        Assert.Equal(1, confirmation.Calls);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++) await Task.Delay(10);
        Assert.True(condition());
    }

    private sealed class KeepEditingConfirmation : IDraftDiscardConfirmationService { public int Calls { get; private set; } public bool ConfirmDiscard(string actionDescription) { Calls++; return false; } }
    private class EmptyQueryService : IScheduledMessageQueryService
    {
        public Task<ScheduledMessagePage> QueryAsync(ScheduledMessageQuery query, CancellationToken cancellationToken) => Task.FromResult(new ScheduledMessagePage([], 0, 1, 25, DateTimeOffset.UtcNow));
        public Task<ScheduledMessageFilterOptions> GetFilterOptionsAsync(Guid botProfileId, ulong serverId, CancellationToken cancellationToken) => Task.FromResult(new ScheduledMessageFilterOptions([], []));
        public Task<ScheduledMessageDetail?> GetDetailAsync(Guid botProfileId, ulong serverId, Guid scheduleId, CancellationToken cancellationToken) => Task.FromResult<ScheduledMessageDetail?>(null);
        public Task<ScheduledMessageOccurrencePage> GetRecentOccurrencesAsync(Guid botProfileId, ulong serverId, Guid scheduleId, int limit, CancellationToken cancellationToken) => Task.FromResult(new ScheduledMessageOccurrencePage([], limit));
    }
    private sealed class QueryWithDraft : EmptyQueryService { }
    private sealed class FakeDraftService : IScheduledMessageDraftService
    {
        public List<(Guid Bot, ulong Server, TaskCompletionSource<IReadOnlyList<ScheduledDraftTemplateOption>> Completion)> TemplateRequests { get; } = [];
        public ScheduledMessageDefinition? Loaded { get; set; }
        public int SaveCalls { get; private set; }
        public ScheduledMessageDefinition CreateDraft(Guid botProfileId, MessageDestination destination) => Create(botProfileId, destination.ServerId, Guid.NewGuid());
        public Task<IReadOnlyList<ScheduledDraftTemplateOption>> GetTemplateOptionsAsync(Guid botProfileId, ulong serverId, CancellationToken cancellationToken) { var completion = new TaskCompletionSource<IReadOnlyList<ScheduledDraftTemplateOption>>(TaskCreationOptions.RunContinuationsAsynchronously); TemplateRequests.Add((botProfileId, serverId, completion)); return completion.Task; }
        public Task<ScheduledMessageDefinition?> LoadAsync(Guid botProfileId, ulong serverId, Guid scheduleId, CancellationToken cancellationToken) => Task.FromResult(Loaded);
        public Task<ScheduledDraftValidation> ValidateAsync(ScheduledMessageDefinition definition, CancellationToken cancellationToken) => Task.FromResult(new ScheduledDraftValidation([], [], "Valid", []));
        public Task<ScheduledDraftSaveResult> SaveAsync(ScheduledMessageDefinition definition, int expectedRevision, CancellationToken cancellationToken) { SaveCalls++; var validation = definition.TemplateId is null || definition.TemplateId == Guid.Empty ? new ScheduledDraftValidation([], [], "Valid", []) : new ScheduledDraftValidation(["The selected template is unavailable for this bot and server."], [], "", []); return Task.FromResult(validation.IsValid ? new ScheduledDraftSaveResult(true, false, definition with { Revision = expectedRevision + 1 }, validation, "Saved") : new ScheduledDraftSaveResult(false, false, null, validation, validation.Errors[0])); }
        public static ScheduledMessageDefinition Create(Guid bot, ulong server, Guid id) => new(id, bot, MessageDestination.Channel(server, "Server", 1, "one"), null, new MessageContent("Body", null, AllowedMentionPolicy.None), ScheduledMessageRecurrence.Daily, new TimeOnly(9, 0), "UTC", ImmutableArray<DayOfWeek>.Empty, DateTimeOffset.UtcNow, null, false, MissedOccurrencePolicy.RequireManualApproval, 0, null, null) { Name = "Draft", SavedLifecycle = ScheduledMessageLifecycle.Draft, Revision = 1 };
    }
}
