using System.Collections.ObjectModel;
using DiscordControlCenter.App.Mvvm;
using DiscordControlCenter.Application.Messaging;
using DiscordControlCenter.Core.Messaging;

namespace DiscordControlCenter.App.ViewModels;

public sealed record ScheduledChoice<T>(string Label, T? Value, bool IsAny = false)
{
    public override string ToString() => Label;
}

/// <summary>
/// Read-only schedule browsing state. Query, detail, and occurrence generations are separate so
/// a slow result can never replace a newer scope, filter, or selected schedule.
/// </summary>
public sealed class ScheduledMessagesViewModel : ObservableObject, IDisposable
{
    private const int RecentOccurrenceLimit = 10;
    private readonly IScheduledMessageQueryService service;
    private readonly IScheduledMessageDraftService? drafts;
    private ScheduledMessageDefinition? _draft;
    private int _draftRevision;
    private bool _draftSaving;
    private string? _draftMessage;
    private ScheduledChoice<ulong>? _draftDestination;
    private bool _draftConflict;
    private Guid? _botId;
    private ulong? _serverId;
    private string _botName = "No bot selected";
    private string _serverName = "No server selected";
    private string _search = string.Empty;
    private ScheduledChoice<ScheduledMessageLifecycle>? _lifecycle;
    private ScheduledChoice<ScheduledMessageRecurrence>? _recurrence;
    private ScheduledChoice<Guid>? _template;
    private ScheduledChoice<ulong>? _destination;
    private ScheduledChoice<MissedOccurrencePolicy>? _missedPolicy;
    private ScheduledChoice<bool>? _warning;
    private DateTime? _createdFrom;
    private DateTime? _createdTo;
    private DateTime? _nextRunFrom;
    private DateTime? _nextRunTo;
    private ScheduledChoice<ScheduledMessageSort>? _sort;
    private int _pageSize = 25;
    private int _page = 1;
    private int _total;
    private bool _loading;
    private bool _refreshing;
    private string? _error;
    private ScheduledMessageListItem? _selected;
    private ScheduledMessageDetail? _detail;
    private bool _detailLoading;
    private string? _detailError;
    private bool _occurrencesLoading;
    private string? _occurrenceError;
    private int _queryVersion;
    private int _detailVersion;
    private int _occurrenceVersion;
    private CancellationTokenSource? _detailCancellation;
    private CancellationTokenSource? _occurrenceCancellation;

