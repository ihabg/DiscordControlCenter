using System.Collections.ObjectModel;
using System.Collections.Immutable;
using DiscordControlCenter.App.Mvvm;
using DiscordControlCenter.App.Services;
using DiscordControlCenter.App.Views;
using DiscordControlCenter.Application.Explorer;
using DiscordControlCenter.Application.Messaging;
using DiscordControlCenter.Core.Bots;
using DiscordControlCenter.Core.Explorer;
using DiscordControlCenter.Core.Messaging;

namespace DiscordControlCenter.App.ViewModels;

public sealed class MessageChannelOption(ChannelReadModel channel)
{
    public ulong Id => channel.Id;
    public string Name => channel.Name;
    public string TypeName => channel.Kind == ChannelKind.Announcement ? "Announcement" : channel.Kind == ChannelKind.Thread ? "Thread" : "Text";
}

public sealed class MessageMemberOption(MemberReadModel member)
{
    public ulong Id => member.Id;
    public string DisplayName => member.DisplayName;
    public string Username => member.Username;
}

public sealed record ApprovalChoice<T>(string Label, T Value, bool IsAny = false)
{
    public override string ToString() => Label;
}

public sealed class MessagesViewModel : ObservableObject, IDisposable
{
    private readonly IBotExplorerService _explorer;
    private readonly IMessagePlanBuilder _planner;
    private readonly IMessageTemplateRepository _templates;
    private readonly IMessageDeliveryDialogService _deliveryDialog;
    private readonly IScheduledMessageRepository _scheduledMessages;
    private readonly IScheduledApprovalService _approvals;
    private readonly IScheduledApprovalPreflightService _approvalPreflight;
    private readonly UiDispatcher _dispatcher;
    private Guid? _botProfileId;
    private ulong? _serverId;
    private BotConnectionState _connectionState;
    private BotExplorerSnapshot? _snapshot;
    private MessageDestinationKind _destinationMode = MessageDestinationKind.ServerChannel;
    private MessageChannelOption? _selectedChannel;
    private MessageMemberOption? _selectedMember;
    private string _messageBody = string.Empty;
    private bool _includeEmbed;
    private string _embedTitle = string.Empty;
    private string _embedDescription = string.Empty;
    private string _templateName = string.Empty;
    private MessagePreview? _preview;
    private string? _statusMessage;
    private bool _disposed;
    private ScheduledApprovalListItem? _selectedApproval;
    private ScheduledMessageApproval? _approvalDetails;
    private bool _isApprovalBusy;
    private bool _isApprovalDetailsLoading;
    private bool _isApprovalPreflightLoading;
    private int _approvalSelectionVersion;
    private int _approvalPreflightVersion;
    private ScheduledApprovalPreflightResult? _approvalPreflightResult;
    private string _approvalSearch = string.Empty;
    private DateTime? _approvalFromDate;
    private DateTime? _approvalToDate;
    private ApprovalChoice<MessageOperationState>? _selectedApprovalState;
    private ApprovalChoice<SnapshotCompatibility>? _selectedApprovalCompatibility;
    private ApprovalChoice<bool>? _selectedApprovalBroadMention;
    private ApprovalChoice<bool>? _selectedApprovalManualReview;
    private ApprovalChoice<ScheduledApprovalSort>? _selectedApprovalSort;
    private int _approvalPageSize = 25;
    private int _approvalPageNumber = 1;
    private int _approvalTotalCount;
    private bool _isApprovalQueryLoading;
    private string? _approvalQueryMessage;
    private int _approvalQueryVersion;
    private int _approvalScheduleVersion;
    private ApprovalChoice<Guid>? _selectedApprovalSchedule;
    private string _historySearch = string.Empty;
    private DateTime? _historyFromDueDate;
    private DateTime? _historyToDueDate;
    private DateTime? _historyFromDecisionDate;
    private DateTime? _historyToDecisionDate;
    private ApprovalChoice<Guid>? _selectedHistorySchedule;
    private ApprovalChoice<MessageOperationState>? _selectedHistoryState;
    private ApprovalChoice<SnapshotCompatibility>? _selectedHistoryCompatibility;
    private ApprovalChoice<bool>? _selectedHistoryBroadMention;
    private ApprovalChoice<bool>? _selectedHistoryManualReview;
    private ApprovalChoice<ScheduledApprovalSort>? _selectedHistorySort;
    private int _historyPageSize = 25;
    private int _historyPageNumber = 1;
    private int _historyTotalCount;
    private bool _isHistoryQueryLoading;
    private string? _historyQueryMessage;
    private int _historyQueryVersion;
    private ScheduledApprovalListItem? _selectedHistory;
    private ScheduledMessageApproval? _historyDetails;
    private bool _isHistoryDetailsLoading;
    private bool _historyContentIsOpen;
    private int _historyDetailVersion;
    private readonly Array _destinationModes = Enum.GetValues<MessageDestinationKind>();

    public MessagesViewModel(
        IBotExplorerService explorer,
        IMessagePlanBuilder planner,
        IMessageTemplateRepository templates,
        IMessageDeliveryDialogService deliveryDialog,
        IScheduledMessageRepository scheduledMessages,
        IScheduledApprovalService approvals,
        IScheduledApprovalPreflightService approvalPreflight,
        UiDispatcher dispatcher)
    {
        _explorer = explorer;
        _planner = planner;
        _templates = templates;
        _deliveryDialog = deliveryDialog;
        _scheduledMessages = scheduledMessages;
        _approvals = approvals;
        _approvalPreflight = approvalPreflight;
        _dispatcher = dispatcher;
        _explorer.CacheChanged += OnCacheChanged;
        GeneratePreviewCommand = new RelayCommand(_ => GeneratePreview());
        SaveTemplateCommand = new AsyncRelayCommand(SaveTemplateAsync, () => CanSaveTemplate, OnCommandError);
        RefreshTemplatesCommand = new AsyncRelayCommand(LoadTemplatesAsync, errorHandler: OnCommandError);
        SendCommand = new AsyncRelayCommand(SendAsync, () => Preview is not null, OnCommandError);
        RefreshApprovalsCommand = new AsyncRelayCommand(LoadApprovalsAsync, errorHandler: OnCommandError);
        SkipApprovalCommand = new AsyncRelayCommand(SkipApprovalAsync, () => CanDecidePending, OnCommandError);
        ArchiveApprovalCommand = new AsyncRelayCommand(ArchiveApprovalAsync, () => CanDecidePending, OnCommandError);
        ApproveApprovalCommand = new AsyncRelayCommand(ApproveApprovalAsync, () => CanApproveApproval, OnCommandError);
        RefreshApprovalStatusCommand = new AsyncRelayCommand(RefreshApprovalStatusAsync, () => CanRefreshApprovalStatus, OnCommandError);
        ApplyApprovalFiltersCommand = new AsyncRelayCommand(ApplyApprovalFiltersAsync, () => !IsApprovalQueryLoading, OnCommandError);
        ClearApprovalFiltersCommand = new AsyncRelayCommand(ClearApprovalFiltersAsync, () => !IsApprovalQueryLoading, OnCommandError);
        PreviousApprovalPageCommand = new AsyncRelayCommand(_ => NavigateApprovalPageAsync(ApprovalPageNumber - 1, _), () => CanGoToPreviousApprovalPage, OnCommandError);
        NextApprovalPageCommand = new AsyncRelayCommand(_ => NavigateApprovalPageAsync(ApprovalPageNumber + 1, _), () => CanGoToNextApprovalPage, OnCommandError);
        FirstApprovalPageCommand = new AsyncRelayCommand(_ => NavigateApprovalPageAsync(1, _), () => CanGoToPreviousApprovalPage, OnCommandError);
        LastApprovalPageCommand = new AsyncRelayCommand(_ => NavigateApprovalPageAsync(ApprovalTotalPages, _), () => CanGoToNextApprovalPage, OnCommandError);
        OpenHistoricalContentCommand = new RelayCommand(_ => { HistoryContentIsOpen = true; }, _ => HistoryDetails?.ImmutableContent is not null);
        RefreshHistoryCommand = new AsyncRelayCommand(LoadHistoryAsync, () => !IsHistoryQueryLoading, OnCommandError);
        ApplyHistoryFiltersCommand = new AsyncRelayCommand(ApplyHistoryFiltersAsync, () => !IsHistoryQueryLoading, OnCommandError);
        ClearHistoryFiltersCommand = new AsyncRelayCommand(ClearHistoryFiltersAsync, () => !IsHistoryQueryLoading, OnCommandError);
        PreviousHistoryPageCommand = new AsyncRelayCommand(_ => NavigateHistoryPageAsync(HistoryPageNumber - 1, _), () => CanGoToPreviousHistoryPage, OnCommandError);
        NextHistoryPageCommand = new AsyncRelayCommand(_ => NavigateHistoryPageAsync(HistoryPageNumber + 1, _), () => CanGoToNextHistoryPage, OnCommandError);
        FirstHistoryPageCommand = new AsyncRelayCommand(_ => NavigateHistoryPageAsync(1, _), () => CanGoToPreviousHistoryPage, OnCommandError);
        LastHistoryPageCommand = new AsyncRelayCommand(_ => NavigateHistoryPageAsync(HistoryTotalPages, _), () => CanGoToNextHistoryPage, OnCommandError);
        _selectedApprovalState = ApprovalStates.First(choice => choice.Value == MessageOperationState.PendingApproval);
        _selectedApprovalSort = ApprovalSorts.First(choice => choice.Value == ScheduledApprovalSort.DueAscending);
        _selectedHistorySort = HistorySorts.First(choice => choice.Value == ScheduledApprovalSort.DecisionNewest);
        _selectedHistoryState = ApprovalStates.First(choice => choice.IsAny);
    }

