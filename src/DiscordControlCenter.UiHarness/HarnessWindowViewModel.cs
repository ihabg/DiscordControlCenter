using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using DiscordControlCenter.App.Mvvm;
using DiscordControlCenter.App.ViewModels;
using DiscordControlCenter.App.Views;
using DiscordControlCenter.Application.Messaging;
using DiscordControlCenter.Core.Messaging;

namespace DiscordControlCenter.UiHarness;

/// <summary>
/// Deliberately has no service provider or persistence dependency. All state is rebuilt in
/// memory, so this executable cannot read bot tokens, connect a gateway, write Discord, or
/// modify the production SQLite database.
/// </summary>
public sealed class HarnessWindowViewModel : ObservableObject
{
    private readonly Window _owner;
    private readonly List<HarnessScenario> _seed;
    private HarnessScenario? _selectedScenario;
    private bool _connected = true;
    private bool _sendMessages = true;
    private readonly HarnessScheduledMessageQueryService _scheduleService;
    private readonly HarnessScheduledMessageDraftService _draftService;

    public HarnessWindowViewModel(Window owner)
    {
        _owner = owner;
        _seed = HarnessScenario.CreateAll();
        Scenarios = new ObservableCollection<HarnessScenario>(_seed);
        Approval = new HarnessApprovalViewModel(owner);
        _scheduleService = new HarnessScheduledMessageQueryService();
        _draftService = new HarnessScheduledMessageDraftService();
        ScheduledMessages = new ScheduledMessagesViewModel(_scheduleService, _draftService, new HarnessDiscardConfirmationService());
        NormalLayoutCommand = new RelayCommand(_ => SetLayout(1600, 900));
        NarrowLayoutCommand = new RelayCommand(_ => SetLayout(1100, 700));
        ToggleConnectionCommand = new RelayCommand(_ => { _connected = !_connected; Apply(); });
        ToggleSendMessagesCommand = new RelayCommand(_ => { _sendMessages = !_sendMessages; Apply(); });
        ResetCommand = new RelayCommand(_ => { _connected = true; _sendMessages = true; Apply(); });
        SelectedScenario = Scenarios[0];
    }

    public ObservableCollection<HarnessScenario> Scenarios { get; }
    public HarnessApprovalViewModel Approval { get; }
    public ScheduledMessagesViewModel ScheduledMessages { get; }
    public ICommand NormalLayoutCommand { get; }
    public ICommand NarrowLayoutCommand { get; }
    public ICommand ToggleConnectionCommand { get; }
    public ICommand ToggleSendMessagesCommand { get; }
    public ICommand ResetCommand { get; }

    public HarnessScenario? SelectedScenario
    {
        get => _selectedScenario;
        set { if (SetProperty(ref _selectedScenario, value)) Apply(); }
    }

    private void SetLayout(double width, double height)
    {
        _owner.Width = width;
        _owner.Height = height;
    }

    private void Apply()
    {
        if (_selectedScenario is not null)
        {
            Approval.Load(_selectedScenario, _connected, _sendMessages);
            _scheduleService.Scenario = _selectedScenario;
            _draftService.Scenario = _selectedScenario;
            if (_selectedScenario.Kind == HarnessScenarioKind.NoBotServerScope)
            {
                ScheduledMessages.SetContext(null, null, null, null);
            }
            else
            {
                ScheduledMessages.SetContext(HarnessScheduledMessageQueryService.BotId, "Harness bot", HarnessScheduledMessageQueryService.ServerId, "Harness server");
                _ = ScheduledMessages.LoadAsync(CancellationToken.None);
                if (_selectedScenario.Kind.ToString().StartsWith("Draft", StringComparison.Ordinal)) ScheduledMessages.NewDraftCommand.Execute(null);
            }
        }
    }
}

public sealed class HarnessDiscardConfirmationService : DiscordControlCenter.App.Services.IDraftDiscardConfirmationService
{
    public bool ConfirmDiscard(string actionDescription) => false;
}

public sealed class HarnessApprovalViewModel : ObservableObject
{
    private readonly Window _owner;
    private ScheduledMessageApproval? _approvalDetails;
    private ScheduledApprovalListItem? _selectedApproval;
    private string? _statusMessage;
    private string _approvalDisabledReason = string.Empty;
    private string _approvalPreflightSummary = "Choose a test scenario.";
    private string _approvalCheckedAt = string.Empty;

    public HarnessApprovalViewModel(Window owner)
    {
        _owner = owner;
        RefreshApprovalsCommand = new RelayCommand(_ => StatusMessage = "Harness data is already current.");
        RefreshApprovalStatusCommand = new RelayCommand(_ => StatusMessage = "Current status was recomputed from isolated test state.");
        ApproveApprovalCommand = new RelayCommand(_ => Decide("Approve and send"), _ => CanApprove);
        SkipApprovalCommand = new RelayCommand(_ => Decide("Skip occurrence"), _ => ApprovalDetails is not null);
        ArchiveApprovalCommand = new RelayCommand(_ => Decide("Archive occurrence"), _ => ApprovalDetails is not null);
        ApplyApprovalFiltersCommand = new RelayCommand(_ => StatusMessage = "Filters were applied to isolated harness data.");
        ClearApprovalFiltersCommand = new RelayCommand(_ => StatusMessage = "Harness filters were cleared.");
        PreviousApprovalPageCommand = new RelayCommand(_ => ApprovalPageNumber = Math.Max(1, ApprovalPageNumber - 1));
        NextApprovalPageCommand = new RelayCommand(_ => ApprovalPageNumber = Math.Min(ApprovalTotalPages, ApprovalPageNumber + 1));
        FirstApprovalPageCommand = new RelayCommand(_ => ApprovalPageNumber = 1);
        LastApprovalPageCommand = new RelayCommand(_ => ApprovalPageNumber = ApprovalTotalPages);
        OpenHistoricalContentCommand = new RelayCommand(_ => HistoryContentIsOpen = true, _ => HistoryDetails?.ImmutableContent is not null);
        RefreshHistoryCommand = new RelayCommand(_ => StatusMessage = "Terminal harness history is already current.");
        ApplyHistoryFiltersCommand = new RelayCommand(_ => StatusMessage = "History filters were applied to isolated harness data.");
        ClearHistoryFiltersCommand = new RelayCommand(_ => StatusMessage = "Harness history filters were cleared.");
        PreviousHistoryPageCommand = new RelayCommand(_ => HistoryPageNumber = Math.Max(1, HistoryPageNumber - 1));
        NextHistoryPageCommand = new RelayCommand(_ => HistoryPageNumber = Math.Min(HistoryTotalPages, HistoryPageNumber + 1));
        FirstHistoryPageCommand = new RelayCommand(_ => HistoryPageNumber = 1);
        LastHistoryPageCommand = new RelayCommand(_ => HistoryPageNumber = HistoryTotalPages);
    }

