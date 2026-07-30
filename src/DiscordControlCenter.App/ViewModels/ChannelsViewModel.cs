using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.Windows.Threading;
using DiscordControlCenter.App.Mvvm;
using DiscordControlCenter.App.Services;
using DiscordControlCenter.Application.Explorer;
using DiscordControlCenter.Application.Operations;
using DiscordControlCenter.Core.Bots;
using DiscordControlCenter.Core.Explorer;
using DiscordControlCenter.Core.Operations;

namespace DiscordControlCenter.App.ViewModels;

public sealed class ChannelsViewModel : ObservableObject, IDisposable
{
    private readonly IBotExplorerService _explorer;
    private readonly IPermissionResolutionService _permissions;
    private readonly IChannelOperationDialogService _operationDialogs;
    private readonly IChannelOperationScheduler _operationScheduler;
    private readonly UiDispatcher _dispatcher;
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _updateTimer;
    private BotExplorerSnapshot? _snapshot;
    private Guid? _botProfileId;
    private ulong? _serverId;
    private ulong? _selectedChannelId;
    private BotConnectionState _connectionState;
    private string _botDisplayName = "Selected bot";
    private ChannelItemViewModel? _selectedChannel;
    private string _searchText = string.Empty;
    private string? _operationError;
    private bool _disposed;
    private readonly HashSet<ulong> _operationSelectedIds = [];