    public ObservableCollection<MessageChannelOption> Channels { get; } = [];
    public ObservableCollection<MessageMemberOption> Members { get; } = [];
    public ObservableCollection<MessageTemplate> Templates { get; } = [];
    public ObservableCollection<string> ValidationErrors { get; } = [];
    public ObservableCollection<string> PreviewWarnings { get; } = [];
    public ObservableCollection<ScheduledApprovalListItem> Approvals { get; } = [];
    public ObservableCollection<ScheduledApprovalPreflightCheckItem> ApprovalPreflightChecks { get; } = [];
    public ObservableCollection<ContentUsageItem> ApprovalPlainMessageUsage { get; } = [];
    public ObservableCollection<ContentUsageItem> ApprovalEmbedUsage { get; } = [];
    public ObservableCollection<MentionPolicyUsageItem> ApprovalMentionPolicy { get; } = [];
    public ObservableCollection<string> ApprovalUsageWarnings { get; } = [];
    public ObservableCollection<ScheduledApprovalListItem> ApprovalHistory { get; } = [];
    public ObservableCollection<ApprovalChoice<Guid>> ApprovalSchedules { get; } = [];
    public Array DestinationModes => _destinationModes;
    public IReadOnlyList<ApprovalChoice<MessageOperationState>> ApprovalStates { get; } =
    [new("All states", default, true), new("Pending approval", MessageOperationState.PendingApproval), new("Delivered", MessageOperationState.Delivered), new("Failed", MessageOperationState.Failed), new("Outcome uncertain", MessageOperationState.Uncertain), new("Skipped", MessageOperationState.Skipped), new("Archived", MessageOperationState.Archived)];
    public IReadOnlyList<ApprovalChoice<SnapshotCompatibility>> ApprovalCompatibilities { get; } =
    [new("Any compatibility", default, true), new("Supported", SnapshotCompatibility.Supported), new("Supported legacy", SnapshotCompatibility.SupportedLegacy), new("Unsupported newer version", SnapshotCompatibility.UnsupportedNewerVersion), new("Missing required data", SnapshotCompatibility.MissingRequiredData), new("Corrupt", SnapshotCompatibility.Corrupt)];
    public IReadOnlyList<ApprovalChoice<bool>> ApprovalBooleanChoices { get; } = [new("Any", default, true), new("Yes", true), new("No", false)];
    public IReadOnlyList<ApprovalChoice<ScheduledApprovalSort>> ApprovalSorts { get; } = [new("Due time — oldest first", ScheduledApprovalSort.DueAscending), new("Due time — newest first", ScheduledApprovalSort.DueDescending), new("Reservation time — newest first", ScheduledApprovalSort.NewestReservation), new("Reservation time — oldest first", ScheduledApprovalSort.OldestReservation), new("Schedule name", ScheduledApprovalSort.ScheduleName), new("Server name", ScheduledApprovalSort.ServerName), new("State", ScheduledApprovalSort.State), new("Decision time — newest first", ScheduledApprovalSort.DecisionNewest)];
    public IReadOnlyList<ApprovalChoice<ScheduledApprovalSort>> HistorySorts { get; } = [new("Decision time — newest first", ScheduledApprovalSort.DecisionNewest), new("Decision time — oldest first", ScheduledApprovalSort.DecisionOldest), new("Due time — newest first", ScheduledApprovalSort.DueDescending), new("Due time — oldest first", ScheduledApprovalSort.DueAscending), new("Reservation time — newest first", ScheduledApprovalSort.NewestReservation), new("Reservation time — oldest first", ScheduledApprovalSort.OldestReservation), new("Schedule name", ScheduledApprovalSort.ScheduleName), new("Server name", ScheduledApprovalSort.ServerName), new("Terminal state", ScheduledApprovalSort.State)];
    public IReadOnlyList<int> ApprovalPageSizes { get; } = [10, 25, 50, 100, 200];

    public MessageDestinationKind DestinationMode
    {
        get => _destinationMode;
        set { if (SetProperty(ref _destinationMode, value)) { ClearPreview(); OnPropertyChanged(nameof(IsChannelDestination)); OnPropertyChanged(nameof(IsDirectMessageDestination)); } }
    }

