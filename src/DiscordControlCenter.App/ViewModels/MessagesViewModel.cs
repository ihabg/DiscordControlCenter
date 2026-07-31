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
    public bool IsApprovalDetailsLoading { get => _isApprovalDetailsLoading; private set { if (SetProperty(ref _isApprovalDetailsLoading, value)) NotifyApprovalCommands(); } }
    public bool IsApprovalPreflightLoading { get => _isApprovalPreflightLoading; private set { if (SetProperty(ref _isApprovalPreflightLoading, value)) NotifyApprovalCommands(); } }
    public ScheduledApprovalPreflightResult? ApprovalPreflightResult { get => _approvalPreflightResult; private set { if (SetProperty(ref _approvalPreflightResult, value)) NotifyApprovalCommands(); } }
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
        ApproveApprovalCommand.Dispose(); RefreshApprovalStatusCommand.Dispose();
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
        var approval = ApprovalDetails;
        if (approval is null || !CanDecidePending || !ConfirmDecision("Skip occurrence", approval)) return;
        IsApprovalBusy = true;
        try
        {
            if (await _approvals.SkipAsync(approval.Occurrence.Id, cancellationToken).ConfigureAwait(false))
            {
                StatusMessage = "The missed occurrence was skipped and was not sent.";
                await LoadApprovalsAsync(cancellationToken).ConfigureAwait(false);
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
