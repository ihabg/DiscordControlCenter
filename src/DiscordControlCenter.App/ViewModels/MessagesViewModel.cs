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

public sealed class MessagesViewModel : ObservableObject, IDisposable
{
    private readonly IBotExplorerService _explorer;
    private readonly IMessagePlanBuilder _planner;
    private readonly IMessageTemplateRepository _templates;
    private readonly IMessageDeliveryDialogService _deliveryDialog;
    private readonly IScheduledMessageRepository _scheduledMessages;
    private readonly IScheduledApprovalService _approvals;
    private readonly IMessagePreflightService _messagePreflight;
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
    private int _approvalSelectionVersion;
    private string? _approvalPreflightSummary;
    private readonly Array _destinationModes = Enum.GetValues<MessageDestinationKind>();

    public MessagesViewModel(
        IBotExplorerService explorer,
        IMessagePlanBuilder planner,
        IMessageTemplateRepository templates,
        IMessageDeliveryDialogService deliveryDialog,
        IScheduledMessageRepository scheduledMessages,
        IScheduledApprovalService approvals,
        IMessagePreflightService messagePreflight,
        UiDispatcher dispatcher)
    {
        _explorer = explorer;
        _planner = planner;
        _templates = templates;
        _deliveryDialog = deliveryDialog;
        _scheduledMessages = scheduledMessages;
        _approvals = approvals;
        _messagePreflight = messagePreflight;
        _dispatcher = dispatcher;
        _explorer.CacheChanged += OnCacheChanged;
        GeneratePreviewCommand = new RelayCommand(_ => GeneratePreview());
        SaveTemplateCommand = new AsyncRelayCommand(SaveTemplateAsync, () => CanSaveTemplate, OnCommandError);
        RefreshTemplatesCommand = new AsyncRelayCommand(LoadTemplatesAsync, errorHandler: OnCommandError);
        SendCommand = new AsyncRelayCommand(SendAsync, () => Preview is not null, OnCommandError);
        RefreshApprovalsCommand = new AsyncRelayCommand(LoadApprovalsAsync, errorHandler: OnCommandError);
        SkipApprovalCommand = new AsyncRelayCommand(SkipApprovalAsync, () => SelectedApproval?.State == MessageOperationState.PendingApproval, OnCommandError);
        ArchiveApprovalCommand = new AsyncRelayCommand(ArchiveApprovalAsync, () => SelectedApproval?.State == MessageOperationState.PendingApproval, OnCommandError);
        ApproveApprovalCommand = new AsyncRelayCommand(ApproveApprovalAsync, () => CanApproveApproval, OnCommandError);
        RefreshApprovalStatusCommand = new RelayCommand(_ => RefreshApprovalPreflight());
    }

    public ObservableCollection<MessageChannelOption> Channels { get; } = [];
    public ObservableCollection<MessageMemberOption> Members { get; } = [];
    public ObservableCollection<MessageTemplate> Templates { get; } = [];
    public ObservableCollection<string> ValidationErrors { get; } = [];
    public ObservableCollection<string> PreviewWarnings { get; } = [];
    public ObservableCollection<ScheduledApprovalListItem> Approvals { get; } = [];
    public ObservableCollection<string> ApprovalPreflightChecks { get; } = [];
    public Array DestinationModes => _destinationModes;

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
    public string? ApprovalPreflightSummary { get => _approvalPreflightSummary; private set => SetProperty(ref _approvalPreflightSummary, value); }
    public bool CanApproveApproval => !IsApprovalBusy && ApprovalDetails?.Occurrence.State == MessageOperationState.PendingApproval && ApprovalDetails.Compatibility is SnapshotCompatibility.Supported or SnapshotCompatibility.SupportedLegacy;
    public string AllowedMentionSummary => ApprovalDetails?.ImmutableContent is not { } content ? "No immutable mention policy is available." : content.AllowedMentions.AllowEveryoneAndHere ? "Everyone and here mentions are allowed; stronger confirmation is required." : content.AllowedMentions.AllowRoleMentions ? "Role mentions are allowed for the saved target IDs." : content.AllowedMentions.AllowedUserIds.Length > 0 ? "Only the saved user mention IDs are allowed." : "Everyone, here, role, and user mentions are blocked.";
    public string ApprovalMessageUsage => ApprovalDetails?.ImmutableContent is { } content ? $"{content.Body.Length:N0} / {MessageLimits.MaximumMessageCharacters:N0} characters" : string.Empty;
    public string ApprovalEmbedUsage => ApprovalDetails?.ImmutableContent is { Embed: { } embed } ? $"{embed.Fields.Length:N0} fields; {MessageLimits.Validate(new MessageContent(string.Empty, embed, AllowedMentionPolicy.None)).Count:N0} validation warning(s)" : "No embed";
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
    public RelayCommand RefreshApprovalStatusCommand { get; }