    public ObservableCollection<ScheduledApprovalListItem> Approvals { get; } = [];
    public ObservableCollection<ContentUsageItem> ApprovalPlainMessageUsage { get; } = [];
    public ObservableCollection<ContentUsageItem> ApprovalEmbedUsage { get; } = [];
    public ObservableCollection<MentionPolicyUsageItem> ApprovalMentionPolicy { get; } = [];
    public ObservableCollection<string> ApprovalUsageWarnings { get; } = [];
    public ObservableCollection<ScheduledApprovalPreflightCheckItem> ApprovalPreflightChecks { get; } = [];
    public ObservableCollection<ScheduledApprovalListItem> ApprovalHistory { get; } = [];
    public ObservableCollection<ApprovalChoice<Guid>> ApprovalSchedules { get; } = [new("All schedules", Guid.Empty, true), new("Harness weekday schedule", Guid.Parse("11111111-1111-1111-1111-111111111111")), new("Deleted or unavailable schedule", Guid.Parse("33333333-3333-3333-3333-333333333333"))];
    public ICommand RefreshApprovalsCommand { get; }
    public ICommand RefreshApprovalStatusCommand { get; }
    public RelayCommand ApproveApprovalCommand { get; }
    public RelayCommand SkipApprovalCommand { get; }
    public RelayCommand ArchiveApprovalCommand { get; }
    public ICommand ApplyApprovalFiltersCommand { get; }
    public ICommand ClearApprovalFiltersCommand { get; }
    public ICommand PreviousApprovalPageCommand { get; }
    public ICommand NextApprovalPageCommand { get; }
    public ICommand FirstApprovalPageCommand { get; }
    public ICommand LastApprovalPageCommand { get; }
    public RelayCommand OpenHistoricalContentCommand { get; }
    public ICommand RefreshHistoryCommand { get; }
    public ICommand ApplyHistoryFiltersCommand { get; }
    public ICommand ClearHistoryFiltersCommand { get; }
    public ICommand PreviousHistoryPageCommand { get; }
    public ICommand NextHistoryPageCommand { get; }
    public ICommand FirstHistoryPageCommand { get; }
    public ICommand LastHistoryPageCommand { get; }
    public ScheduledApprovalListItem? SelectedApproval { get => _selectedApproval; set => SetProperty(ref _selectedApproval, value); }
    public ScheduledMessageApproval? ApprovalDetails { get => _approvalDetails; private set => SetProperty(ref _approvalDetails, value); }
    public string? StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string ApprovalDisabledReason { get => _approvalDisabledReason; private set => SetProperty(ref _approvalDisabledReason, value); }
    public string ApprovalPreflightSummary { get => _approvalPreflightSummary; private set => SetProperty(ref _approvalPreflightSummary, value); }
    public string ApprovalCheckedAt { get => _approvalCheckedAt; private set => SetProperty(ref _approvalCheckedAt, value); }
    public bool CanApprove { get; private set; }
    public string ApprovalSearch { get; set; } = string.Empty;
    public DateTime? ApprovalFromDate { get; set; }
    public DateTime? ApprovalToDate { get; set; }
    public IReadOnlyList<ApprovalChoice<MessageOperationState>> ApprovalStates { get; } = [new("All states", default, true), new("Pending approval", MessageOperationState.PendingApproval), new("Delivered", MessageOperationState.Delivered), new("Failed", MessageOperationState.Failed), new("Outcome uncertain", MessageOperationState.Uncertain), new("Skipped", MessageOperationState.Skipped), new("Archived", MessageOperationState.Archived)];
    public IReadOnlyList<ApprovalChoice<SnapshotCompatibility>> ApprovalCompatibilities { get; } = [new("Any compatibility", default, true), new("Supported", SnapshotCompatibility.Supported), new("Supported legacy", SnapshotCompatibility.SupportedLegacy), new("Unsupported newer version", SnapshotCompatibility.UnsupportedNewerVersion), new("Missing required data", SnapshotCompatibility.MissingRequiredData), new("Corrupt", SnapshotCompatibility.Corrupt)];
    public IReadOnlyList<ApprovalChoice<bool>> ApprovalBooleanChoices { get; } = [new("Any", default, true), new("Yes", true), new("No", false)];
    public IReadOnlyList<ApprovalChoice<ScheduledApprovalSort>> ApprovalSorts { get; } = [new("Due time — oldest first", ScheduledApprovalSort.DueAscending), new("Due time — newest first", ScheduledApprovalSort.DueDescending), new("Reservation time — newest first", ScheduledApprovalSort.NewestReservation), new("Reservation time — oldest first", ScheduledApprovalSort.OldestReservation), new("Schedule name", ScheduledApprovalSort.ScheduleName), new("Server name", ScheduledApprovalSort.ServerName), new("State", ScheduledApprovalSort.State), new("Decision time — newest first", ScheduledApprovalSort.DecisionNewest)];
    public IReadOnlyList<int> ApprovalPageSizes { get; } = [10, 25, 50, 100, 200];
    public ApprovalChoice<MessageOperationState>? SelectedApprovalState { get; set; }
    public ApprovalChoice<SnapshotCompatibility>? SelectedApprovalCompatibility { get; set; }
    public ApprovalChoice<bool>? SelectedApprovalBroadMention { get; set; }
    public ApprovalChoice<bool>? SelectedApprovalManualReview { get; set; }
    public ApprovalChoice<ScheduledApprovalSort>? SelectedApprovalSort { get; set; }
    public ApprovalChoice<Guid>? SelectedApprovalSchedule { get; set; }
    public int ApprovalPageSize { get; set; } = 10;
    public int ApprovalPageNumber { get; private set; } = 1;
    public int ApprovalTotalCount => Approvals.Count;
    public int ApprovalTotalPages => Math.Max(1, (int)Math.Ceiling(Approvals.Count / (double)ApprovalPageSize));
    public bool IsApprovalQueryLoading => Approvals.Count < 0;
    public string ApprovalFilterSummary => $"Harness filters: safe metadata only ({Approvals.Count} records).";
    public string? ApprovalQueryMessage => Approvals.Count == 0 ? "No isolated results match this scenario." : null;
    public ScheduledApprovalListItem? SelectedHistory { get; set; }
    public ScheduledMessageApproval? HistoryDetails { get; private set; }
    public bool HistoryContentIsOpen { get; private set; }
    public string HistoryManualReviewExplanation => HistoryDetails?.Occurrence.State == MessageOperationState.Uncertain ? "Manual review required. This harness never retries or sends." : string.Empty;
    public string ApprovalScopeSummary => $"Current approval scope follows the harness test bot and private test server ({ApprovalSchedules.Count} schedules).";
    public string HistorySearch { get; set; } = string.Empty;
    public DateTime? HistoryFromDueDate { get; set; }
    public DateTime? HistoryToDueDate { get; set; }
    public DateTime? HistoryFromDecisionDate { get; set; }
    public DateTime? HistoryToDecisionDate { get; set; }
    public ApprovalChoice<Guid>? SelectedHistorySchedule { get; set; }
    public ApprovalChoice<MessageOperationState>? SelectedHistoryState { get; set; }
    public ApprovalChoice<SnapshotCompatibility>? SelectedHistoryCompatibility { get; set; }
    public ApprovalChoice<bool>? SelectedHistoryBroadMention { get; set; }
    public ApprovalChoice<bool>? SelectedHistoryManualReview { get; set; }
    public IReadOnlyList<ApprovalChoice<ScheduledApprovalSort>> HistorySorts { get; } = [new("Decision time — newest first", ScheduledApprovalSort.DecisionNewest), new("Decision time — oldest first", ScheduledApprovalSort.DecisionOldest), new("Schedule name", ScheduledApprovalSort.ScheduleName), new("Server name", ScheduledApprovalSort.ServerName), new("Terminal state", ScheduledApprovalSort.State)];
    public ApprovalChoice<ScheduledApprovalSort>? SelectedHistorySort { get; set; }
    public int HistoryPageSize { get; set; } = 10;
    public int HistoryPageNumber { get; private set; } = 1;
    public int HistoryTotalCount => ApprovalHistory.Count;
    public int HistoryTotalPages => Math.Max(1, (int)Math.Ceiling(ApprovalHistory.Count / (double)HistoryPageSize));
    public bool IsHistoryQueryLoading => ApprovalHistory.Count < 0;
    public bool CanGoToPreviousHistoryPage => HistoryPageNumber > 1;
    public bool CanGoToNextHistoryPage => HistoryPageNumber < HistoryTotalPages;
    public string HistoryFilterSummary => $"Harness history filters: safe metadata only ({ApprovalHistory.Count} records).";
    public string? HistoryQueryMessage => ApprovalHistory.Count == 0 ? "No isolated terminal history matches this scenario." : null;