    public bool IsChannelDestination => DestinationMode == MessageDestinationKind.ServerChannel;
    public bool IsDirectMessageDestination => DestinationMode == MessageDestinationKind.IndividualDirectMessage;
    public MessageChannelOption? SelectedChannel { get => _selectedChannel; set { if (SetProperty(ref _selectedChannel, value)) ClearPreview(); } }
    public MessageMemberOption? SelectedMember { get => _selectedMember; set { if (SetProperty(ref _selectedMember, value)) ClearPreview(); } }
    public string MessageBody { get => _messageBody; set { if (SetProperty(ref _messageBody, value)) { ClearPreview(); OnPropertyChanged(nameof(CharacterUsage)); } } }
    public bool IncludeEmbed { get => _includeEmbed; set { if (SetProperty(ref _includeEmbed, value)) ClearPreview(); } }
    public string EmbedTitle { get => _embedTitle; set { if (SetProperty(ref _embedTitle, value)) ClearPreview(); } }
    public string EmbedDescription { get => _embedDescription; set { if (SetProperty(ref _embedDescription, value)) ClearPreview(); } }
    public string TemplateName { get => _templateName; set { if (SetProperty(ref _templateName, value)) { OnPropertyChanged(nameof(CanSaveTemplate)); SaveTemplateCommand.NotifyCanExecuteChanged(); } } }
    public MessagePreview? Preview { get => _preview; private set => SetProperty(ref _preview, value); }
    public string? StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public ScheduledApprovalListItem? SelectedApproval { get => _selectedApproval; set { if (SetProperty(ref _selectedApproval, value)) { _ = LoadApprovalDetailsAsync(); NotifyApprovalCommands(); } } }
    public ScheduledMessageApproval? ApprovalDetails { get => _approvalDetails; private set => SetProperty(ref _approvalDetails, value); }
    public bool IsApprovalBusy { get => _isApprovalBusy; private set { if (SetProperty(ref _isApprovalBusy, value)) NotifyApprovalCommands(); } }
    public bool IsApprovalDetailsLoading { get => _isApprovalDetailsLoading; private set { if (SetProperty(ref _isApprovalDetailsLoading, value)) NotifyApprovalCommands(); } }
    public bool IsApprovalPreflightLoading { get => _isApprovalPreflightLoading; private set { if (SetProperty(ref _isApprovalPreflightLoading, value)) NotifyApprovalCommands(); } }
    public ScheduledApprovalPreflightResult? ApprovalPreflightResult { get => _approvalPreflightResult; private set { if (SetProperty(ref _approvalPreflightResult, value)) NotifyApprovalCommands(); } }
    public string ApprovalSearch { get => _approvalSearch; set { if (SetProperty(ref _approvalSearch, value)) ResetApprovalPage(); } }
    public DateTime? ApprovalFromDate { get => _approvalFromDate; set { if (SetProperty(ref _approvalFromDate, value)) ResetApprovalPage(); } }
    public DateTime? ApprovalToDate { get => _approvalToDate; set { if (SetProperty(ref _approvalToDate, value)) ResetApprovalPage(); } }
    public ApprovalChoice<MessageOperationState>? SelectedApprovalState { get => _selectedApprovalState; set { if (SetProperty(ref _selectedApprovalState, value)) ResetApprovalPage(); } }
    public ApprovalChoice<SnapshotCompatibility>? SelectedApprovalCompatibility { get => _selectedApprovalCompatibility; set { if (SetProperty(ref _selectedApprovalCompatibility, value)) ResetApprovalPage(); } }
    public ApprovalChoice<bool>? SelectedApprovalBroadMention { get => _selectedApprovalBroadMention; set { if (SetProperty(ref _selectedApprovalBroadMention, value)) ResetApprovalPage(); } }
    public ApprovalChoice<bool>? SelectedApprovalManualReview { get => _selectedApprovalManualReview; set { if (SetProperty(ref _selectedApprovalManualReview, value)) ResetApprovalPage(); } }
    public ApprovalChoice<ScheduledApprovalSort>? SelectedApprovalSort { get => _selectedApprovalSort; set { if (SetProperty(ref _selectedApprovalSort, value)) ResetApprovalPage(); } }
    public ApprovalChoice<Guid>? SelectedApprovalSchedule { get => _selectedApprovalSchedule; set { if (SetProperty(ref _selectedApprovalSchedule, value)) ResetApprovalPage(); } }
    public int ApprovalPageSize { get => _approvalPageSize; set { if (SetProperty(ref _approvalPageSize, value)) ResetApprovalPage(); } }
    public int ApprovalPageNumber { get => _approvalPageNumber; private set { if (SetProperty(ref _approvalPageNumber, value)) NotifyApprovalQueryProperties(); } }
    public int ApprovalTotalCount { get => _approvalTotalCount; private set { if (SetProperty(ref _approvalTotalCount, value)) NotifyApprovalQueryProperties(); } }
    public int ApprovalTotalPages => Math.Max(1, (int)Math.Ceiling(ApprovalTotalCount / (double)ApprovalPageSize));
    public bool CanGoToPreviousApprovalPage => !IsApprovalQueryLoading && ApprovalPageNumber > 1;
    public bool CanGoToNextApprovalPage => !IsApprovalQueryLoading && ApprovalPageNumber < ApprovalTotalPages;
    public bool IsApprovalQueryLoading { get => _isApprovalQueryLoading; private set { if (SetProperty(ref _isApprovalQueryLoading, value)) NotifyApprovalQueryProperties(); } }
    public string? ApprovalQueryMessage { get => _approvalQueryMessage; private set => SetProperty(ref _approvalQueryMessage, value); }
    public string ApprovalFilterSummary => BuildApprovalFilterSummary();
    public string ApprovalScopeSummary => _botProfileId is null || _serverId is null ? "Select a bot and server in the application toolbar to set the approval scope." : $"Current approval scope follows the application toolbar. Bot: selected saved profile; Server: {_snapshot?.Servers.FirstOrDefault(server => server.Id == _serverId)?.Name ?? "selected server"}.";
    public ScheduledApprovalListItem? SelectedHistory { get => _selectedHistory; set { if (SetProperty(ref _selectedHistory, value)) _ = LoadHistoryDetailsAsync(); } }
    public ScheduledMessageApproval? HistoryDetails { get => _historyDetails; private set => SetProperty(ref _historyDetails, value); }
    public bool IsHistoryDetailsLoading { get => _isHistoryDetailsLoading; private set => SetProperty(ref _isHistoryDetailsLoading, value); }
    public bool HistoryContentIsOpen { get => _historyContentIsOpen; private set { if (SetProperty(ref _historyContentIsOpen, value)) OpenHistoricalContentCommand.NotifyCanExecuteChanged(); } }
    public string HistoryManualReviewExplanation => HistoryDetails?.Occurrence.State == MessageOperationState.Uncertain ? "Manual review required. The application will not resend this occurrence automatically." : string.Empty;
    public string HistorySearch { get => _historySearch; set { if (SetProperty(ref _historySearch, value)) ResetHistoryPage(); } }
    public DateTime? HistoryFromDueDate { get => _historyFromDueDate; set { if (SetProperty(ref _historyFromDueDate, value)) ResetHistoryPage(); } }
    public DateTime? HistoryToDueDate { get => _historyToDueDate; set { if (SetProperty(ref _historyToDueDate, value)) ResetHistoryPage(); } }
    public DateTime? HistoryFromDecisionDate { get => _historyFromDecisionDate; set { if (SetProperty(ref _historyFromDecisionDate, value)) ResetHistoryPage(); } }
    public DateTime? HistoryToDecisionDate { get => _historyToDecisionDate; set { if (SetProperty(ref _historyToDecisionDate, value)) ResetHistoryPage(); } }
    public ApprovalChoice<Guid>? SelectedHistorySchedule { get => _selectedHistorySchedule; set { if (SetProperty(ref _selectedHistorySchedule, value)) ResetHistoryPage(); } }
    public ApprovalChoice<MessageOperationState>? SelectedHistoryState { get => _selectedHistoryState; set { if (SetProperty(ref _selectedHistoryState, value)) ResetHistoryPage(); } }
    public ApprovalChoice<SnapshotCompatibility>? SelectedHistoryCompatibility { get => _selectedHistoryCompatibility; set { if (SetProperty(ref _selectedHistoryCompatibility, value)) ResetHistoryPage(); } }
    public ApprovalChoice<bool>? SelectedHistoryBroadMention { get => _selectedHistoryBroadMention; set { if (SetProperty(ref _selectedHistoryBroadMention, value)) ResetHistoryPage(); } }
    public ApprovalChoice<bool>? SelectedHistoryManualReview { get => _selectedHistoryManualReview; set { if (SetProperty(ref _selectedHistoryManualReview, value)) ResetHistoryPage(); } }
    public ApprovalChoice<ScheduledApprovalSort>? SelectedHistorySort { get => _selectedHistorySort; set { if (SetProperty(ref _selectedHistorySort, value)) ResetHistoryPage(); } }
    public int HistoryPageSize { get => _historyPageSize; set { if (SetProperty(ref _historyPageSize, value)) ResetHistoryPage(); } }
    public int HistoryPageNumber { get => _historyPageNumber; private set { if (SetProperty(ref _historyPageNumber, value)) NotifyHistoryQueryProperties(); } }
    public int HistoryTotalCount { get => _historyTotalCount; private set { if (SetProperty(ref _historyTotalCount, value)) NotifyHistoryQueryProperties(); } }
    public int HistoryTotalPages => Math.Max(1, (int)Math.Ceiling(HistoryTotalCount / (double)HistoryPageSize));
    public bool IsHistoryQueryLoading { get => _isHistoryQueryLoading; private set { if (SetProperty(ref _isHistoryQueryLoading, value)) NotifyHistoryQueryProperties(); } }
    public bool CanGoToPreviousHistoryPage => !IsHistoryQueryLoading && HistoryPageNumber > 1;
    public bool CanGoToNextHistoryPage => !IsHistoryQueryLoading && HistoryPageNumber < HistoryTotalPages;
    public string? HistoryQueryMessage { get => _historyQueryMessage; private set => SetProperty(ref _historyQueryMessage, value); }
    public string HistoryFilterSummary => string.IsNullOrWhiteSpace(HistorySearch) ? "No active history filters." : "Active history filters: search.";
    public string ApprovalPreflightSummary => IsApprovalPreflightLoading ? "Refreshing current Discord status…" : ApprovalPreflightResult?.Summary ?? "Current Discord status has not been checked.";
    public string ApprovalCheckedAt => ApprovalPreflightResult is null ? "Not checked" : $"Last checked {ApprovalPreflightResult.CheckedAt.LocalDateTime:g}";
    public bool CanApproveApproval => !IsApprovalBusy && !IsApprovalDetailsLoading && !IsApprovalPreflightLoading && ApprovalDetails?.Occurrence.State == MessageOperationState.PendingApproval && ApprovalPreflightResult?.CanSend == true;
    public bool CanDecidePending => !IsApprovalBusy && !IsApprovalDetailsLoading && ApprovalDetails?.Occurrence.State == MessageOperationState.PendingApproval;
    public bool CanRefreshApprovalStatus => !IsApprovalBusy && !IsApprovalDetailsLoading && !IsApprovalPreflightLoading && ApprovalDetails is not null;
    public string ApprovalDisabledReason => GetApprovalDisabledReason();
    public string AllowedMentionSummary => ApprovalDetails?.ImmutableContent is not { } content ? "No immutable mention policy is available." : content.AllowedMentions.AllowEveryoneAndHere ? "Everyone and here mentions are allowed; stronger confirmation is required." : content.AllowedMentions.AllowRoleMentions ? "Role mentions are allowed for the saved target IDs." : content.AllowedMentions.AllowedUserIds.Length > 0 ? "Only the saved user mention IDs are allowed." : "Everyone, here, role, and user mentions are blocked.";
    public string CharacterUsage => $"{MessageBody.Length:N0} / {MessageLimits.MaximumMessageCharacters:N0}";
    public bool HasContext => _botProfileId is not null && _serverId is not null && _connectionState == BotConnectionState.Connected;
    public bool CanSaveTemplate => !string.IsNullOrWhiteSpace(TemplateName) && (MessageBody.Length > 0 || IncludeEmbed);
    public string ContextMessage => !HasContext
        ? "Select a connected bot and server before composing a delivery. Templates can still be saved locally."
        : "Messages are sent only after a preview and explicit confirmation. Broad mentions are disabled by default.";

