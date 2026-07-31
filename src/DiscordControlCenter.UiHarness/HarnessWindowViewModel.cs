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
    }

    public ObservableCollection<ScheduledApprovalListItem> Approvals { get; } = [];
    public ObservableCollection<ContentUsageItem> ApprovalPlainMessageUsage { get; } = [];
    public ObservableCollection<ContentUsageItem> ApprovalEmbedUsage { get; } = [];
    public ObservableCollection<MentionPolicyUsageItem> ApprovalMentionPolicy { get; } = [];
    public ObservableCollection<string> ApprovalUsageWarnings { get; } = [];
    public ObservableCollection<ScheduledApprovalPreflightCheckItem> ApprovalPreflightChecks { get; } = [];
    public ICommand RefreshApprovalsCommand { get; }
    public ICommand RefreshApprovalStatusCommand { get; }
    public RelayCommand ApproveApprovalCommand { get; }
    public RelayCommand SkipApprovalCommand { get; }
    public RelayCommand ArchiveApprovalCommand { get; }
    public ScheduledApprovalListItem? SelectedApproval { get => _selectedApproval; set => SetProperty(ref _selectedApproval, value); }
    public ScheduledMessageApproval? ApprovalDetails { get => _approvalDetails; private set => SetProperty(ref _approvalDetails, value); }
    public string? StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string ApprovalDisabledReason { get => _approvalDisabledReason; private set => SetProperty(ref _approvalDisabledReason, value); }
    public string ApprovalPreflightSummary { get => _approvalPreflightSummary; private set => SetProperty(ref _approvalPreflightSummary, value); }
    public string ApprovalCheckedAt { get => _approvalCheckedAt; private set => SetProperty(ref _approvalCheckedAt, value); }
    public bool CanApprove { get; private set; }

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
        Approvals.Add(SelectedApproval);

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
        var occurrence = new ScheduledMessageOccurrence(Guid.NewGuid(), definition.Id, DateTimeOffset.UtcNow.AddMinutes(-10), MessageOperationState.PendingApproval, Guid.NewGuid(), null, null);
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

public enum HarnessScenarioKind { Plain, FullEmbed, NearLimit, OverLimit, BroadMention, Disconnected, MissingSendMessages, MissingEmbedLinks, Legacy, Unsupported, MissingData, TargetIds, LongText }