    public void Load(HarnessScenario scenario, bool connected, bool sendMessages)
    {
        var approval = scenario.CreateApproval();
        ApprovalDetails = approval;
        var occurrence = approval.Occurrence;
        SelectedApproval = new ScheduledApprovalListItem(
            occurrence.Id, approval.Snapshot.Id, scenario.Name, approval.Snapshot.BotProfileId,
            approval.Snapshot.Destination.ServerId, approval.Snapshot.Destination.ServerName,
            approval.Snapshot.Destination.ChannelId, approval.Snapshot.Destination.ChannelName ?? "#test-channel",
            occurrence.OccurrenceAt, approval.Snapshot.TimeZoneId, occurrence.OccurrenceAt,
            occurrence.State, null, null, approval.ImmutableContent is not null,
            approval.ImmutableContent?.Embed is not null, approval.ImmutableContent?.AllowedMentions.HasBroadMentions == true,
            1, approval.Compatibility, occurrence.CorrelationId, null, null);
        Approvals.Clear();
        for (var index = 0; index < 30; index++) Approvals.Add(SelectedApproval with { OccurrenceId = Guid.NewGuid(), ScheduleName = $"Harness schedule {index + 1:D2}", DueAt = occurrence.OccurrenceAt.AddMinutes(index) });
        SelectedApproval = Approvals[0];
        SelectedApprovalSchedule = ApprovalSchedules[0];
        ApprovalHistory.Clear();
        var terminalStates = new[] { MessageOperationState.Delivered, MessageOperationState.Failed, MessageOperationState.Uncertain, MessageOperationState.Skipped, MessageOperationState.Archived };
        for (var index = 0; index < 30; index++)
        {
            var state = terminalStates[index % terminalStates.Length];
            ApprovalHistory.Add(SelectedApproval with { OccurrenceId = Guid.NewGuid(), State = state, ScheduleName = index == 29 ? "Deleted or unavailable schedule" : $"Harness schedule {index + 1:D2}", DecisionAt = DateTimeOffset.UtcNow.AddMinutes(-index) });
        }
        SelectedHistory = ApprovalHistory[0];
        SelectedHistorySchedule = ApprovalSchedules[0];
        SelectedHistoryState = ApprovalStates[0];
        SelectedHistorySort = HistorySorts[0];
        HistoryDetails = approval;
        HistoryContentIsOpen = false;

        ApprovalPlainMessageUsage.Clear();
        ApprovalEmbedUsage.Clear();
        ApprovalMentionPolicy.Clear();
        ApprovalUsageWarnings.Clear();
        var usage = MessageLimits.GetUsage(approval.ImmutableContent);
        foreach (var row in usage.PlainMessageRows) ApprovalPlainMessageUsage.Add(new ContentUsageItem(row));
        foreach (var row in usage.EmbedRows) ApprovalEmbedUsage.Add(new ContentUsageItem(row));
        foreach (var warning in usage.ValidationWarnings) ApprovalUsageWarnings.Add(warning);
        foreach (var row in MentionRows(approval.ImmutableContent?.AllowedMentions)) ApprovalMentionPolicy.Add(new MentionPolicyUsageItem(row));

        var checks = scenario.CreateChecks(connected, sendMessages, usage);
        ApprovalPreflightChecks.Clear();
        foreach (var check in checks) ApprovalPreflightChecks.Add(new ScheduledApprovalPreflightCheckItem(check));
        CanApprove = checks.All(check => !check.BlocksApproval);
        OnPropertyChanged(nameof(CanApprove));
        ApprovalDisabledReason = CanApprove ? string.Empty : checks.First(check => check.BlocksApproval).Remediation ?? checks.First(check => check.BlocksApproval).Explanation;
        ApprovalPreflightSummary = CanApprove
            ? "Allowed — all required snapshot and current test checks passed."
            : checks.First(check => check.BlocksApproval).Explanation;
        ApprovalCheckedAt = "Harness check: " + DateTimeOffset.UtcNow.ToString("u");
        StatusMessage = "Test data only. Approve, Skip, and Archive do not leave this process.";
        NotifyCommands();
    }

