using System.Collections.ObjectModel;
using System.Collections.Immutable;
using DiscordControlCenter.App.Mvvm;
using DiscordControlCenter.App.Services;
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
    private readonly Array _destinationModes = Enum.GetValues<MessageDestinationKind>();

    public MessagesViewModel(
        IBotExplorerService explorer,
        IMessagePlanBuilder planner,
        IMessageTemplateRepository templates,
        IMessageDeliveryDialogService deliveryDialog,
        UiDispatcher dispatcher)
    {
        _explorer = explorer;
        _planner = planner;
        _templates = templates;
        _deliveryDialog = deliveryDialog;
        _dispatcher = dispatcher;
        _explorer.CacheChanged += OnCacheChanged;
        GeneratePreviewCommand = new RelayCommand(_ => GeneratePreview());
        SaveTemplateCommand = new AsyncRelayCommand(SaveTemplateAsync, () => CanSaveTemplate, OnCommandError);
        RefreshTemplatesCommand = new AsyncRelayCommand(LoadTemplatesAsync, errorHandler: OnCommandError);
        SendCommand = new AsyncRelayCommand(SendAsync, () => Preview is not null, OnCommandError);
    }

    public ObservableCollection<MessageChannelOption> Channels { get; } = [];
    public ObservableCollection<MessageMemberOption> Members { get; } = [];
    public ObservableCollection<MessageTemplate> Templates { get; } = [];
    public ObservableCollection<string> ValidationErrors { get; } = [];
    public ObservableCollection<string> PreviewWarnings { get; } = [];
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

    public async Task InitializeAsync(CancellationToken cancellationToken) => await LoadTemplatesAsync(cancellationToken).ConfigureAwait(false);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _explorer.CacheChanged -= OnCacheChanged;
        SaveTemplateCommand.Dispose();
        RefreshTemplatesCommand.Dispose();
        SendCommand.Dispose();
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
