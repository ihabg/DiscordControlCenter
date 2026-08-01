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

    public HarnessWindowViewModel(Window owner)
    {
        _owner = owner;
        _seed = HarnessScenario.CreateAll();
        Scenarios = new ObservableCollection<HarnessScenario>(_seed);
        Approval = new HarnessApprovalViewModel(owner);
        NormalLayoutCommand = new RelayCommand(_ => SetLayout(1600, 900));
        NarrowLayoutCommand = new RelayCommand(_ => SetLayout(1100, 700));
        ToggleConnectionCommand = new RelayCommand(_ => { _connected = !_connected; Apply(); });
        ToggleSendMessagesCommand = new RelayCommand(_ => { _sendMessages = !_sendMessages; Apply(); });
        ResetCommand = new RelayCommand(_ => { _connected = true; _sendMessages = true; Apply(); });
        SelectedScenario = Scenarios[0];
    }

    public ObservableCollection<HarnessScenario> Scenarios { get; }
    public HarnessApprovalViewModel Approval { get; }
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
        }
    }
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

public enum HarnessScenarioKind { Plain, FullEmbed, NearLimit, OverLimit, BroadMention, Disconnected, MissingSendMessages, MissingEmbedLinks, Legacy, Unsupported, MissingData, TargetIds, LongText, Delivered, Failed, Uncertain, Skipped, Archived }