    private void Decide(string action)
    {
        if (ApprovalDetails is null)
        {
            return;
        }

        var dialog = new ScheduledApprovalDecisionWindow(new ScheduledApprovalDecisionViewModel(action, ApprovalDetails)) { Owner = _owner };
        if (dialog.ShowDialog() == true)
        {
            StatusMessage = $"{action} was recorded in isolated in-memory harness data only.";
        }
    }

    private static IReadOnlyList<MentionPolicyUsageRow> MentionRows(AllowedMentionPolicy? policy)
    {
        policy ??= AllowedMentionPolicy.None;
        return
        [
            new("mentions.everyone", "Everyone mention", policy.AllowEveryoneAndHere, policy.AllowEveryoneAndHere ? "Allowed by saved test policy." : "Blocked by saved test policy."),
            new("mentions.roles", "Role mention parsing", policy.AllowRoleMentions, policy.AllowRoleMentions ? "Allowed by saved test policy." : "Blocked by saved test policy."),
            new("mentions.users", "User mention parsing", policy.AllowedUserIds.Length > 0, policy.AllowedUserIds.Length > 0 ? "Saved user targets are displayed below." : "No saved user targets.")
        ];
    }

    private void NotifyCommands()
    {
        ApproveApprovalCommand.NotifyCanExecuteChanged();
        SkipApprovalCommand.NotifyCanExecuteChanged();
        ArchiveApprovalCommand.NotifyCanExecuteChanged();
    }

}

public sealed record HarnessScenario(string Name, HarnessScenarioKind Kind)
{
    public override string ToString() => Name;