    public ObservableCollection<ScheduledMessageListItem> Schedules { get; } = [];
    public ObservableCollection<ScheduledMessageOccurrenceListItem> RecentOccurrences { get; } = [];
    public ObservableCollection<ScheduledChoice<Guid>> Templates { get; } = [new("Any template", default, true)];
    public ObservableCollection<ScheduledChoice<ulong>> Destinations { get; } = [new("Any destination", default, true)];
    public IReadOnlyList<ScheduledChoice<ScheduledMessageLifecycle>> Lifecycles { get; } =
    [new("Any lifecycle", default, true), new("Draft", ScheduledMessageLifecycle.Draft), new("Enabled", ScheduledMessageLifecycle.Enabled), new("Disabled", ScheduledMessageLifecycle.Disabled), new("Needs attention", ScheduledMessageLifecycle.Faulted), new("Expired", ScheduledMessageLifecycle.Expired), new("Archived", ScheduledMessageLifecycle.Archived)];
    public IReadOnlyList<ScheduledChoice<ScheduledMessageRecurrence>> Recurrences { get; } =
    [new("Any recurrence", default, true), new("One time", ScheduledMessageRecurrence.Once), new("Daily", ScheduledMessageRecurrence.Daily), new("Weekly", ScheduledMessageRecurrence.Weekly)];
    public IReadOnlyList<ScheduledChoice<MissedOccurrencePolicy>> MissedPolicies { get; } =
    [new("Any missed policy", default, true), new("Require manual approval", MissedOccurrencePolicy.RequireManualApproval), new("Skip missed occurrences", MissedOccurrencePolicy.Skip), new("Send latest occurrence only", MissedOccurrencePolicy.SendLatestOnly)];
    public IReadOnlyList<ScheduledChoice<bool>> WarningStates { get; } = [new("Any warning state", default, true), new("Has a fault or warning", true), new("No saved warning", false)];
    public IReadOnlyList<ScheduledChoice<ScheduledMessageSort>> Sorts { get; } =
    [new("Next run — earliest first", ScheduledMessageSort.NextRunAscending), new("Next run — latest first", ScheduledMessageSort.NextRunDescending), new("Name", ScheduledMessageSort.Name), new("Created — newest first", ScheduledMessageSort.CreatedNewest), new("Created — oldest first", ScheduledMessageSort.CreatedOldest), new("Updated — newest first", ScheduledMessageSort.UpdatedNewest), new("Lifecycle", ScheduledMessageSort.State), new("Last result", ScheduledMessageSort.LastResult)];
    public IReadOnlyList<int> PageSizes { get; } = [10, 25, 50, 100, 200];
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ClearFiltersCommand { get; }
    public AsyncRelayCommand FirstPageCommand { get; }
    public AsyncRelayCommand PreviousPageCommand { get; }
    public AsyncRelayCommand NextPageCommand { get; }
    public AsyncRelayCommand LastPageCommand { get; }
    public RelayCommand NavigateMessagesSectionCommand { get; }
    public AsyncRelayCommand NewDraftCommand { get; }
    public AsyncRelayCommand SaveDraftCommand { get; }
    public AsyncRelayCommand ValidateDraftCommand { get; }
    public AsyncRelayCommand EditDraftCommand { get; }
    public AsyncRelayCommand ReloadDraftCommand { get; }
    public RelayCommand CancelDraftCommand { get; }
    public event EventHandler<string>? MessagesSectionRequested;

    public ScheduledMessagesViewModel(IScheduledMessageQueryService service, IScheduledMessageDraftService? drafts = null)
    {
        this.service = service;
        this.drafts = drafts;
        _lifecycle = Lifecycles[0]; _recurrence = Recurrences[0]; _missedPolicy = MissedPolicies[0]; _warning = WarningStates[0]; _sort = Sorts[0];
        RefreshCommand = new AsyncRelayCommand(LoadAsync, CanQuery);
        ClearFiltersCommand = new AsyncRelayCommand(ClearFiltersAsync, () => !IsLoading);
        FirstPageCommand = new AsyncRelayCommand(token => GoToPageAsync(1, token), () => CanQuery() && PageNumber > 1);
        PreviousPageCommand = new AsyncRelayCommand(token => GoToPageAsync(PageNumber - 1, token), () => CanQuery() && PageNumber > 1);
        NextPageCommand = new AsyncRelayCommand(token => GoToPageAsync(PageNumber + 1, token), () => CanQuery() && PageNumber < TotalPages);
        LastPageCommand = new AsyncRelayCommand(token => GoToPageAsync(TotalPages, token), () => CanQuery() && PageNumber < TotalPages);
        NavigateMessagesSectionCommand = new RelayCommand(section => MessagesSectionRequested?.Invoke(this, section as string ?? "Compose"));
        NewDraftCommand = new AsyncRelayCommand(NewDraftAsync, () => CanEditDraft);
        SaveDraftCommand = new AsyncRelayCommand(SaveDraftAsync, () => CanEditDraft && Draft is not null && !IsDraftSaving);
        ValidateDraftCommand = new AsyncRelayCommand(ValidateDraftAsync, () => CanEditDraft && Draft is not null && !IsDraftSaving);
        EditDraftCommand = new AsyncRelayCommand(EditDraftAsync, () => CanEditDraft && SelectedSchedule?.Lifecycle == ScheduledMessageLifecycle.Draft && !IsDraftSaving);
        ReloadDraftCommand = new AsyncRelayCommand(ReloadDraftAsync, () => CanEditDraft && Draft is not null && !IsDraftSaving);
        CancelDraftCommand = new RelayCommand(_ => { Draft = null; DraftMessage = "Draft editor closed without saving."; DraftConflict = false; });
    }