    public ChannelsViewModel(
        IBotExplorerService explorer,
        IPermissionResolutionService permissions,
        IChannelOperationDialogService operationDialogs,
        IChannelOperationScheduler operationScheduler,
        UiDispatcher dispatcher)
    {
        _explorer = explorer;
        _permissions = permissions;
        _operationDialogs = operationDialogs;
        _operationScheduler = operationScheduler;
        _dispatcher = dispatcher;
        var uiDispatcher = System.Windows.Application.Current?.Dispatcher
            ?? Dispatcher.CurrentDispatcher;
        _searchTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(250),
            DispatcherPriority.Background,
            OnSearchTimer,
            uiDispatcher);
        _searchTimer.Stop();
        _updateTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(100),
            DispatcherPriority.DataBind,
            OnUpdateTimer,
            uiDispatcher);
        _updateTimer.Stop();
        SelectChannelCommand = new RelayCommand(SelectChannel);
        ToggleOperationSelectionCommand = new RelayCommand(ToggleOperationSelection);
        ClearOperationSelectionCommand = new RelayCommand(
            _ => ClearOperationSelection(),
            _ => SelectedOperationCount > 0);
        CreateOperationCommand = CreateOperationCommandFor(ChannelOperationUiMode.Create);
        EditOperationCommand = CreateOperationCommandFor(ChannelOperationUiMode.Edit);
        RenameOperationCommand = CreateOperationCommandFor(ChannelOperationUiMode.Rename);
        MoveOperationCommand = CreateOperationCommandFor(ChannelOperationUiMode.Move);
        CloneOperationCommand = CreateOperationCommandFor(ChannelOperationUiMode.Clone);
        LockOperationCommand = CreateOperationCommandFor(ChannelOperationUiMode.Lock);
        SynchronizeOperationCommand =
            CreateOperationCommandFor(ChannelOperationUiMode.SynchronizePermissions);
        DeleteOperationCommand = CreateOperationCommandFor(ChannelOperationUiMode.Delete);
        OpenOperationCenterCommand = new RelayCommand(
            _ => OperationCenterRequested?.Invoke(this, EventArgs.Empty));
        RefreshCommand = new AsyncRelayCommand(
            RefreshAsync,
            () => CanRefresh,
            HandleUnexpectedError);
        _explorer.CacheChanged += OnCacheChanged;
        _operationScheduler.OperationChanged += OnOperationChanged;
    }

    public ObservableCollection<ChannelGroupViewModel> ChannelGroups { get; } = [];
    public event EventHandler? OperationQueued;
    public event EventHandler? OperationCenterRequested;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                _searchTimer.Stop();
                _searchTimer.Start();
            }
        }
    }

    public ChannelItemViewModel? SelectedChannel
    {
        get => _selectedChannel;
        private set
        {
            if (ReferenceEquals(_selectedChannel, value))
            {
                return;
            }

            if (_selectedChannel is not null)
            {
                _selectedChannel.IsSelected = false;
            }

            if (SetProperty(ref _selectedChannel, value) && value is not null)
            {
                value.IsSelected = true;
            }

            _selectedChannelId = value?.Id;
            OnPropertyChanged(nameof(HasSelectedChannel));
        }
    }

    public bool HasSelectedChannel => SelectedChannel is not null;
    public int SelectedOperationCount => _operationSelectedIds.Count;
    public string OperationSelectionSummary =>
        $"{SelectedOperationCount} selected for channel operations";
    public bool CanCreateOperation =>
        HasOperationContext && HasServerPermission(PermissionBits.ManageChannels);
    public bool CanEditOperation =>
        HasSupportedOperationSelection && SelectedOperationCount == 1;
    public bool CanRenameOperation => HasSupportedOperationSelection;
    public bool CanMoveOperation => HasSupportedOperationSelection;
    public bool CanCloneOperation
    {
        get
        {
            if (!HasSupportedOperationSelection)
            {
                return false;
            }

            var selected = SelectedOperationChannels;
            if (selected.Count == 1)
            {
                return true;
            }

            var category = selected.SingleOrDefault(channel =>
                channel.Kind == ChannelKind.Category);
            return category is not null
                && selected.Where(channel => channel.Id != category.Id)
                    .All(channel => channel.CategoryId == category.Id);
        }
    }
    public bool CanLockOperation => SelectedOperationCount is > 0 and <= 50
        && HasOperationContext
        && HasSelectedChannelPermission(PermissionBits.ManageChannels)
        && HasServerPermission(PermissionBits.ManageRoles)
        && SelectedOperationChannels.All(channel =>
            channel.Kind is ChannelKind.Text or ChannelKind.Voice);
    public bool CanSynchronizeOperation => SelectedOperationCount is > 0 and <= 50
        && HasOperationContext
        && HasSelectedChannelPermission(PermissionBits.ManageChannels)
        && HasServerPermission(PermissionBits.ManageRoles)
        && SelectedOperationChannels.All(channel =>
            (channel.Kind is ChannelKind.Text or ChannelKind.Voice)
            && channel.CategoryId is not null);
    public bool CanDeleteOperation
    {
        get
        {
            if (!HasSupportedOperationSelection)
            {
                return false;
            }

            var selected = SelectedOperationChannels;
            var categories = selected
                .Where(channel => channel.Kind == ChannelKind.Category)
                .ToArray();
            return categories.Length == 0
                || categories is [var category]
                && selected.Where(channel => channel.Id != category.Id)
                    .All(channel => channel.CategoryId == category.Id);
        }
    }
    public string CreateOperationExplanation => CanCreateOperation
        ? "Create categories, text channels, or voice channels through a guarded preview."
        : OperationContextFailure
          ?? "Manage Channels is required and current permission data must be complete.";
    public string EditOperationExplanation => CanEditOperation
        ? "Edit the selected ordinary channel or category."
        : OperationContextFailure
          ?? "Select exactly one ordinary text channel, voice channel, or category.";
    public string RenameOperationExplanation => CanRenameOperation
        ? "Generate exact names for the selected resources."
        : "Select 1 to 50 supported channels or categories.";
    public string MoveOperationExplanation => CanMoveOperation
        ? "Move the selected resources with deterministic ordering."
        : RenameOperationExplanation;
    public string CloneOperationExplanation => CanCloneOperation
        ? "Clone the selected channel or category structure."
        : OperationContextFailure
          ?? "Select one channel, or one category with any selected child channels.";
    public string LockOperationExplanation => CanLockOperation
        ? "Change only the selected deny bits while preserving unrelated overwrite bits."
        : OperationContextFailure
          ?? (!HasServerPermission(PermissionBits.ManageRoles)
              ? "Manage Roles is required for permission overwrites."
              : "Select one or more ordinary text or voice channels.");
    public string SynchronizeOperationExplanation => CanSynchronizeOperation
        ? "Replace each selected channel's overwrites with its current category overwrites."
        : OperationContextFailure
          ?? (!HasServerPermission(PermissionBits.ManageRoles)
              ? "Manage Roles is required for permission synchronization."
              : "Select ordinary child channels that have a parent category.");
    public string DeleteOperationExplanation => CanDeleteOperation
        ? "Configure deletion scope. A backup and typed confirmation are mandatory."
        : OperationContextFailure
          ?? "Select ordinary channels, or one category with children that belong to it.";
    public bool HasBotSelection => _botProfileId is not null;
    public bool HasServerSelection => _serverId is not null;
    public bool IsDisconnected =>
        HasBotSelection && _connectionState != BotConnectionState.Connected;
    public bool IsLoading => _snapshot?.State == ExplorerCacheState.Loading;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasChannels => ChannelGroups.Any(group => group.Channels.Count > 0 || group.HasCategory);
    public bool CanRefresh =>
        HasBotSelection
        && _connectionState == BotConnectionState.Connected
        && !RefreshCommand.IsRunning;
    public string? ErrorMessage => _operationError ?? _snapshot?.ErrorMessage;
    public string ServerName => GetServer()?.Name ?? "No server selected";
    public string StateTitle => !HasBotSelection
        ? "Select a bot"
        : IsDisconnected
            ? "Bot is disconnected"
            : !HasServerSelection
                ? "Select a server"
                : HasError
                    ? "Channel Explorer needs attention"
                    : IsLoading
                        ? "Loading channels"
                        : "No channels found";
    public string StateMessage => !HasBotSelection
        ? "Choose a saved bot from the toolbar."
        : IsDisconnected
            ? "Connect this bot from Bot Manager before browsing channels."
            : !HasServerSelection
                ? "Choose one of the selected bot's servers from the toolbar or Server Explorer."
                : HasError
                    ? ErrorMessage ?? "Channel information could not be loaded."
                    : IsLoading
                        ? "Reading categories and channels from the gateway cache."
                        : string.IsNullOrWhiteSpace(SearchText)
                            ? "This server has no channels available to the bot."
                            : "No channel name or ID matches the current search.";

    public RelayCommand SelectChannelCommand { get; }
    public RelayCommand ToggleOperationSelectionCommand { get; }
    public RelayCommand ClearOperationSelectionCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand CreateOperationCommand { get; }
    public AsyncRelayCommand EditOperationCommand { get; }
    public AsyncRelayCommand RenameOperationCommand { get; }
    public AsyncRelayCommand MoveOperationCommand { get; }
    public AsyncRelayCommand CloneOperationCommand { get; }
    public AsyncRelayCommand LockOperationCommand { get; }
    public AsyncRelayCommand SynchronizeOperationCommand { get; }
    public AsyncRelayCommand DeleteOperationCommand { get; }
    public RelayCommand OpenOperationCenterCommand { get; }

    public void SetBot(
        Guid? botProfileId,
        BotConnectionState connectionState,
        string? botDisplayName = null)
    {
        RefreshCommand.Cancel();
        _botProfileId = botProfileId;
        _connectionState = connectionState;
        _botDisplayName = string.IsNullOrWhiteSpace(botDisplayName)
            ? "Selected bot"
            : botDisplayName;
        _serverId = null;
        _selectedChannelId = null;
        _operationSelectedIds.Clear();
        _operationError = null;
        _snapshot = botProfileId is Guid id ? _explorer.GetSnapshot(id) : null;
        ApplySnapshot();
    }

    public void SetConnectionState(BotConnectionState connectionState)
    {
        _connectionState = connectionState;
        if (_botProfileId is Guid id)
        {
            _snapshot = _explorer.GetSnapshot(id);
        }

        if (connectionState != BotConnectionState.Connected)
        {
            _serverId = null;
            _selectedChannelId = null;
            _operationSelectedIds.Clear();
        }

        ApplySnapshot();
    }

    public void SetServer(ulong? serverId)
    {
        _serverId = serverId;
        _selectedChannelId = null;
        _operationSelectedIds.Clear();
        ApplySnapshot();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _explorer.CacheChanged -= OnCacheChanged;
        _operationScheduler.OperationChanged -= OnOperationChanged;
        _searchTimer.Stop();
        _searchTimer.Tick -= OnSearchTimer;
        _updateTimer.Stop();
        _updateTimer.Tick -= OnUpdateTimer;
        RefreshCommand.Dispose();
        CreateOperationCommand.Dispose();
        EditOperationCommand.Dispose();
        RenameOperationCommand.Dispose();
        MoveOperationCommand.Dispose();
        CloneOperationCommand.Dispose();
        LockOperationCommand.Dispose();
        SynchronizeOperationCommand.Dispose();
        DeleteOperationCommand.Dispose();
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (_botProfileId is not Guid botProfileId)
        {
            return;
        }

        _operationError = null;
        NotifyStateChanged();
        var result = await _explorer.RefreshAsync(botProfileId, cancellationToken);
        if (_botProfileId != botProfileId)
        {
            return;
        }

        if (!result.IsSuccess)
        {
            _operationError = result.Error;
        }

        _snapshot = _explorer.GetSnapshot(botProfileId);
        ApplySnapshot();
    }

    private void SelectChannel(object? parameter)
    {
        if (parameter is ChannelItemViewModel channel)
        {
            SelectedChannel = channel;
        }
    }

    private void ToggleOperationSelection(object? parameter)
    {
        if (parameter is not ChannelItemViewModel channel)
        {
            return;
        }

        if (!_operationSelectedIds.Add(channel.Id))
        {
            _operationSelectedIds.Remove(channel.Id);
            channel.IsOperationSelected = false;
        }
        else
        {
            channel.IsOperationSelected = true;
        }

        NotifyOperationSelectionChanged();
    }

    private void ClearOperationSelection()
    {
        _operationSelectedIds.Clear();
        foreach (var channel in EnumerateChannelItems())
        {
            channel.IsOperationSelected = false;
        }

        NotifyOperationSelectionChanged();
    }

    private AsyncRelayCommand CreateOperationCommandFor(ChannelOperationUiMode mode) =>
        new(
            cancellationToken => ConfigureOperationAsync(mode, cancellationToken),
            () => CanExecuteOperation(mode),
            HandleUnexpectedError);

    private async Task ConfigureOperationAsync(
        ChannelOperationUiMode mode,
        CancellationToken cancellationToken)
    {
        if (_botProfileId is not Guid botProfileId || GetServer() is not { } server)
        {
            return;
        }

        var context = new ChannelOperationContext(
            botProfileId,
            _botDisplayName,
            server,
            SelectedOperationChannels.ToImmutableArray());
        if (await _operationDialogs
                .ConfigurePreviewConfirmAndQueueAsync(context, mode, cancellationToken)
                .ConfigureAwait(true))
        {
            OperationQueued?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool CanExecuteOperation(ChannelOperationUiMode mode) =>
        mode switch
        {
            ChannelOperationUiMode.Create => CanCreateOperation,
            ChannelOperationUiMode.Edit => CanEditOperation,
            ChannelOperationUiMode.Rename => CanRenameOperation,
            ChannelOperationUiMode.Move => CanMoveOperation,
            ChannelOperationUiMode.Clone => CanCloneOperation,
            ChannelOperationUiMode.Lock => CanLockOperation,
            ChannelOperationUiMode.SynchronizePermissions => CanSynchronizeOperation,
            ChannelOperationUiMode.Delete => CanDeleteOperation,
            _ => false
        };

    private void OnCacheChanged(object? sender, ExplorerCacheChanged update)
    {
        _ = sender;
        if (_botProfileId != update.BotProfileId)
        {
            return;
        }

        _permissions.Invalidate(update.BotProfileId, update.ServerId);
        _dispatcher.Post(
            () =>
            {
                _snapshot = update.Snapshot;
                _updateTimer.Stop();
                _updateTimer.Start();
            });
    }

    private void OnOperationChanged(object? sender, QueuedOperationSnapshot snapshot)
    {
        _ = sender;
        if (_botProfileId != snapshot.Plan.BotProfileId
            || _serverId != snapshot.Plan.ServerId)
        {
            return;
        }

        _dispatcher.Post(NotifyOperationSelectionChanged);
    }

    private void OnSearchTimer(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        _searchTimer.Stop();
        ApplySnapshot();
    }

    private void OnUpdateTimer(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        _updateTimer.Stop();
        ApplySnapshot();
    }

    private void ApplySnapshot()
    {
        var server = GetServer();
        var selectedId = _selectedChannelId;
        ChannelGroups.Clear();
        if (server is not null && _snapshot is not null)
        {
            foreach (var group in ExplorerSearch.BuildChannelTree(server, SearchText))
            {
                var categoryModel = group.CategoryId is ulong categoryId
                    ? server.Channels.FirstOrDefault(channel => channel.Id == categoryId)
                    : null;
                var category = categoryModel is null
                    ? null
                    : CreateChannel(categoryModel, server);
                var channels = group.Channels
                    .Select(channel => CreateChannel(channel, server))
                    .ToArray();
                ChannelGroups.Add(new ChannelGroupViewModel(group.Name, category, channels));
            }
        }

        if (server is null)
        {
            _operationSelectedIds.Clear();
        }
        else
        {
            _operationSelectedIds.RemoveWhere(
                id => !server.Channels.Any(channel => channel.Id == id));
            foreach (var channel in EnumerateChannelItems())
            {
                channel.IsOperationSelected = _operationSelectedIds.Contains(channel.Id);
            }
        }

        SelectedChannel = FindChannel(selectedId);
        NotifyStateChanged();
    }

    private ChannelItemViewModel CreateChannel(
        ChannelReadModel channel,
        ServerReadModel server) =>
        new(
            channel,
            _permissions.ResolveChannel(
                _snapshot!.BotProfileId,
                _snapshot.Version,
                server,
                channel));

    private ChannelItemViewModel? FindChannel(ulong? channelId)
    {
        if (channelId is null)
        {
            return null;
        }

        foreach (var group in ChannelGroups)
        {
            if (group.Category?.Id == channelId)
            {
                return group.Category;
            }

            var channel = group.Channels.FirstOrDefault(item => item.Id == channelId);
            if (channel is not null)
            {
                return channel;
            }
        }

        return null;
    }

    private ServerReadModel? GetServer() =>
        _serverId is ulong serverId
            ? _snapshot?.Servers.FirstOrDefault(server => server.Id == serverId)
            : null;

    private IReadOnlyList<ChannelReadModel> SelectedOperationChannels
    {
        get
        {
            var server = GetServer();
            if (server is null || _operationSelectedIds.Count == 0)
            {
                return [];
            }

            return server.Channels
                .Where(channel => _operationSelectedIds.Contains(channel.Id))
                .OrderBy(channel => channel.Kind == ChannelKind.Category ? 0 : 1)
                .ThenBy(channel => channel.Position)
                .ThenBy(channel => channel.Id)
                .ToArray();
        }
    }

    private bool HasOperationContext =>
        _botProfileId is not null
        && _serverId is not null
        && _connectionState == BotConnectionState.Connected
        && !HasActiveServerOperation;
    private bool HasSupportedOperationSelection =>
        HasOperationContext
        && HasSelectedChannelPermission(PermissionBits.ManageChannels)
        && SelectedOperationCount is > 0 and <= 50
        && SelectedOperationChannels.All(IsSupportedMutableChannel);

    private bool HasActiveServerOperation =>
        _botProfileId is Guid botId
        && _serverId is ulong serverId
        && _operationScheduler.Snapshots.Any(snapshot =>
            snapshot.Plan.BotProfileId == botId
            && snapshot.Plan.ServerId == serverId
            && snapshot.State is ChannelOperationState.Pending
                or ChannelOperationState.Running
                or ChannelOperationState.Waiting
                or ChannelOperationState.Cancelling);

    private string? OperationContextFailure =>
        _botProfileId is null
            ? "Select a bot before configuring a write."
            : _serverId is null
                ? "Select a server before configuring a write."
                : _connectionState != BotConnectionState.Connected
            ? "Connect the selected bot before configuring a write."
            : HasActiveServerOperation
                ? "Wait for the active operation on this server to reach a terminal result."
                : !HasServerPermission(PermissionBits.ManageChannels)
                    ? "Manage Channels is required and current permission data must be complete."
                    : null;

    private bool HasServerPermission(PermissionBits permission)
    {
        if (_botProfileId is not Guid botId
            || _snapshot is null
            || GetServer() is not { } server)
        {
            return false;
        }

        var result = _permissions
            .ResolveServer(botId, _snapshot.Version, server)
            .Permissions
            .FirstOrDefault(item => item.Permission == permission);
        return result?.Status is PermissionStatus.Allowed
            or PermissionStatus.AllowedThroughAdministrator;
    }

    private bool HasSelectedChannelPermission(PermissionBits permission)
    {
        if (_botProfileId is not Guid botId
            || _snapshot is null
            || GetServer() is not { } server)
        {
            return false;
        }

        var selected = SelectedOperationChannels;
        return selected.Count > 0
            && selected.All(
                channel =>
                {
                    var result = _permissions
                        .ResolveChannel(botId, _snapshot.Version, server, channel)
                        .Permissions
                        .FirstOrDefault(item => item.Permission == permission);
                    return result?.Status is PermissionStatus.Allowed
                        or PermissionStatus.AllowedThroughAdministrator;
                });
    }

    private static bool IsSupportedMutableChannel(ChannelReadModel channel) =>
        channel.Kind is ChannelKind.Category or ChannelKind.Text or ChannelKind.Voice;

    private IEnumerable<ChannelItemViewModel> EnumerateChannelItems()
    {
        foreach (var group in ChannelGroups)
        {
            if (group.Category is not null)
            {
                yield return group.Category;
            }

            foreach (var channel in group.Channels)
            {
                yield return channel;
            }
        }
    }

    private void HandleUnexpectedError(Exception exception)
    {
        _ = exception;
        _operationError = "An unexpected Channel Explorer error occurred.";
        NotifyStateChanged();
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(HasSelectedChannel));
        OnPropertyChanged(nameof(HasBotSelection));
        OnPropertyChanged(nameof(HasServerSelection));
        OnPropertyChanged(nameof(IsDisconnected));
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(HasChannels));
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(ErrorMessage));
        OnPropertyChanged(nameof(ServerName));
        OnPropertyChanged(nameof(StateTitle));
        OnPropertyChanged(nameof(StateMessage));
        NotifyOperationSelectionChanged();
        RefreshCommand.NotifyCanExecuteChanged();
    }

    private void NotifyOperationSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedOperationCount));
        OnPropertyChanged(nameof(OperationSelectionSummary));
        OnPropertyChanged(nameof(CanCreateOperation));
        OnPropertyChanged(nameof(CanEditOperation));
        OnPropertyChanged(nameof(CanRenameOperation));
        OnPropertyChanged(nameof(CanMoveOperation));
        OnPropertyChanged(nameof(CanCloneOperation));
        OnPropertyChanged(nameof(CanLockOperation));
        OnPropertyChanged(nameof(CanSynchronizeOperation));
        OnPropertyChanged(nameof(CanDeleteOperation));
        OnPropertyChanged(nameof(CreateOperationExplanation));
        OnPropertyChanged(nameof(EditOperationExplanation));
        OnPropertyChanged(nameof(RenameOperationExplanation));
        OnPropertyChanged(nameof(MoveOperationExplanation));
        OnPropertyChanged(nameof(CloneOperationExplanation));
        OnPropertyChanged(nameof(LockOperationExplanation));
        OnPropertyChanged(nameof(SynchronizeOperationExplanation));
        OnPropertyChanged(nameof(DeleteOperationExplanation));
        ClearOperationSelectionCommand.NotifyCanExecuteChanged();
        CreateOperationCommand.NotifyCanExecuteChanged();
        EditOperationCommand.NotifyCanExecuteChanged();
        RenameOperationCommand.NotifyCanExecuteChanged();
        MoveOperationCommand.NotifyCanExecuteChanged();
        CloneOperationCommand.NotifyCanExecuteChanged();
        LockOperationCommand.NotifyCanExecuteChanged();
        SynchronizeOperationCommand.NotifyCanExecuteChanged();
        DeleteOperationCommand.NotifyCanExecuteChanged();
    }
}