    public static List<HarnessScenario> CreateAll() =>
    [
        new("1. Supported plain message", HarnessScenarioKind.Plain),
        new("2. Supported full embed", HarnessScenarioKind.FullEmbed),
        new("3. Near-limit content", HarnessScenarioKind.NearLimit),
        new("4. Over-limit content", HarnessScenarioKind.OverLimit),
        new("5. Broad-mention occurrence", HarnessScenarioKind.BroadMention),
        new("6. Disconnected bot", HarnessScenarioKind.Disconnected),
        new("7. Missing Send Messages", HarnessScenarioKind.MissingSendMessages),
        new("8. Missing Embed Links", HarnessScenarioKind.MissingEmbedLinks),
        new("9. Supported legacy snapshot", HarnessScenarioKind.Legacy),
        new("10. Unsupported newer snapshot", HarnessScenarioKind.Unsupported),
        new("11. Missing required snapshot data", HarnessScenarioKind.MissingData),
        new("12. Saved role and user target IDs", HarnessScenarioKind.TargetIds),
        new("13. Long names and explanations", HarnessScenarioKind.LongText)
        ,new("14. Delivered history", HarnessScenarioKind.Delivered)
        ,new("15. Failed history", HarnessScenarioKind.Failed)
        ,new("16. Uncertain manual review", HarnessScenarioKind.Uncertain)
        ,new("17. Skipped history", HarnessScenarioKind.Skipped)
        ,new("18. Archived history", HarnessScenarioKind.Archived)
        ,new("19. No bot/server scope", HarnessScenarioKind.NoBotServerScope)
        ,new("20. No schedules", HarnessScenarioKind.NoSchedules)
        ,new("21. No filtered schedule results", HarnessScenarioKind.NoFilteredSchedules)
        ,new("22. Draft schedule", HarnessScenarioKind.ScheduleDraft)
        ,new("23. Enabled schedule and multiple pages", HarnessScenarioKind.ScheduleEnabled)
        ,new("24. Disabled schedule", HarnessScenarioKind.ScheduleDisabled)
        ,new("25. Faulted schedule", HarnessScenarioKind.ScheduleFaulted)
        ,new("26. Expired schedule", HarnessScenarioKind.ScheduleExpired)
        ,new("27. Archived schedule", HarnessScenarioKind.ScheduleArchived)
        ,new("28. One-time recurrence", HarnessScenarioKind.ScheduleOnce)
        ,new("29. Weekly selected weekdays", HarnessScenarioKind.ScheduleWeekly)
        ,new("30. Invalid recurrence", HarnessScenarioKind.ScheduleInvalidRecurrence)
        ,new("31. Invalid time zone", HarnessScenarioKind.ScheduleInvalidTimeZone)
        ,new("32. Missing template metadata", HarnessScenarioKind.ScheduleMissingTemplate)
        ,new("33. Deleted destination metadata", HarnessScenarioKind.ScheduleDeletedDestination)
        ,new("34. Long schedule and time-zone labels", HarnessScenarioKind.ScheduleLongText)
        ,new("35. Recent delivered occurrence", HarnessScenarioKind.OccurrenceDelivered)
        ,new("36. Recent failed occurrence", HarnessScenarioKind.OccurrenceFailed)
        ,new("37. Recent pending approval", HarnessScenarioKind.OccurrencePendingApproval)
        ,new("38. Recent skipped occurrence", HarnessScenarioKind.OccurrenceSkipped)
        ,new("39. Recent archived occurrence", HarnessScenarioKind.OccurrenceArchived)
        ,new("40. Recent uncertain/manual-review occurrence", HarnessScenarioKind.OccurrenceUncertain)
        ,new("41. No recent occurrences", HarnessScenarioKind.NoRecentOccurrences)
        ,new("42. Schedule query error", HarnessScenarioKind.ScheduleQueryError)
        ,new("43. Schedule detail error", HarnessScenarioKind.ScheduleDetailError)
        ,new("44. Occurrence query error", HarnessScenarioKind.ScheduleOccurrenceError)
        ,new("45. New blank Draft", HarnessScenarioKind.DraftNewBlank)
        ,new("46. Valid Draft and preview", HarnessScenarioKind.DraftValid)
        ,new("47. Invalid Draft validation", HarnessScenarioKind.DraftInvalid)
        ,new("48. Dirty Draft", HarnessScenarioKind.DraftDirty)
        ,new("49. Draft save success", HarnessScenarioKind.DraftSaveSuccess)
        ,new("50. Draft save failure", HarnessScenarioKind.DraftSaveFailure)
        ,new("51. Scoped template options", HarnessScenarioKind.DraftScopedTemplates)
        ,new("52. Different template scope", HarnessScenarioKind.DraftDifferentScope)
        ,new("53. No Draft templates", HarnessScenarioKind.DraftNoTemplates)
        ,new("54. Deleted template", HarnessScenarioKind.DraftMissingTemplate)
        ,new("55. Long template name", HarnessScenarioKind.DraftLongTemplate)
        ,new("56. Template loading failure", HarnessScenarioKind.DraftTemplateFailure)
        ,new("57. Invalid saved time zone", HarnessScenarioKind.DraftInvalidZone)
        ,new("58. Weekly weekday required", HarnessScenarioKind.DraftWeeklyMissingDay)
        ,new("59. Invalid Draft date range", HarnessScenarioKind.DraftInvalidDateRange)
        ,new("60. No future occurrence", HarnessScenarioKind.DraftNoFutureOccurrence)
        ,new("61. Draft conflict retained", HarnessScenarioKind.DraftConflict)
        ,new("62. Reload latest Draft", HarnessScenarioKind.DraftReloaded)
        ,new("63. Unsaved changes confirmation", HarnessScenarioKind.DraftUnsavedChanges)
        ,new("64. Narrow Draft layout", HarnessScenarioKind.DraftNarrow)
    ];