    public void SetContext(Guid? botProfileId, BotConnectionState connectionState, ulong? serverId)
    {
        _botProfileId = botProfileId;
        _connectionState = connectionState;
        _serverId = serverId;
        _snapshot = botProfileId is Guid id ? _explorer.GetSnapshot(id) : null;
        ApplySnapshot();
        OnPropertyChanged(nameof(HasContext));
        OnPropertyChanged(nameof(ContextMessage));
    }

    public void SetConnectionState(BotConnectionState state)
    {
        _connectionState = state;
        OnPropertyChanged(nameof(HasContext));
        OnPropertyChanged(nameof(ContextMessage));
    }

    public void SetServer(ulong? serverId)
    {
        _serverId = serverId;
        ApplySnapshot();
        OnPropertyChanged(nameof(HasContext));
        OnPropertyChanged(nameof(ContextMessage));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken) { await LoadTemplatesAsync(cancellationToken).ConfigureAwait(false); await LoadApprovalsAsync(cancellationToken).ConfigureAwait(false); }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _explorer.CacheChanged -= OnCacheChanged;
        SaveTemplateCommand.Dispose();
        RefreshTemplatesCommand.Dispose();
        SendCommand.Dispose();
        RefreshApprovalsCommand.Dispose(); SkipApprovalCommand.Dispose(); ArchiveApprovalCommand.Dispose();
        ApproveApprovalCommand.Dispose();
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
        var page = await _scheduledMessages.QueryApprovalsAsync(new ScheduledApprovalQuery(null, _botProfileId, _serverId, null, MessageOperationState.PendingApproval, null, null, null, ScheduledApprovalSort.DueAscending, 1, 100), cancellationToken).ConfigureAwait(false);
        _dispatcher.Post(() => { Approvals.Clear(); foreach (var item in page.Items) Approvals.Add(item); SelectedApproval = Approvals.FirstOrDefault(); });
    }

    private async Task SkipApprovalAsync(CancellationToken cancellationToken)
    {
        if (SelectedApproval is null) return;
        if (ApprovalDetails is null || !ConfirmDecision("Skip occurrence", ApprovalDetails)) return;
        IsApprovalBusy = true; try { if (await _approvals.SkipAsync(SelectedApproval.OccurrenceId, cancellationToken).ConfigureAwait(false)) { StatusMessage = "The missed occurrence was skipped and was not sent."; await LoadApprovalsAsync(cancellationToken).ConfigureAwait(false); } } finally { IsApprovalBusy = false; }
    }

    private async Task ArchiveApprovalAsync(CancellationToken cancellationToken)
    {
        if (SelectedApproval is null) return;
        if (ApprovalDetails is null || !ConfirmDecision("Archive occurrence", ApprovalDetails)) return;
        IsApprovalBusy = true; try { if (await _approvals.ArchiveAsync(SelectedApproval.OccurrenceId, cancellationToken).ConfigureAwait(false)) { StatusMessage = "The missed occurrence was archived and was not sent."; await LoadApprovalsAsync(cancellationToken).ConfigureAwait(false); } } finally { IsApprovalBusy = false; }
    }

    private async Task ApproveApprovalAsync(CancellationToken cancellationToken)
    {
        if (ApprovalDetails is null || !ConfirmDecision("Approve and send", ApprovalDetails)) return;
        IsApprovalBusy = true; try { var result = await _approvals.ApproveAsync(ApprovalDetails.Occurrence.Id, cancellationToken).ConfigureAwait(false); StatusMessage = result?.State == MessageOperationState.Delivered ? "The approved missed message was delivered." : "This occurrence was already processed or could not be sent safely."; await LoadApprovalsAsync(cancellationToken).ConfigureAwait(false); } finally { IsApprovalBusy = false; }
    }

    private async Task LoadApprovalDetailsAsync()
    {
        var selection = SelectedApproval; var version = Interlocked.Increment(ref _approvalSelectionVersion); ApprovalDetails = null;
        if (selection is null) return;
        try { var details = await _scheduledMessages.GetApprovalAsync(selection.OccurrenceId, CancellationToken.None).ConfigureAwait(false); if (version == Volatile.Read(ref _approvalSelectionVersion)) _dispatcher.Post(() => { ApprovalDetails = details; OnPropertyChanged(nameof(AllowedMentionSummary)); OnPropertyChanged(nameof(ApprovalMessageUsage)); OnPropertyChanged(nameof(ApprovalEmbedUsage)); RefreshApprovalPreflight(); }); } catch { if (version == Volatile.Read(ref _approvalSelectionVersion)) _dispatcher.Post(() => StatusMessage = "Approval details could not be loaded."); }
    }

    private void NotifyApprovalCommands() { SkipApprovalCommand.NotifyCanExecuteChanged(); ArchiveApprovalCommand.NotifyCanExecuteChanged(); ApproveApprovalCommand.NotifyCanExecuteChanged(); }

    private static bool ConfirmDecision(string action, ScheduledMessageApproval approval) =>
        new ScheduledApprovalDecisionWindow(new ScheduledApprovalDecisionViewModel(action, approval)) { Owner = System.Windows.Application.Current.MainWindow }.ShowDialog() == true;

    private void RefreshApprovalPreflight()
    {
        ApprovalPreflightChecks.Clear();
        if (ApprovalDetails?.ImmutableContent is null) { ApprovalPreflightSummary = "Current status cannot be checked until immutable details are available."; return; }
        var draft = new MessageDraft(Guid.NewGuid(), ApprovalDetails.Snapshot.BotProfileId, ApprovalDetails.Snapshot.Destination, ApprovalDetails.ImmutableContent, ImmutableArray<MessageAttachmentReference>.Empty, null, DateTimeOffset.UtcNow);
        var plan = _planner.Build(draft, MessageOperationKind.ScheduledChannelMessage).Plan;
        var check = plan is null ? null : _messagePreflight.Validate(plan);
        ApprovalPreflightSummary = check is null ? "The immutable message no longer meets delivery limits." : check.IsAllowed ? "Allowed — current Discord checks passed." : string.Join(" ", check.Issues.Select(issue => issue.Message));
        ApprovalPreflightChecks.Add(ApprovalDetails.Compatibility is SnapshotCompatibility.Supported or SnapshotCompatibility.SupportedLegacy ? "Snapshot compatibility — Allowed" : "Snapshot compatibility — Blocked");
        foreach (var issue in check?.Issues ?? []) ApprovalPreflightChecks.Add($"Blocked — {issue.Message}");
        if (check?.IsAllowed == true) { ApprovalPreflightChecks.Add("Bot connection, destination, and required permissions — Allowed"); ApprovalPreflightChecks.Add(ApprovalDetails.ImmutableContent.Embed is null ? "Embed Links — Not required" : "Embed Links — Allowed"); }
        NotifyApprovalCommands();
    }

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