    public RelayCommand GeneratePreviewCommand { get; }
    public AsyncRelayCommand SaveTemplateCommand { get; }
    public AsyncRelayCommand RefreshTemplatesCommand { get; }
    public AsyncRelayCommand SendCommand { get; }
    public AsyncRelayCommand RefreshApprovalsCommand { get; }
    public AsyncRelayCommand SkipApprovalCommand { get; }
    public AsyncRelayCommand ArchiveApprovalCommand { get; }
    public AsyncRelayCommand ApproveApprovalCommand { get; }
    public AsyncRelayCommand RefreshApprovalStatusCommand { get; }
    public AsyncRelayCommand ApplyApprovalFiltersCommand { get; }
    public AsyncRelayCommand ClearApprovalFiltersCommand { get; }
    public AsyncRelayCommand PreviousApprovalPageCommand { get; }
    public AsyncRelayCommand NextApprovalPageCommand { get; }
    public AsyncRelayCommand FirstApprovalPageCommand { get; }
    public AsyncRelayCommand LastApprovalPageCommand { get; }
    public RelayCommand OpenHistoricalContentCommand { get; }
    public AsyncRelayCommand RefreshHistoryCommand { get; }
    public AsyncRelayCommand ApplyHistoryFiltersCommand { get; }
    public AsyncRelayCommand ClearHistoryFiltersCommand { get; }
    public AsyncRelayCommand PreviousHistoryPageCommand { get; }
    public AsyncRelayCommand NextHistoryPageCommand { get; }
    public AsyncRelayCommand FirstHistoryPageCommand { get; }
    public AsyncRelayCommand LastHistoryPageCommand { get; }

    private void ResetApprovalPage()
    {
        if (ApprovalPageNumber != 1) ApprovalPageNumber = 1;
        OnPropertyChanged(nameof(ApprovalFilterSummary));
    }

    public void SetContext(Guid? botProfileId, BotConnectionState connectionState, ulong? serverId)
    {
        _botProfileId = botProfileId;
        _connectionState = connectionState;
        _serverId = serverId;
        _snapshot = botProfileId is Guid id ? _explorer.GetSnapshot(id) : null;
        ApplySnapshot();
        OnPropertyChanged(nameof(HasContext));
        OnPropertyChanged(nameof(ContextMessage));
        OnPropertyChanged(nameof(ApprovalScopeSummary));
        _ = LoadApprovalSchedulesAsync(CancellationToken.None);
    }

    public void SetConnectionState(BotConnectionState state)
    {
        _connectionState = state;
        OnPropertyChanged(nameof(HasContext));
        OnPropertyChanged(nameof(ContextMessage));
        OnPropertyChanged(nameof(ApprovalScopeSummary));
    }