    public ScheduledMessageApproval CreateApproval()
    {
        var content = Kind switch
        {
            HarnessScenarioKind.FullEmbed or HarnessScenarioKind.MissingEmbedLinks => FullEmbed(),
            HarnessScenarioKind.NearLimit => new MessageContent(new string('N', 1801), null, AllowedMentionPolicy.None),
            HarnessScenarioKind.OverLimit => new MessageContent(new string('O', 2001), null, AllowedMentionPolicy.None),
            HarnessScenarioKind.BroadMention => new MessageContent("@everyone isolated test warning", null, new AllowedMentionPolicy(true, true, ImmutableArray.Create<ulong>(900000000000000001), ImmutableArray.Create<ulong>(800000000000000001))),
            HarnessScenarioKind.TargetIds => new MessageContent("Saved target ID scenario", null, new AllowedMentionPolicy(false, false, ImmutableArray.Create<ulong>(700000000000000001, 700000000000000002), ImmutableArray.Create<ulong>(600000000000000001, 600000000000000002))),
            HarnessScenarioKind.LongText => new MessageContent("Long-name test", null, AllowedMentionPolicy.None),
            _ => new MessageContent("A supported immutable test message.", null, AllowedMentionPolicy.None)
        };
        var destination = MessageDestination.Channel(111111111111111111, Kind == HarnessScenarioKind.LongText ? "A deliberately long fake server name for wrapping verification and visual inspection" : "Fake QA Server", 222222222222222222, Kind == HarnessScenarioKind.LongText ? "#a-deliberately-long-test-channel-name-for-wrapping-and-button-reachability" : "#manual-approvals");
        var definition = new ScheduledMessageDefinition(Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"), destination, null, content, ScheduledMessageRecurrence.Daily, new TimeOnly(9, 0), "UTC", ImmutableArray<DayOfWeek>.Empty, DateTimeOffset.UtcNow.AddDays(-1), null, true, MissedOccurrencePolicy.RequireManualApproval, 0, null, null);
        var state = Kind switch { HarnessScenarioKind.Delivered => MessageOperationState.Delivered, HarnessScenarioKind.Failed => MessageOperationState.Failed, HarnessScenarioKind.Uncertain => MessageOperationState.Uncertain, HarnessScenarioKind.Skipped => MessageOperationState.Skipped, HarnessScenarioKind.Archived => MessageOperationState.Archived, _ => MessageOperationState.PendingApproval };
        var occurrence = new ScheduledMessageOccurrence(Guid.NewGuid(), definition.Id, DateTimeOffset.UtcNow.AddMinutes(-10), state, Guid.NewGuid(), state == MessageOperationState.PendingApproval ? null : DateTimeOffset.UtcNow, state is MessageOperationState.Failed or MessageOperationState.Uncertain ? "HARNESS_SAFE_FAILURE" : null);
        return new ScheduledMessageApproval(occurrence, definition)
        {
            ImmutableContent = Kind == HarnessScenarioKind.MissingData ? null : content,
            Compatibility = Kind == HarnessScenarioKind.Legacy ? SnapshotCompatibility.SupportedLegacy : Kind == HarnessScenarioKind.Unsupported ? SnapshotCompatibility.UnsupportedNewerVersion : Kind == HarnessScenarioKind.MissingData ? SnapshotCompatibility.MissingRequiredData : SnapshotCompatibility.Supported,
            CompatibilityMessage = Kind == HarnessScenarioKind.Legacy ? "Supported legacy test snapshot." : Kind == HarnessScenarioKind.Unsupported ? "This newer snapshot is intentionally unsupported." : null
        };
    }

    public IReadOnlyList<ScheduledApprovalPreflightCheck> CreateChecks(bool connected, bool sendMessages, ContentUsageResult usage)
    {
        var labels = new[] { "Snapshot compatibility", "Plain-message limits", "Embed limits", "Allowed-mention policy validity", "Bot profile exists", "Bot connected", "Server accessible", "Channel exists", "Channel supports message sending", "View Channel", "Send Messages", "Embed Links", "Attach Files", "Mention Everyone" };
        var ids = Enum.GetValues<ScheduledApprovalPreflightCheckId>();
        var checks = new List<ScheduledApprovalPreflightCheck>(14);
        foreach (var id in ids)
        {
            var required = id is not ScheduledApprovalPreflightCheckId.AttachFiles && !(id == ScheduledApprovalPreflightCheckId.EmbedLinks && CreateApproval().ImmutableContent?.Embed is null) && !(id == ScheduledApprovalPreflightCheckId.MentionEveryone && Kind != HarnessScenarioKind.BroadMention);
            var state = required ? ScheduledApprovalPreflightState.Allowed : ScheduledApprovalPreflightState.NotRequired;
            var explanation = required ? "Current isolated test check passed." : "Not required for this immutable test occurrence.";
            if (id == ScheduledApprovalPreflightCheckId.SnapshotCompatibility && Kind is HarnessScenarioKind.Unsupported or HarnessScenarioKind.MissingData) { state = ScheduledApprovalPreflightState.Blocked; explanation = "The immutable test snapshot is unsupported or incomplete."; }
            if (id == ScheduledApprovalPreflightCheckId.PlainMessageLimits && usage.PlainMessageRows.Any(row => row.BlocksApproval)) { state = ScheduledApprovalPreflightState.Blocked; explanation = "The immutable test message exceeds the limit."; }
            if (id == ScheduledApprovalPreflightCheckId.BotConnected && (!connected || Kind == HarnessScenarioKind.Disconnected)) { state = ScheduledApprovalPreflightState.Unavailable; explanation = "The fake bot is disconnected; live checks are unavailable."; }
            if (required && (!connected || Kind == HarnessScenarioKind.Disconnected) && id > ScheduledApprovalPreflightCheckId.BotConnected) { state = ScheduledApprovalPreflightState.Unavailable; explanation = "Connect the fake bot before this live test check is available."; }
            if (id == ScheduledApprovalPreflightCheckId.SendMessages && (!sendMessages || Kind == HarnessScenarioKind.MissingSendMessages)) { state = ScheduledApprovalPreflightState.Blocked; explanation = "The fake bot is missing Send Messages."; }
            if (id == ScheduledApprovalPreflightCheckId.EmbedLinks && Kind == HarnessScenarioKind.MissingEmbedLinks) { state = ScheduledApprovalPreflightState.Blocked; explanation = "The fake bot is missing Embed Links."; }
            if (id == ScheduledApprovalPreflightCheckId.ViewChannel && Kind == HarnessScenarioKind.LongText) explanation = "A deliberately long explanation verifies wrapping without hiding the decision footer or producing unreadable dark-control text in narrow layouts.";
            var blocks = required && state != ScheduledApprovalPreflightState.Allowed;
            checks.Add(new ScheduledApprovalPreflightCheck(id, labels[(int)id], state, required, blocks, explanation, blocks ? "Resolve this isolated test condition before approving." : null, null));
        }
        return checks;
    }

    private static MessageContent FullEmbed() => new(
        "A supported full-embed test message.",
        new EmbedDraft("Harness embed title", "A deterministic description used for visual inspection.", "https://example.invalid/harness", 0x5865F2, "Harness author", "https://example.invalid/author", "https://example.invalid/author.png", "https://example.invalid/thumb.png", "https://example.invalid/image.png", "Harness footer", "https://example.invalid/footer.png", DateTimeOffset.UtcNow, [new EmbedFieldDraft("Inline field", "Inline value", true), new EmbedFieldDraft("Block field", "Non-inline value", false)]),
        AllowedMentionPolicy.None);
}

public enum HarnessScenarioKind { Plain, FullEmbed, NearLimit, OverLimit, BroadMention, Disconnected, MissingSendMessages, MissingEmbedLinks, Legacy, Unsupported, MissingData, TargetIds, LongText, Delivered, Failed, Uncertain, Skipped, Archived, NoBotServerScope, NoSchedules, NoFilteredSchedules, ScheduleDraft, ScheduleEnabled, ScheduleDisabled, ScheduleFaulted, ScheduleExpired, ScheduleArchived, ScheduleOnce, ScheduleWeekly, ScheduleInvalidRecurrence, ScheduleInvalidTimeZone, ScheduleMissingTemplate, ScheduleDeletedDestination, ScheduleLongText, OccurrenceDelivered, OccurrenceFailed, OccurrencePendingApproval, OccurrenceSkipped, OccurrenceArchived, OccurrenceUncertain, NoRecentOccurrences, ScheduleQueryError, ScheduleDetailError, ScheduleOccurrenceError, DraftNewBlank, DraftValid, DraftInvalid, DraftDirty, DraftSaveSuccess, DraftSaveFailure, DraftScopedTemplates, DraftDifferentScope, DraftNoTemplates, DraftMissingTemplate, DraftLongTemplate, DraftTemplateFailure, DraftInvalidZone, DraftWeeklyMissingDay, DraftInvalidDateRange, DraftNoFutureOccurrence, DraftConflict, DraftReloaded, DraftUnsavedChanges, DraftNarrow }

/// <summary>In-memory only source for the shared Scheduled Messages presentation.</summary>
public sealed class HarnessScheduledMessageQueryService : IScheduledMessageQueryService
{
    public static readonly Guid BotId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    public const ulong ServerId = 444444444444444444;
    private static readonly Guid ScheduleId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    public HarnessScenario? Scenario { get; set; }