    public string Search { get => _search; set { if (SetProperty(ref _search, value)) ResetPage(); } }
    public ScheduledChoice<ScheduledMessageLifecycle>? SelectedLifecycle { get => _lifecycle; set { if (SetProperty(ref _lifecycle, value)) ResetPage(); } }
    public ScheduledChoice<ScheduledMessageRecurrence>? SelectedRecurrence { get => _recurrence; set { if (SetProperty(ref _recurrence, value)) ResetPage(); } }
    public ScheduledChoice<Guid>? SelectedTemplate { get => _template; set { if (SetProperty(ref _template, value)) ResetPage(); } }
    public ScheduledChoice<ulong>? SelectedDestination { get => _destination; set { if (SetProperty(ref _destination, value)) ResetPage(); } }
    public ScheduledChoice<MissedOccurrencePolicy>? SelectedMissedPolicy { get => _missedPolicy; set { if (SetProperty(ref _missedPolicy, value)) ResetPage(); } }
    public ScheduledChoice<bool>? SelectedWarningState { get => _warning; set { if (SetProperty(ref _warning, value)) ResetPage(); } }
    public DateTime? CreatedFrom { get => _createdFrom; set { if (SetProperty(ref _createdFrom, value)) ResetPage(); } }
    public DateTime? CreatedTo { get => _createdTo; set { if (SetProperty(ref _createdTo, value)) ResetPage(); } }
    public DateTime? NextRunFrom { get => _nextRunFrom; set { if (SetProperty(ref _nextRunFrom, value)) ResetPage(); } }
    public DateTime? NextRunTo { get => _nextRunTo; set { if (SetProperty(ref _nextRunTo, value)) ResetPage(); } }
    public ScheduledChoice<ScheduledMessageSort>? SelectedSort { get => _sort; set { if (SetProperty(ref _sort, value)) ResetPage(); } }
    public int PageSize { get => _pageSize; set { if (SetProperty(ref _pageSize, Math.Clamp(value, 1, 200))) ResetPage(); } }
    public int PageNumber => _page;
    public int TotalCount => _total;
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(_total / (double)_pageSize));
    public bool IsLoading { get => _loading; private set { if (SetProperty(ref _loading, value)) NotifyCommands(); } }
    public bool IsRefreshing { get => _refreshing; private set => SetProperty(ref _refreshing, value); }
    public string? QueryError { get => _error; private set => SetProperty(ref _error, value); }
    public bool HasValidDateRange => !((CreatedFrom is { } createdFrom && CreatedTo is { } createdTo && createdFrom > createdTo) || (NextRunFrom is { } nextFrom && NextRunTo is { } nextTo && nextFrom > nextTo));
    public string DateRangeMessage => HasValidDateRange ? string.Empty : "The start date must be on or before the end date. Correct the range, then refresh.";
    public string ScopeSummary => _botId is null || _serverId is null ? "Select a connected bot and server in the application toolbar to view scheduled messages." : $"Current schedule scope — Bot: {_botName}; Server: {_serverName}.";
    public string ActiveFilterSummary => BuildFilterSummary();
    public string ScheduleStateMessage => _botId is null || _serverId is null ? ScopeSummary : IsLoading ? "Loading scheduled messages…" : Schedules.Count == 0 ? (HasActiveFilters ? "No scheduled messages match these filters. Clear filters or adjust the scope." : "No schedules exist in this bot and server scope yet.") : string.Empty;
    public bool HasActiveFilters => !string.IsNullOrWhiteSpace(Search) || SelectedLifecycle?.IsAny != true || SelectedRecurrence?.IsAny != true || SelectedTemplate?.IsAny != true || SelectedDestination?.IsAny != true || SelectedMissedPolicy?.IsAny != true || SelectedWarningState?.IsAny != true || CreatedFrom is not null || CreatedTo is not null || NextRunFrom is not null || NextRunTo is not null || SelectedSort?.Value != ScheduledMessageSort.NextRunAscending || PageSize != 25;
    public ScheduledMessageListItem? SelectedSchedule { get => _selected; set { if (SetProperty(ref _selected, value)) _ = LoadDetailAsync(value); } }
    public ScheduledMessageDetail? Detail { get => _detail; private set => SetProperty(ref _detail, value); }
    public bool IsDetailLoading { get => _detailLoading; private set => SetProperty(ref _detailLoading, value); }
    public string? DetailError { get => _detailError; private set => SetProperty(ref _detailError, value); }
    public bool IsOccurrencesLoading { get => _occurrencesLoading; private set => SetProperty(ref _occurrencesLoading, value); }
    public string? OccurrenceError { get => _occurrenceError; private set => SetProperty(ref _occurrenceError, value); }
    public string OccurrenceStateMessage => IsOccurrencesLoading ? "Loading the latest 10 safe occurrence records…" : RecentOccurrences.Count == 0 && SelectedSchedule is not null && OccurrenceError is null ? "No recent occurrences are available for this schedule." : string.Empty;

    public ScheduledMessageDefinition? Draft { get => _draft; private set { if (SetProperty(ref _draft, value)) { OnPropertyChanged(nameof(IsEditingDraft)); SaveDraftCommand.NotifyCanExecuteChanged(); ValidateDraftCommand.NotifyCanExecuteChanged(); } } }
    public bool IsEditingDraft => Draft is not null;
    public bool IsDraftSaving { get => _draftSaving; private set { if (SetProperty(ref _draftSaving, value)) SaveDraftCommand.NotifyCanExecuteChanged(); } }
    public string? DraftMessage { get => _draftMessage; private set => SetProperty(ref _draftMessage, value); }
    public bool CanEditDraft => drafts is not null && _botId is not null && _serverId is not null;
    public bool DraftConflict { get => _draftConflict; private set { if (SetProperty(ref _draftConflict, value)) { SaveDraftCommand.NotifyCanExecuteChanged(); ReloadDraftCommand.NotifyCanExecuteChanged(); } } }
    public string DraftName { get => Draft?.Name ?? string.Empty; set { if (Draft is not null) Draft = Draft with { Name = value }; } }
    public string DraftBody { get => Draft?.InlineContent?.Body ?? string.Empty; set { if (Draft is not null) Draft = Draft with { InlineContent = new MessageContent(value, null, AllowedMentionPolicy.None), TemplateId = null }; } }
    public string DraftTimeZone { get => Draft?.TimeZoneId ?? string.Empty; set { if (Draft is not null) Draft = Draft with { TimeZoneId = value }; } }
    public ScheduledChoice<ScheduledMessageRecurrence>? SelectedDraftRecurrence { get; set; }
    public ScheduledChoice<ulong>? SelectedDraftDestination { get => _draftDestination; set { if (SetProperty(ref _draftDestination, value) && Draft is not null && value is { IsAny: false, Value: ulong channel }) Draft = Draft with { Destination = MessageDestination.Channel(_serverId ?? 0, _serverName, channel, value.Label) }; } }

    public void SetContext(Guid? botId, string? botName, ulong? serverId, string? serverName)
    {
        RefreshCommand.Cancel(); _botId = botId; _serverId = serverId; _botName = botName ?? "No bot selected"; _serverName = serverName ?? "No server selected";
        InvalidateSelection(); ResetPage(); QueryError = null; Schedules.Clear(); _total = 0;
        OnPropertyChanged(nameof(ScopeSummary)); OnPropertyChanged(nameof(ScheduleStateMessage)); OnPropertyChanged(nameof(TotalCount)); OnPropertyChanged(nameof(TotalPages));
        NotifyCommands();
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (!CanQuery()) { QueryError = HasValidDateRange ? null : DateRangeMessage; OnPropertyChanged(nameof(ScheduleStateMessage)); return; }
        if (!HasValidDateRange) { QueryError = DateRangeMessage; return; }
        var version = ++_queryVersion; IsLoading = true; IsRefreshing = Schedules.Count > 0; QueryError = null; OnPropertyChanged(nameof(ScheduleStateMessage));
        try
        {
            var options = await service.GetFilterOptionsAsync(_botId!.Value, _serverId!.Value, cancellationToken).ConfigureAwait(false);
            var result = await service.QueryAsync(BuildQuery(), cancellationToken).ConfigureAwait(false);
            if (version != _queryVersion) return;
            UpdateFilterOptions(options); _page = Math.Min(result.TotalPages, result.PageNumber); _total = result.TotalCount;
            Schedules.Clear(); foreach (var item in result.Items) Schedules.Add(item);
            if (SelectedSchedule is not null && !Schedules.Any(item => item.Id == SelectedSchedule.Id)) { DetailError = "The selected schedule is no longer in this result. Select another schedule."; SelectedSchedule = null; }
            OnPropertyChanged(nameof(PageNumber)); OnPropertyChanged(nameof(TotalCount)); OnPropertyChanged(nameof(TotalPages)); OnPropertyChanged(nameof(ScheduleStateMessage));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch { if (version == _queryVersion) { QueryError = "Scheduled messages could not be loaded. Check the current scope and try again."; } }
        finally { if (version == _queryVersion) { IsLoading = false; IsRefreshing = false; OnPropertyChanged(nameof(ScheduleStateMessage)); } }
    }

    private async Task ClearFiltersAsync(CancellationToken cancellationToken)
    {
        Search = string.Empty; SelectedLifecycle = Lifecycles[0]; SelectedRecurrence = Recurrences[0]; SelectedTemplate = Templates[0]; SelectedDestination = Destinations[0]; SelectedMissedPolicy = MissedPolicies[0]; SelectedWarningState = WarningStates[0]; CreatedFrom = null; CreatedTo = null; NextRunFrom = null; NextRunTo = null; SelectedSort = Sorts[0]; PageSize = 25;
        await LoadAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task GoToPageAsync(int page, CancellationToken cancellationToken)
    {
        _page = Math.Clamp(page, 1, TotalPages); OnPropertyChanged(nameof(PageNumber)); await LoadAsync(cancellationToken).ConfigureAwait(false);
    }

    private ScheduledMessageQuery BuildQuery() => new(_botId!.Value, _serverId!.Value, Search, Optional(SelectedLifecycle), Optional(SelectedRecurrence), Optional(SelectedTemplate), Optional(SelectedDestination), Optional(SelectedMissedPolicy), Optional(SelectedWarningState), ToOffset(CreatedFrom, false), ToOffset(CreatedTo, true), ToOffset(NextRunFrom, false), ToOffset(NextRunTo, true), SelectedSort?.Value ?? ScheduledMessageSort.NextRunAscending, _page, PageSize);
    private static T? Optional<T>(ScheduledChoice<T>? choice) => choice is null || choice.IsAny ? default : choice.Value;
    private static DateTimeOffset? ToOffset(DateTime? date, bool endOfDay) => date is null ? null : new DateTimeOffset(endOfDay ? date.Value.Date.AddDays(1).AddTicks(-1) : date.Value.Date, TimeZoneInfo.Local.GetUtcOffset(date.Value));

    private async Task LoadDetailAsync(ScheduledMessageListItem? item)
    {
        var version = ++_detailVersion; Cancel(ref _detailCancellation); Cancel(ref _occurrenceCancellation); ++_occurrenceVersion; Detail = null; DetailError = null; RecentOccurrences.Clear(); OccurrenceError = null;
        if (item is null || _botId is not Guid bot || _serverId is not ulong server) { IsDetailLoading = false; IsOccurrencesLoading = false; return; }
        _detailCancellation = new CancellationTokenSource(); IsDetailLoading = true;
        try
        {
            var detail = await service.GetDetailAsync(bot, server, item.Id, _detailCancellation.Token).ConfigureAwait(false);
            if (version != _detailVersion) return;
            if (detail is null) { DetailError = "This schedule is no longer available in the selected scope. Refresh the list and choose another schedule."; return; }
            Detail = detail;
            await LoadOccurrencesAsync(item.Id, bot, server).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch { if (version == _detailVersion) DetailError = "Schedule details could not be loaded. Refresh the list and try again."; }
        finally { if (version == _detailVersion) IsDetailLoading = false; }
    }

    private async Task LoadOccurrencesAsync(Guid scheduleId, Guid botId, ulong serverId)
    {
        var version = ++_occurrenceVersion; Cancel(ref _occurrenceCancellation); _occurrenceCancellation = new CancellationTokenSource(); IsOccurrencesLoading = true; OccurrenceError = null; RecentOccurrences.Clear();
        try
        {
            var result = await service.GetRecentOccurrencesAsync(botId, serverId, scheduleId, RecentOccurrenceLimit, _occurrenceCancellation.Token).ConfigureAwait(false);
            if (version != _occurrenceVersion || SelectedSchedule?.Id != scheduleId) return;
            foreach (var item in result.Items) RecentOccurrences.Add(item);
        }
        catch (OperationCanceledException) { }
        catch { if (version == _occurrenceVersion) OccurrenceError = "Recent occurrences could not be loaded. Select the schedule again or refresh the list."; }
        finally { if (version == _occurrenceVersion) { IsOccurrencesLoading = false; OnPropertyChanged(nameof(OccurrenceStateMessage)); } }
    }

    private void UpdateFilterOptions(ScheduledMessageFilterOptions options)
    {
        UpdateOptions(Templates, options.Templates); UpdateOptions(Destinations, options.Destinations);
    }
    private static void UpdateOptions<T>(ObservableCollection<ScheduledChoice<T>> target, IReadOnlyList<ScheduledMessageFilterOption<T>> options)
    {
        var selected = target.FirstOrDefault(item => item.IsAny) ?? new ScheduledChoice<T>("Any", default, true);
        target.Clear(); target.Add(selected); foreach (var option in options) target.Add(new(option.Label, option.Value));
    }
    private void InvalidateSelection() { ++_detailVersion; ++_occurrenceVersion; Cancel(ref _detailCancellation); Cancel(ref _occurrenceCancellation); SelectedSchedule = null; Detail = null; DetailError = null; RecentOccurrences.Clear(); OccurrenceError = null; }
    private static void Cancel(ref CancellationTokenSource? cancellation) { try { cancellation?.Cancel(); } catch (ObjectDisposedException) { } finally { cancellation?.Dispose(); cancellation = null; } }
    private void ResetPage() { ++_queryVersion; RefreshCommand?.Cancel(); _page = 1; OnPropertyChanged(nameof(PageNumber)); OnPropertyChanged(nameof(HasValidDateRange)); OnPropertyChanged(nameof(DateRangeMessage)); OnPropertyChanged(nameof(ActiveFilterSummary)); OnPropertyChanged(nameof(ScheduleStateMessage)); NotifyCommands(); }
    private bool CanQuery() => _botId is not null && _serverId is not null && !IsLoading;
    private string BuildFilterSummary()
    {
        var values = new List<string>(); if (!string.IsNullOrWhiteSpace(Search)) values.Add($"Search: {Search.Trim()}"); Add(SelectedLifecycle); Add(SelectedRecurrence); Add(SelectedTemplate); Add(SelectedDestination); Add(SelectedMissedPolicy); Add(SelectedWarningState); if (CreatedFrom is not null) values.Add($"Created from {CreatedFrom:dd MMM yyyy}"); if (CreatedTo is not null) values.Add($"Created to {CreatedTo:dd MMM yyyy}"); if (NextRunFrom is not null) values.Add($"Next run from {NextRunFrom:dd MMM yyyy}"); if (NextRunTo is not null) values.Add($"Next run to {NextRunTo:dd MMM yyyy}"); if (SelectedSort?.Value != ScheduledMessageSort.NextRunAscending) values.Add($"Sort: {SelectedSort?.Label}"); if (PageSize != 25) values.Add($"Page size: {PageSize}"); return values.Count == 0 ? "No additional filters. Results are scoped to the selected bot and server." : string.Join(" • ", values);
        void Add<T>(ScheduledChoice<T>? value) { if (value is { IsAny: false }) values.Add(value.Label); }
    }
    private void NotifyCommands() { RefreshCommand?.NotifyCanExecuteChanged(); ClearFiltersCommand?.NotifyCanExecuteChanged(); FirstPageCommand?.NotifyCanExecuteChanged(); PreviousPageCommand?.NotifyCanExecuteChanged(); NextPageCommand?.NotifyCanExecuteChanged(); LastPageCommand?.NotifyCanExecuteChanged(); }

    private Task NewDraftAsync(CancellationToken token)
    {
        if (drafts is null || _botId is not Guid bot || _serverId is not ulong server) return Task.CompletedTask;
        Draft = drafts.CreateDraft(bot, MessageDestination.Channel(server, _serverName, 0, "Select a destination")); SelectedDraftRecurrence = Recurrences.Single(item => item.Value == Draft.Recurrence); _draftRevision = 0; DraftMessage = "New draft. Select a destination and message source, then validate."; return Task.CompletedTask;
    }
    private async Task ValidateDraftAsync(CancellationToken token)
    {
        if (drafts is null || Draft is null) return; var result = await drafts.ValidateAsync(Draft, token).ConfigureAwait(false); DraftMessage = result.IsValid ? result.RecurrenceSummary + " " + string.Join(" ", result.Warnings) : string.Join(" ", result.Errors);
    }
    private async Task SaveDraftAsync(CancellationToken token)
    {
        if (drafts is null || Draft is null || DraftConflict) return; IsDraftSaving = true; try { var result = await drafts.SaveAsync(Draft, _draftRevision, token).ConfigureAwait(false); DraftMessage = result.Message; DraftConflict = result.Conflict; if (result.Saved && result.Definition is not null) { Draft = result.Definition; _draftRevision = result.Definition.Revision; await LoadAsync(token).ConfigureAwait(false); SelectedSchedule = Schedules.FirstOrDefault(item => item.Id == result.Definition.Id); } } finally { IsDraftSaving = false; }
    }
    private async Task EditDraftAsync(CancellationToken token)
    {
        if (drafts is null || SelectedSchedule is null || _botId is not Guid bot || _serverId is not ulong server) return;
        var loaded = await drafts.LoadAsync(bot, server, SelectedSchedule.Id, token).ConfigureAwait(false); if (loaded is null || loaded.SavedLifecycle != ScheduledMessageLifecycle.Draft) { DraftMessage = "Only an available Draft schedule can be edited."; return; }
        Draft = loaded; SelectedDraftRecurrence = Recurrences.Single(item => item.Value == loaded.Recurrence); _draftRevision = loaded.Revision; DraftConflict = false; DraftMessage = $"Editing Draft revision {loaded.Revision}.";
    }
    private async Task ReloadDraftAsync(CancellationToken token)
    {
        if (drafts is null || Draft is null || _botId is not Guid bot || _serverId is not ulong server) return;
        var loaded = await drafts.LoadAsync(bot, server, Draft.Id, token).ConfigureAwait(false); if (loaded is null) { DraftMessage = "The latest Draft is unavailable."; return; }
        Draft = loaded; _draftRevision = loaded.Revision; DraftConflict = false; DraftMessage = "Reloaded the latest Draft. Earlier unsaved values were not saved."; await ValidateDraftAsync(token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        RefreshCommand.Cancel();
        Cancel(ref _detailCancellation);
        Cancel(ref _occurrenceCancellation);
        RefreshCommand.Dispose();
        ClearFiltersCommand.Dispose();
        FirstPageCommand.Dispose();
        PreviousPageCommand.Dispose();
        NextPageCommand.Dispose();
        LastPageCommand.Dispose();
    }
}