    public void SetServer(ulong? serverId)
    {
        _serverId = serverId;
        ApplySnapshot();
        OnPropertyChanged(nameof(HasContext));
        OnPropertyChanged(nameof(ContextMessage));
        _ = LoadApprovalSchedulesAsync(CancellationToken.None);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken) { await LoadTemplatesAsync(cancellationToken).ConfigureAwait(false); await LoadApprovalSchedulesAsync(cancellationToken).ConfigureAwait(false); await LoadApprovalsAsync(cancellationToken).ConfigureAwait(false); await LoadHistoryAsync(cancellationToken).ConfigureAwait(false); }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _explorer.CacheChanged -= OnCacheChanged;
        SaveTemplateCommand.Dispose();
        RefreshTemplatesCommand.Dispose();
        SendCommand.Dispose();
        RefreshApprovalsCommand.Dispose(); SkipApprovalCommand.Dispose(); ArchiveApprovalCommand.Dispose();
        ApproveApprovalCommand.Dispose(); RefreshApprovalStatusCommand.Dispose();
        ApplyApprovalFiltersCommand.Dispose(); ClearApprovalFiltersCommand.Dispose(); PreviousApprovalPageCommand.Dispose();
        NextApprovalPageCommand.Dispose(); FirstApprovalPageCommand.Dispose(); LastApprovalPageCommand.Dispose();
        RefreshHistoryCommand.Dispose(); ApplyHistoryFiltersCommand.Dispose(); ClearHistoryFiltersCommand.Dispose(); PreviousHistoryPageCommand.Dispose(); NextHistoryPageCommand.Dispose(); FirstHistoryPageCommand.Dispose(); LastHistoryPageCommand.Dispose();
    }

    private void GeneratePreview()
    {
        ValidationErrors.Clear();
        PreviewWarnings.Clear();
        if (!TryBuildDraft(out var draft)) return;
        var result = _planner.Build(draft, DestinationMode == MessageDestinationKind.ServerChannel ? MessageOperationKind.ManualChannelMessage : MessageOperationKind.IndividualDirectMessage);
        foreach (var error in result.Errors) ValidationErrors.Add(error);
        if (result.Plan is null) { Preview = null; return; }
        Preview = _planner.BuildPreview(result.Plan, "Selected bot");
        foreach (var warning in Preview.Warnings) PreviewWarnings.Add(warning);
        StatusMessage = "Preview generated. Sending remains blocked until a dedicated confirmation dialog is approved.";
        SendCommand.NotifyCanExecuteChanged();
    }

    private async Task SaveTemplateAsync(CancellationToken cancellationToken)
    {
        ValidationErrors.Clear();
        var content = BuildContent();
        foreach (var error in MessageLimits.Validate(content)) ValidationErrors.Add(error);
        if (ValidationErrors.Count > 0 || string.IsNullOrWhiteSpace(TemplateName)) return;
        var now = DateTimeOffset.UtcNow;
        var template = new MessageTemplate(
            Guid.NewGuid(), TemplateName.Trim(), null, content,
            ImmutableArray<TemplateVariableDefinition>.Empty,
            ImmutableArray<string>.Empty, now, now, null);
        await _templates.SaveAsync(template, cancellationToken).ConfigureAwait(false);
        await LoadTemplatesAsync(cancellationToken).ConfigureAwait(false);
        StatusMessage = "Template saved locally. Template content is retained only because it was explicitly saved.";
    }

    private async Task SendAsync(CancellationToken cancellationToken)
    {
        ValidationErrors.Clear();
        if (!TryBuildDraft(out var draft))
        {
            return;
        }

        var kind = DestinationMode == MessageDestinationKind.ServerChannel
            ? MessageOperationKind.ManualChannelMessage
            : MessageOperationKind.IndividualDirectMessage;
        var result = await _deliveryDialog
            .PreviewConfirmAndDeliverAsync(draft, kind, "Selected bot", cancellationToken)
            .ConfigureAwait(true);
        StatusMessage = result?.State switch
        {
            MessageOperationState.Delivered => "Message delivery was confirmed by Discord.",
            MessageOperationState.Uncertain => "Discord did not confirm delivery. The message was not repeated automatically.",
            MessageOperationState.Failed => result.Failure?.SafeMessage ?? "Message delivery failed safely.",
            _ => "Message delivery was cancelled before a Discord request was made."
        };
    }

    private async Task LoadTemplatesAsync(CancellationToken cancellationToken)
    {
        var templates = await _templates.SearchAsync(null, cancellationToken).ConfigureAwait(false);
        _dispatcher.Post(() => { Templates.Clear(); foreach (var template in templates) Templates.Add(template); });
    }

    private async Task LoadApprovalsAsync(CancellationToken cancellationToken)
    {
        var version = Interlocked.Increment(ref _approvalQueryVersion);
        if (_botProfileId is null || _serverId is null)
        {
            Approvals.Clear(); ApprovalTotalCount = 0; ApprovalQueryMessage = "Select a bot and server to view pending approvals."; return;
        }
        if (ApprovalFromDate is { } from && ApprovalToDate is { } to && from.Date > to.Date)
        {
            ApprovalQueryMessage = "The from date must be on or before the to date.";
            return;
        }

        IsApprovalQueryLoading = true;
        ApprovalQueryMessage = null;
        try
        {
            var queueTask = _scheduledMessages.QueryApprovalsAsync(BuildApprovalQuery(), cancellationToken);
            await queueTask.ConfigureAwait(false);
            if (_disposed || version != Volatile.Read(ref _approvalQueryVersion)) return;
            var page = await queueTask.ConfigureAwait(false);
            var nearestPage = Math.Max(1, (int)Math.Ceiling(page.TotalCount / (double)ApprovalPageSize));
            if (page.Items.Count == 0 && page.TotalCount > 0 && page.PageNumber > nearestPage)
            {
                ApprovalPageNumber = nearestPage;
                page = await _scheduledMessages.QueryApprovalsAsync(BuildApprovalQuery(), cancellationToken).ConfigureAwait(false);
                ApprovalQueryMessage = "The current page became empty after a decision, so the nearest available page is shown.";
            }
            _dispatcher.Post(() =>
            {
                if (_disposed || version != Volatile.Read(ref _approvalQueryVersion)) return;
                var selectedId = SelectedApproval?.OccurrenceId;
                Approvals.Clear(); foreach (var item in page.Items) Approvals.Add(item);
                ApprovalTotalCount = page.TotalCount;
                ApprovalPageNumber = page.PageNumber;
                SelectedApproval = Approvals.FirstOrDefault(item => item.OccurrenceId == selectedId) ?? Approvals.FirstOrDefault();
                ApprovalQueryMessage = page.TotalCount == 0 ? (SelectedApprovalState?.Value == MessageOperationState.PendingApproval ? "No pending approvals match the current filters. Clear filters or refresh the queue." : "No results match the current filters. Adjust or clear the filters.") : null;
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception) { if (!_disposed && version == Volatile.Read(ref _approvalQueryVersion)) ApprovalQueryMessage = "The approval query could not be completed. Refresh and try again."; }
        finally { if (!_disposed && version == Volatile.Read(ref _approvalQueryVersion)) IsApprovalQueryLoading = false; }
    }

    private ScheduledApprovalQuery BuildApprovalQuery() => new(
        ApprovalSearch, _botProfileId, _serverId, SelectedApprovalSchedule is { IsAny: false } schedule ? schedule.Value : null, SelectedApprovalState is not { IsAny: false } selectedState ? null : selectedState.Value,
        ApprovalFromDate is { } from ? new DateTimeOffset(from.Date, TimeZoneInfo.Local.GetUtcOffset(from.Date)) : null,
        ApprovalToDate is { } to ? new DateTimeOffset(to.Date.AddDays(1).AddTicks(-1), TimeZoneInfo.Local.GetUtcOffset(to.Date)) : null,
        SelectedApprovalCompatibility is { IsAny: false } compatibility ? compatibility.Value : null, SelectedApprovalSort?.Value ?? ScheduledApprovalSort.DueAscending, ApprovalPageNumber, ApprovalPageSize)
    {
        HasBroadMention = SelectedApprovalBroadMention is { IsAny: false } broadMention ? broadMention.Value : null,
        RequiresManualReview = SelectedApprovalManualReview is { IsAny: false } manualReview ? manualReview.Value : null,
        HistoryOnly = false
    };

    private async Task ApplyApprovalFiltersAsync(CancellationToken cancellationToken)
    {
        ApprovalPageNumber = 1;
        await LoadApprovalsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ClearApprovalFiltersAsync(CancellationToken cancellationToken)
    {
        ApprovalSearch = string.Empty; ApprovalFromDate = null; ApprovalToDate = null;
        SelectedApprovalState = ApprovalStates.First(choice => choice.Value == MessageOperationState.PendingApproval);
        SelectedApprovalSchedule = ApprovalSchedules.FirstOrDefault();
        SelectedApprovalCompatibility = null; SelectedApprovalBroadMention = null; SelectedApprovalManualReview = null;
        SelectedApprovalSort = ApprovalSorts.First(choice => choice.Value == ScheduledApprovalSort.DueAscending);
        ApprovalPageSize = 25; ApprovalPageNumber = 1;
        await LoadApprovalsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task NavigateApprovalPageAsync(int page, CancellationToken cancellationToken)
    {
        if (page < 1 || page > ApprovalTotalPages || IsApprovalQueryLoading) return;
        ApprovalPageNumber = page;
        await LoadApprovalsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task LoadApprovalSchedulesAsync(CancellationToken cancellationToken)
    {
        var version = Interlocked.Increment(ref _approvalScheduleVersion);
        if (_botProfileId is null || _serverId is null)
        {
            ApprovalSchedules.Clear(); SelectedApprovalSchedule = null; SelectedHistorySchedule = null;
            return;
        }
        try
        {
            var schedules = await _scheduledMessages.ListApprovalSchedulesAsync(_botProfileId, _serverId, cancellationToken).ConfigureAwait(false);
            if (_disposed || version != Volatile.Read(ref _approvalScheduleVersion)) return;
            _dispatcher.Post(() =>
            {
                if (_disposed || version != Volatile.Read(ref _approvalScheduleVersion)) return;
                var queueSelection = SelectedApprovalSchedule?.Value;
                var historySelection = SelectedHistorySchedule?.Value;
                ApprovalSchedules.Clear(); ApprovalSchedules.Add(new("All schedules", Guid.Empty, true));
                foreach (var schedule in schedules) ApprovalSchedules.Add(new(schedule.DisplayName, schedule.ScheduleId));
                SelectedApprovalSchedule = ApprovalSchedules.FirstOrDefault(item => item.Value == queueSelection) ?? ApprovalSchedules[0];
                SelectedHistorySchedule = ApprovalSchedules.FirstOrDefault(item => item.Value == historySelection) ?? ApprovalSchedules[0];
            });
        }
        catch { if (!_disposed && version == Volatile.Read(ref _approvalScheduleVersion)) ApprovalQueryMessage = "Schedules could not be loaded for the current scope."; }
    }

    private async Task LoadHistoryAsync(CancellationToken cancellationToken)
    {
        var version = Interlocked.Increment(ref _historyQueryVersion);
        if (_botProfileId is null || _serverId is null)
        {
            ApprovalHistory.Clear(); HistoryQueryMessage = "Select a bot and server to view terminal approval history."; return;
        }
        if ((HistoryFromDueDate is { } fromDue && HistoryToDueDate is { } toDue && fromDue.Date > toDue.Date) || (HistoryFromDecisionDate is { } fromDecision && HistoryToDecisionDate is { } toDecision && fromDecision.Date > toDecision.Date))
        {
            HistoryQueryMessage = "Each history date range must start on or before its end date."; return;
        }
        IsHistoryQueryLoading = true; HistoryQueryMessage = null;
        try
        {
            var page = await _scheduledMessages.QueryApprovalsAsync(BuildHistoryQuery(), cancellationToken).ConfigureAwait(false);
            if (_disposed || version != Volatile.Read(ref _historyQueryVersion)) return;
            _dispatcher.Post(() =>
            {
                if (_disposed || version != Volatile.Read(ref _historyQueryVersion)) return;
                var selectedId = SelectedHistory?.OccurrenceId;
                ApprovalHistory.Clear(); foreach (var item in page.Items) ApprovalHistory.Add(item);
                HistoryTotalCount = page.TotalCount; HistoryPageNumber = page.PageNumber;
                SelectedHistory = ApprovalHistory.FirstOrDefault(item => item.OccurrenceId == selectedId) ?? ApprovalHistory.FirstOrDefault();
                HistoryQueryMessage = page.TotalCount == 0 ? "No terminal history matches the current filters. Adjust filters or wait for a decision." : null;
            });
        }
        catch { if (!_disposed && version == Volatile.Read(ref _historyQueryVersion)) HistoryQueryMessage = "Terminal history could not be loaded. Refresh and try again."; }
        finally { if (!_disposed && version == Volatile.Read(ref _historyQueryVersion)) IsHistoryQueryLoading = false; }
    }

    private ScheduledApprovalQuery BuildHistoryQuery() => new(
        HistorySearch, _botProfileId, _serverId, SelectedHistorySchedule is { IsAny: false } schedule ? schedule.Value : null, SelectedHistoryState is { IsAny: false } state ? state.Value : null,
        HistoryFromDueDate is { } from ? LocalDayStart(from) : null, HistoryToDueDate is { } to ? LocalDayEnd(to) : null,
        SelectedHistoryCompatibility is { IsAny: false } compatibility ? compatibility.Value : null, SelectedHistorySort?.Value ?? ScheduledApprovalSort.DecisionNewest, HistoryPageNumber, HistoryPageSize)
    {
        HasBroadMention = SelectedHistoryBroadMention is { IsAny: false } broad ? broad.Value : null,
        RequiresManualReview = SelectedHistoryManualReview is { IsAny: false } manual ? manual.Value : null,
        HistoryOnly = true,
        FromDecision = HistoryFromDecisionDate is { } fromDecision ? LocalDayStart(fromDecision) : null,
        ToDecision = HistoryToDecisionDate is { } toDecision ? LocalDayEnd(toDecision) : null
    };

    private static DateTimeOffset LocalDayStart(DateTime date) => new(date.Date, TimeZoneInfo.Local.GetUtcOffset(date));
    private static DateTimeOffset LocalDayEnd(DateTime date) => new(date.Date.AddDays(1).AddTicks(-1), TimeZoneInfo.Local.GetUtcOffset(date));

    private async Task ApplyHistoryFiltersAsync(CancellationToken cancellationToken) { HistoryPageNumber = 1; await LoadHistoryAsync(cancellationToken).ConfigureAwait(false); }
    private async Task ClearHistoryFiltersAsync(CancellationToken cancellationToken)
    {
        HistorySearch = string.Empty; HistoryFromDueDate = null; HistoryToDueDate = null; HistoryFromDecisionDate = null; HistoryToDecisionDate = null;
        SelectedHistorySchedule = ApprovalSchedules.FirstOrDefault(); SelectedHistoryState = ApprovalStates.First(choice => choice.IsAny); SelectedHistoryCompatibility = null; SelectedHistoryBroadMention = null; SelectedHistoryManualReview = null; SelectedHistorySort = HistorySorts.First(choice => choice.Value == ScheduledApprovalSort.DecisionNewest); HistoryPageSize = 25; HistoryPageNumber = 1;
        await LoadHistoryAsync(cancellationToken).ConfigureAwait(false);
    }
    private async Task NavigateHistoryPageAsync(int page, CancellationToken cancellationToken) { if (page < 1 || page > HistoryTotalPages || IsHistoryQueryLoading) return; HistoryPageNumber = page; await LoadHistoryAsync(cancellationToken).ConfigureAwait(false); }
    private void ResetHistoryPage() { if (HistoryPageNumber != 1) HistoryPageNumber = 1; OnPropertyChanged(nameof(HistoryFilterSummary)); }
    private void NotifyHistoryQueryProperties()
    {
        OnPropertyChanged(nameof(HistoryTotalPages)); OnPropertyChanged(nameof(CanGoToPreviousHistoryPage)); OnPropertyChanged(nameof(CanGoToNextHistoryPage)); OnPropertyChanged(nameof(HistoryFilterSummary));
        RefreshHistoryCommand.NotifyCanExecuteChanged(); ApplyHistoryFiltersCommand.NotifyCanExecuteChanged(); ClearHistoryFiltersCommand.NotifyCanExecuteChanged(); PreviousHistoryPageCommand.NotifyCanExecuteChanged(); NextHistoryPageCommand.NotifyCanExecuteChanged(); FirstHistoryPageCommand.NotifyCanExecuteChanged(); LastHistoryPageCommand.NotifyCanExecuteChanged();
    }

    private async Task LoadHistoryDetailsAsync()
    {
        var selection = SelectedHistory;
        var version = Interlocked.Increment(ref _historyDetailVersion);
        HistoryDetails = null; HistoryContentIsOpen = false;
        IsHistoryDetailsLoading = selection is not null;
        if (selection is null) return;
        try
        {
            var detail = await _scheduledMessages.GetApprovalAsync(selection.OccurrenceId, CancellationToken.None).ConfigureAwait(false);
            if (_disposed || version != Volatile.Read(ref _historyDetailVersion)) return;
            _dispatcher.Post(() =>
            {
                if (_disposed || version != Volatile.Read(ref _historyDetailVersion)) return;
                HistoryDetails = detail; IsHistoryDetailsLoading = false;
                OnPropertyChanged(nameof(HistoryManualReviewExplanation));
                OpenHistoricalContentCommand.NotifyCanExecuteChanged();
            });
        }
        catch { if (!_disposed && version == Volatile.Read(ref _historyDetailVersion)) IsHistoryDetailsLoading = false; }
    }

    private string BuildApprovalFilterSummary()
    {
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(ApprovalSearch)) filters.Add("search");
        if (SelectedApprovalState is { IsAny: false } selectedState) filters.Add(selectedState.Value == MessageOperationState.PendingApproval ? "pending" : selectedState.Label);
        if (ApprovalFromDate is not null || ApprovalToDate is not null) filters.Add("due date");
        if (SelectedApprovalCompatibility is { IsAny: false }) filters.Add(SelectedApprovalCompatibility.Label);
        if (SelectedApprovalBroadMention is { IsAny: false }) filters.Add("broad mentions");
        if (SelectedApprovalManualReview is { IsAny: false }) filters.Add("manual review");
        return filters.Count == 0 ? "No active filters." : $"Active filters: {string.Join(", ", filters)}.";
    }

    private void NotifyApprovalQueryProperties()
    {
        OnPropertyChanged(nameof(ApprovalTotalPages)); OnPropertyChanged(nameof(CanGoToPreviousApprovalPage)); OnPropertyChanged(nameof(CanGoToNextApprovalPage)); OnPropertyChanged(nameof(ApprovalFilterSummary));
        ApplyApprovalFiltersCommand.NotifyCanExecuteChanged(); ClearApprovalFiltersCommand.NotifyCanExecuteChanged(); PreviousApprovalPageCommand.NotifyCanExecuteChanged(); NextApprovalPageCommand.NotifyCanExecuteChanged(); FirstApprovalPageCommand.NotifyCanExecuteChanged(); LastApprovalPageCommand.NotifyCanExecuteChanged();
    }

    private async Task SkipApprovalAsync(CancellationToken cancellationToken)
    {
        var approval = ApprovalDetails;
        if (approval is null || !CanDecidePending || !ConfirmDecision("Skip occurrence", approval)) return;
        IsApprovalBusy = true;
        try
        {
            if (await _approvals.SkipAsync(approval.Occurrence.Id, cancellationToken).ConfigureAwait(false))
            {
                StatusMessage = "The missed occurrence was skipped and was not sent.";
                await LoadApprovalsAsync(cancellationToken).ConfigureAwait(false);
                await LoadHistoryAsync(cancellationToken).ConfigureAwait(false);
            }
            else StatusMessage = "This occurrence was already processed.";
        }
        finally { IsApprovalBusy = false; }
    }

    private async Task ArchiveApprovalAsync(CancellationToken cancellationToken)
    {
        var approval = ApprovalDetails;
        if (approval is null || !CanDecidePending || !ConfirmDecision("Archive occurrence", approval)) return;
        IsApprovalBusy = true;
        try
        {
            if (await _approvals.ArchiveAsync(approval.Occurrence.Id, cancellationToken).ConfigureAwait(false))
            {
                StatusMessage = "The missed occurrence was archived and was not sent.";
                await LoadApprovalsAsync(cancellationToken).ConfigureAwait(false);
                await LoadHistoryAsync(cancellationToken).ConfigureAwait(false);
            }
            else StatusMessage = "This occurrence was already processed.";
        }
        finally { IsApprovalBusy = false; }
    }

    private async Task ApproveApprovalAsync(CancellationToken cancellationToken)
    {
        var approval = ApprovalDetails;
        if (approval is null || !CanApproveApproval) return;
        var occurrenceId = approval.Occurrence.Id;
        if (!await RefreshApprovalPreflightAsync(approval, cancellationToken).ConfigureAwait(true)
            || !ReferenceEquals(approval, ApprovalDetails)
            || approval.Occurrence.State != MessageOperationState.PendingApproval
            || ApprovalPreflightResult?.CanSend != true)
        {
            StatusMessage = "Current Discord status blocks approval. Resolve the shown check before sending.";
            return;
        }

        if (!ConfirmDecision("Approve and send", approval)) return;
        IsApprovalBusy = true;
        try
        {
            var result = await _approvals.ApproveAsync(occurrenceId, cancellationToken).ConfigureAwait(false);
            StatusMessage = result?.State switch
            {
                MessageOperationState.Delivered => "The approved missed message was delivered.",
                MessageOperationState.Uncertain => "The delivery result is uncertain. This occurrence will not be sent again automatically. Manual review is required.",
                MessageOperationState.Failed => result.Failure?.SafeMessage ?? "The approved message could not be sent safely.",
                _ => "This occurrence was already processed or could not be sent safely."
            };
            await LoadApprovalsAsync(cancellationToken).ConfigureAwait(false);
            await LoadHistoryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { IsApprovalBusy = false; }
    }

    private async Task LoadApprovalDetailsAsync()
    {
        var selection = SelectedApproval;
        var version = Interlocked.Increment(ref _approvalSelectionVersion);
        Interlocked.Increment(ref _approvalPreflightVersion);
        ApprovalDetails = null;
        ApprovalPreflightResult = null;
        ClearApprovalPresentation();
        IsApprovalDetailsLoading = selection is not null;
        NotifyApprovalProperties();
        if (selection is null) return;
        try
        {
            var details = await _scheduledMessages.GetApprovalAsync(selection.OccurrenceId, CancellationToken.None).ConfigureAwait(false);
            if (_disposed || version != Volatile.Read(ref _approvalSelectionVersion)) return;
            _dispatcher.Post(() =>
            {
                if (_disposed || version != Volatile.Read(ref _approvalSelectionVersion)) return;
                IsApprovalDetailsLoading = false;
                if (details is null)
                {
                    StatusMessage = "This occurrence is no longer available for a decision.";
                    NotifyApprovalProperties();
                    return;
                }

                ApprovalDetails = details;
                PopulateImmutablePresentation(details);
                NotifyApprovalProperties();
                _ = RefreshApprovalPreflightAsync(details, CancellationToken.None);
            });
        }
        catch
        {
            if (!_disposed && version == Volatile.Read(ref _approvalSelectionVersion)) _dispatcher.Post(() =>
            {
                IsApprovalDetailsLoading = false;
                StatusMessage = "Approval details could not be loaded.";
                NotifyApprovalProperties();
            });
        }
    }

    private Task RefreshApprovalStatusAsync(CancellationToken cancellationToken) =>
        ApprovalDetails is { } approval ? RefreshApprovalPreflightAsync(approval, cancellationToken) : Task.CompletedTask;

    private async Task<bool> RefreshApprovalPreflightAsync(ScheduledMessageApproval approval, CancellationToken cancellationToken)
    {
        if (_disposed || !ReferenceEquals(approval, ApprovalDetails)) return false;
        var selectionVersion = Volatile.Read(ref _approvalSelectionVersion);
        var refreshVersion = Interlocked.Increment(ref _approvalPreflightVersion);
        IsApprovalPreflightLoading = true;
        NotifyApprovalProperties();
        try
        {
            var result = await _approvalPreflight.EvaluateAsync(approval, cancellationToken).ConfigureAwait(false);
            if (_disposed || selectionVersion != Volatile.Read(ref _approvalSelectionVersion) || refreshVersion != Volatile.Read(ref _approvalPreflightVersion) || !ReferenceEquals(approval, ApprovalDetails)) return false;
            _dispatcher.Post(() =>
            {
                if (_disposed || selectionVersion != Volatile.Read(ref _approvalSelectionVersion) || refreshVersion != Volatile.Read(ref _approvalPreflightVersion) || !ReferenceEquals(approval, ApprovalDetails)) return;
                ApprovalPreflightResult = result;
                ApprovalPreflightChecks.Clear();
                foreach (var check in result.Checks) ApprovalPreflightChecks.Add(new ScheduledApprovalPreflightCheckItem(check));
                IsApprovalPreflightLoading = false;
                NotifyApprovalProperties();
            });
            return result.CanSend;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!_disposed && refreshVersion == Volatile.Read(ref _approvalPreflightVersion)) _dispatcher.Post(() => { IsApprovalPreflightLoading = false; StatusMessage = "Current Discord status refresh was cancelled."; NotifyApprovalProperties(); });
            return false;
        }
        catch
        {
            if (!_disposed && refreshVersion == Volatile.Read(ref _approvalPreflightVersion)) _dispatcher.Post(() => { IsApprovalPreflightLoading = false; StatusMessage = "Current Discord status could not be checked."; NotifyApprovalProperties(); });
            return false;
        }
    }

    private void PopulateImmutablePresentation(ScheduledMessageApproval approval)
    {
        ClearApprovalPresentation();
        var usage = _approvalPreflight.GetUsage(approval.ImmutableContent);
        foreach (var row in usage.PlainMessageRows) ApprovalPlainMessageUsage.Add(new ContentUsageItem(row));
        foreach (var row in usage.EmbedRows) ApprovalEmbedUsage.Add(new ContentUsageItem(row));
        foreach (var row in _approvalPreflight.GetMentionPolicyUsage(approval.ImmutableContent)) ApprovalMentionPolicy.Add(new MentionPolicyUsageItem(row));
        foreach (var warning in usage.ValidationWarnings) ApprovalUsageWarnings.Add(warning);
    }

    private void ClearApprovalPresentation() { ApprovalPreflightChecks.Clear(); ApprovalPlainMessageUsage.Clear(); ApprovalEmbedUsage.Clear(); ApprovalMentionPolicy.Clear(); ApprovalUsageWarnings.Clear(); }

    private void NotifyApprovalCommands()
    {
        SkipApprovalCommand.NotifyCanExecuteChanged(); ArchiveApprovalCommand.NotifyCanExecuteChanged(); ApproveApprovalCommand.NotifyCanExecuteChanged(); RefreshApprovalStatusCommand.NotifyCanExecuteChanged();
        NotifyApprovalProperties();
    }

    private void NotifyApprovalProperties()
    {
        OnPropertyChanged(nameof(CanApproveApproval)); OnPropertyChanged(nameof(CanDecidePending)); OnPropertyChanged(nameof(CanRefreshApprovalStatus)); OnPropertyChanged(nameof(ApprovalDisabledReason)); OnPropertyChanged(nameof(ApprovalPreflightSummary)); OnPropertyChanged(nameof(ApprovalCheckedAt)); OnPropertyChanged(nameof(AllowedMentionSummary));
    }

    private string GetApprovalDisabledReason()
    {
        if (SelectedApproval is null) return "Select a pending occurrence.";
        if (IsApprovalDetailsLoading) return "Loading immutable occurrence details…";
        if (IsApprovalBusy) return "Another decision is currently running.";
        if (ApprovalDetails?.Occurrence.State != MessageOperationState.PendingApproval) return "This occurrence is no longer pending approval.";
        if (ApprovalPreflightResult is null) return IsApprovalPreflightLoading ? "Refreshing current Discord status…" : "Refresh current Discord status before sending.";
        var priority = new[] { ScheduledApprovalPreflightCheckId.SnapshotCompatibility, ScheduledApprovalPreflightCheckId.PlainMessageLimits, ScheduledApprovalPreflightCheckId.EmbedLimits, ScheduledApprovalPreflightCheckId.AllowedMentionPolicy, ScheduledApprovalPreflightCheckId.BotProfileExists, ScheduledApprovalPreflightCheckId.BotConnected, ScheduledApprovalPreflightCheckId.ServerAccessible, ScheduledApprovalPreflightCheckId.ChannelExists, ScheduledApprovalPreflightCheckId.ChannelSupportsMessageSending, ScheduledApprovalPreflightCheckId.ViewChannel, ScheduledApprovalPreflightCheckId.SendMessages, ScheduledApprovalPreflightCheckId.EmbedLinks, ScheduledApprovalPreflightCheckId.AttachFiles, ScheduledApprovalPreflightCheckId.MentionEveryone };
        foreach (var id in priority)
        {
            var check = ApprovalPreflightResult.Checks.FirstOrDefault(item => item.Id == id && item.BlocksApproval);
            if (check is not null) return check.Remediation ?? check.Explanation;
        }
        return string.Empty;
    }

    private static bool ConfirmDecision(string action, ScheduledMessageApproval approval) =>
        new ScheduledApprovalDecisionWindow(new ScheduledApprovalDecisionViewModel(action, approval)) { Owner = System.Windows.Application.Current.MainWindow }.ShowDialog() == true;

#if false
    private void RefreshApprovalPreflightLegacy()
    {
        ApprovalPreflightChecks.Clear();
        if (ApprovalDetails?.ImmutableContent is null) { ApprovalPreflightSummary = "Current status cannot be checked until immutable details are available."; return; }
        var draft = new MessageDraft(Guid.NewGuid(), ApprovalDetails.Snapshot.BotProfileId, ApprovalDetails.Snapshot.Destination, ApprovalDetails.ImmutableContent, ImmutableArray<MessageAttachmentReference>.Empty, null, DateTimeOffset.UtcNow);
        var plan = _planner.Build(draft, MessageOperationKind.ScheduledChannelMessage).Plan;
        var check = plan is null ? null : _messagePreflight.Validate(plan);
        _approvalPreflightAllowsSend = check?.IsAllowed == true && ApprovalDetails.Compatibility is SnapshotCompatibility.Supported or SnapshotCompatibility.SupportedLegacy;
        ApprovalPreflightSummary = check is null ? "The immutable message no longer meets delivery limits." : check.IsAllowed ? "Allowed — current Discord checks passed." : string.Join(" ", check.Issues.Select(issue => issue.Message));
        ApprovalPreflightChecks.Add(ApprovalDetails.Compatibility is SnapshotCompatibility.Supported or SnapshotCompatibility.SupportedLegacy ? "Snapshot compatibility — Allowed" : "Snapshot compatibility — Blocked");
        foreach (var issue in check?.Issues ?? []) ApprovalPreflightChecks.Add($"Blocked — {issue.Message}");
        if (check?.IsAllowed == true) { ApprovalPreflightChecks.Add("Bot connection, destination, and required permissions — Allowed"); ApprovalPreflightChecks.Add(ApprovalDetails.ImmutableContent.Embed is null ? "Embed Links — Not required" : "Embed Links — Allowed"); }
        NotifyApprovalCommands();
        OnPropertyChanged(nameof(ApprovalDisabledReason));
    }

#endif
    private bool TryBuildDraft(out MessageDraft draft)
    {
        draft = default!;
        if (_botProfileId is not Guid botId || _serverId is not ulong serverId || _snapshot?.Servers.FirstOrDefault(server => server.Id == serverId) is not { } server)
        {
            ValidationErrors.Add("Select a connected bot and accessible server.");
            return false;
        }

        MessageDestination destination;
        if (DestinationMode == MessageDestinationKind.ServerChannel)
        {
            if (SelectedChannel is null) { ValidationErrors.Add("Select a text, announcement, or thread destination."); return false; }
            destination = MessageDestination.Channel(serverId, server.Name, SelectedChannel.Id, SelectedChannel.Name);
        }
        else
        {
            if (SelectedMember is null) { ValidationErrors.Add("Select exactly one member for a direct message."); return false; }
            destination = MessageDestination.DirectMessage(serverId, server.Name, SelectedMember.Id, SelectedMember.DisplayName);
        }

        draft = new MessageDraft(Guid.NewGuid(), botId, destination, BuildContent(), ImmutableArray<MessageAttachmentReference>.Empty, null, DateTimeOffset.UtcNow);
        return true;
    }

    private MessageContent BuildContent() => new(MessageBody, IncludeEmbed ? new EmbedDraft(EmbedTitle, EmbedDescription, null, null, null, null, null, null, null, null, null, null, ImmutableArray<EmbedFieldDraft>.Empty) : null, AllowedMentionPolicy.None);
    private void ClearPreview() { Preview = null; PreviewWarnings.Clear(); StatusMessage = null; SendCommand.NotifyCanExecuteChanged(); }
    private void OnCommandError(Exception _) => StatusMessage = "The requested local messaging action could not be completed.";
    private void OnCacheChanged(object? sender, ExplorerCacheChanged update) { _ = sender; if (_botProfileId == update.BotProfileId) _dispatcher.Post(() => { _snapshot = update.Snapshot; ApplySnapshot(); }); }
    private void ApplySnapshot()
    {
        var selectedChannel = SelectedChannel?.Id; var selectedMember = SelectedMember?.Id;
        Channels.Clear(); Members.Clear();
        var server = _serverId is ulong serverId ? _snapshot?.Servers.FirstOrDefault(item => item.Id == serverId) : null;
        if (server is not null)
        {
            foreach (var channel in server.Channels.Where(item => item.Kind is ChannelKind.Text or ChannelKind.Announcement or ChannelKind.Thread).OrderBy(item => item.Position)) Channels.Add(new(channel));
            foreach (var member in server.Members.Members.Where(item => !item.IsBot).OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)) Members.Add(new(member));
        }
        SelectedChannel = Channels.FirstOrDefault(item => item.Id == selectedChannel);
        SelectedMember = Members.FirstOrDefault(item => item.Id == selectedMember);
    }
}