    public Task<ScheduledMessageFilterOptions> GetFilterOptionsAsync(Guid botProfileId, ulong serverId, CancellationToken cancellationToken) => Task.FromResult(new ScheduledMessageFilterOptions([new(Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"), "Harness template")], [new(555555555555555555, "#harness-destination")]));
    public Task<ScheduledMessagePage> QueryAsync(ScheduledMessageQuery query, CancellationToken cancellationToken)
    {
        if (Scenario?.Kind == HarnessScenarioKind.ScheduleQueryError) throw new InvalidOperationException("Harness query error");
        if (Scenario?.Kind is HarnessScenarioKind.NoSchedules or HarnessScenarioKind.NoFilteredSchedules) return Task.FromResult(new ScheduledMessagePage([], 0, 1, query.PageSize, DateTimeOffset.UtcNow));
        var item = CreateItem();
        var items = Scenario?.Kind == HarnessScenarioKind.ScheduleEnabled ? Enumerable.Range(1, 30).Select(index => item with { Id = Guid.NewGuid(), Name = $"Harness schedule {index:D2}" }).ToArray() : [item];
        return Task.FromResult(new ScheduledMessagePage(items, items.Length, query.PageNumber, query.PageSize, DateTimeOffset.UtcNow));
    }
    public Task<ScheduledMessageDetail?> GetDetailAsync(Guid botProfileId, ulong serverId, Guid scheduleId, CancellationToken cancellationToken)
    {
        if (Scenario?.Kind == HarnessScenarioKind.ScheduleDetailError) throw new InvalidOperationException("Harness detail error");
        var item = CreateItem(); var weekdays = item.Recurrence == ScheduledMessageRecurrence.Weekly && Scenario?.Kind != HarnessScenarioKind.ScheduleInvalidRecurrence ? ImmutableArray.Create(DayOfWeek.Monday, DayOfWeek.Friday) : ImmutableArray<DayOfWeek>.Empty;
        var definition = new ScheduledMessageDefinition(item.Id, BotId, MessageDestination.Channel(ServerId, "Harness server", 555555555555555555, item.ChannelName), null, null, item.Recurrence, new TimeOnly(9, 30), item.TimeZoneId, weekdays, DateTimeOffset.UtcNow, null, item.Lifecycle == ScheduledMessageLifecycle.Enabled, MissedOccurrencePolicy.RequireManualApproval, 0, null, item.NextRunAt) { Name = item.Name, SavedLifecycle = item.Lifecycle };
        var recurrenceSummary = Scenario?.Kind == HarnessScenarioKind.ScheduleInvalidRecurrence ? "Weekly recurrence has no selected weekdays." : item.Recurrence == ScheduledMessageRecurrence.Once ? "One time on 01 August 2026 at 09:30" : item.Recurrence == ScheduledMessageRecurrence.Weekly ? "Monday, Friday at 09:30" : "Every day at 09:30";
        return Task.FromResult<ScheduledMessageDetail?>(new(item.Id, item.Name, item.Lifecycle, "Harness read-only lifecycle explanation.", recurrenceSummary, item.TimeZoneId, Scenario?.Kind == HarnessScenarioKind.ScheduleInvalidTimeZone ? "Invalid or unavailable time zone." : null, definition));
    }
    public Task<ScheduledMessageOccurrencePage> GetRecentOccurrencesAsync(Guid botProfileId, ulong serverId, Guid scheduleId, int limit, CancellationToken cancellationToken)
    {
        if (Scenario?.Kind == HarnessScenarioKind.ScheduleOccurrenceError) throw new InvalidOperationException("Harness occurrence error");
        if (Scenario?.Kind == HarnessScenarioKind.NoRecentOccurrences) return Task.FromResult(new ScheduledMessageOccurrencePage([], limit));
        var state = Scenario?.Kind switch { HarnessScenarioKind.OccurrenceFailed => MessageOperationState.Failed, HarnessScenarioKind.OccurrencePendingApproval => MessageOperationState.PendingApproval, HarnessScenarioKind.OccurrenceSkipped => MessageOperationState.Skipped, HarnessScenarioKind.OccurrenceArchived => MessageOperationState.Archived, HarnessScenarioKind.OccurrenceUncertain => MessageOperationState.Uncertain, _ => MessageOperationState.Delivered };
        return Task.FromResult(new ScheduledMessageOccurrencePage([new(Guid.NewGuid(), 1, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(-6), null, DateTimeOffset.UtcNow, state, state == MessageOperationState.Failed ? "HARNESS_SAFE_FAILURE" : null, state == MessageOperationState.PendingApproval ? null : "Harness decision", Guid.NewGuid(), SnapshotCompatibility.Supported)], limit));
    }
    private ScheduledMessageListItem CreateItem()
    {
        var lifecycle = Scenario?.Kind switch { HarnessScenarioKind.ScheduleDraft => ScheduledMessageLifecycle.Draft, HarnessScenarioKind.ScheduleDisabled => ScheduledMessageLifecycle.Disabled, HarnessScenarioKind.ScheduleFaulted => ScheduledMessageLifecycle.Faulted, HarnessScenarioKind.ScheduleExpired => ScheduledMessageLifecycle.Expired, HarnessScenarioKind.ScheduleArchived => ScheduledMessageLifecycle.Archived, _ => ScheduledMessageLifecycle.Enabled };
        var recurrence = Scenario?.Kind switch { HarnessScenarioKind.ScheduleOnce => ScheduledMessageRecurrence.Once, HarnessScenarioKind.ScheduleWeekly or HarnessScenarioKind.ScheduleInvalidRecurrence => ScheduledMessageRecurrence.Weekly, _ => ScheduledMessageRecurrence.Daily };
        var name = Scenario?.Kind == HarnessScenarioKind.ScheduleLongText ? "A deliberately long harness schedule name that verifies readable wrapping at narrow widths" : "Harness scheduled message";
        var zone = Scenario?.Kind == HarnessScenarioKind.ScheduleInvalidTimeZone ? "Invalid/Harness-Time-Zone" : Scenario?.Kind == HarnessScenarioKind.ScheduleLongText ? "(UTC+12:45) Chatham Islands — deliberately long harness time-zone display" : "UTC";
        var channel = Scenario?.Kind == HarnessScenarioKind.ScheduleDeletedDestination ? "Deleted or unavailable channel" : "#harness-destination";
        var template = Scenario?.Kind == HarnessScenarioKind.ScheduleMissingTemplate ? "Deleted or unavailable template" : "Harness template";
        return new(ScheduleId, name, lifecycle, "Harness bot", "Harness server", channel, template, recurrence, zone, DateTimeOffset.UtcNow.AddDays(1), null, null, MissedOccurrencePolicy.RequireManualApproval, lifecycle is ScheduledMessageLifecycle.Faulted or ScheduledMessageLifecycle.Expired, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }
}

/// <summary>In-memory Draft Editor double. It does not resolve a writer, token store, SQLite connection, or scheduler.</summary>
public sealed class HarnessScheduledMessageDraftService : IScheduledMessageDraftService
{
    private static readonly Guid TemplateId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    public HarnessScenario? Scenario { get; set; }
    public ScheduledMessageDefinition CreateDraft(Guid botProfileId, MessageDestination destination) => Definition(Guid.NewGuid(), botProfileId, destination.ServerId, null, null);
    public Task<IReadOnlyList<ScheduledDraftTemplateOption>> GetTemplateOptionsAsync(Guid botProfileId, ulong serverId, CancellationToken cancellationToken)
    {
        if (Scenario?.Kind == HarnessScenarioKind.DraftTemplateFailure) throw new InvalidOperationException("Harness template load failure");
        IReadOnlyList<ScheduledDraftTemplateOption> options = Scenario?.Kind switch
        {
            HarnessScenarioKind.DraftNoTemplates => [],
            HarnessScenarioKind.DraftDifferentScope => [new(Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"), "Other scope template")],
            HarnessScenarioKind.DraftLongTemplate => [new(TemplateId, "A deliberately long scoped template name used only to verify safe wrapping and selector reachability in narrow layouts")],
            _ => [new(TemplateId, "Harness scoped template")]
        };
        return Task.FromResult(options);
    }
    public Task<ScheduledMessageDefinition?> LoadAsync(Guid botProfileId, ulong serverId, Guid scheduleId, CancellationToken cancellationToken)
    {
        var template = Scenario?.Kind == HarnessScenarioKind.DraftMissingTemplate ? Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee") : TemplateId;
        var definition = Definition(scheduleId, botProfileId, serverId, template, null) with
        {
            Name = Scenario?.Kind == HarnessScenarioKind.DraftDirty ? "Dirty persisted Draft" : "Harness persisted Draft",
            TimeZoneId = Scenario?.Kind == HarnessScenarioKind.DraftInvalidZone ? "Invalid/Harness-Time-Zone" : "UTC",
            Recurrence = Scenario?.Kind == HarnessScenarioKind.DraftWeeklyMissingDay ? ScheduledMessageRecurrence.Weekly : ScheduledMessageRecurrence.Daily,
            Weekdays = Scenario?.Kind == HarnessScenarioKind.DraftWeeklyMissingDay ? [] : [DayOfWeek.Monday],
            EndAt = Scenario?.Kind == HarnessScenarioKind.DraftInvalidDateRange ? DateTimeOffset.UtcNow.AddDays(-1) : null,
            StartAt = DateTimeOffset.UtcNow
        };
        return Task.FromResult<ScheduledMessageDefinition?>(definition);
    }
    public Task<ScheduledDraftValidation> ValidateAsync(ScheduledMessageDefinition definition, CancellationToken cancellationToken)
    {
        var error = Scenario?.Kind switch
        {
            HarnessScenarioKind.DraftInvalid => "Schedule name and source are required.",
            HarnessScenarioKind.DraftMissingTemplate => "The selected template is unavailable for this bot and server.",
            HarnessScenarioKind.DraftWeeklyMissingDay => "Select at least one weekday for weekly recurrence.",
            HarnessScenarioKind.DraftInvalidDateRange => "The end date cannot be before the start date.",
            _ => null
        };
        var warning = Scenario?.Kind == HarnessScenarioKind.DraftNoFutureOccurrence ? "This draft has no future occurrence with its current dates and recurrence." : null;
        return Task.FromResult(new ScheduledDraftValidation(error is null ? [] : [error], warning is null ? [] : [warning], "Harness recurrence preview", []));
    }
    public async Task<ScheduledDraftSaveResult> SaveAsync(ScheduledMessageDefinition definition, int expectedRevision, CancellationToken cancellationToken)
    {
        var validation = await ValidateAsync(definition, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid || Scenario?.Kind == HarnessScenarioKind.DraftSaveFailure) return new(false, false, null, validation, "Harness save failure. No data was written.");
        if (Scenario?.Kind == HarnessScenarioKind.DraftConflict) return new(false, true, null, validation, "This schedule changed elsewhere. Reload the latest version before saving.");
        var saved = definition with { Revision = expectedRevision + 1, IsEnabled = false, SavedLifecycle = ScheduledMessageLifecycle.Draft };
        return new(true, false, saved, validation, "Harness Draft save succeeded. No Discord action occurred.");
    }
    private static ScheduledMessageDefinition Definition(Guid id, Guid bot, ulong server, Guid? templateId, MessageContent? content) =>
        new(id, bot, MessageDestination.Channel(server, "Harness server", 555555555555555555, "#harness-destination"), templateId, content, ScheduledMessageRecurrence.Daily, new TimeOnly(9, 30), "UTC", [], DateTimeOffset.UtcNow, null, false, MissedOccurrencePolicy.RequireManualApproval, 0, null, null) { Name = "Harness Draft", SavedLifecycle = ScheduledMessageLifecycle.Draft, Revision = 1 };
}
