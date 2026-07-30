using System.Collections.ObjectModel;
using System.Windows.Threading;
using DiscordControlCenter.App.Mvvm;
using DiscordControlCenter.App.Services;
using DiscordControlCenter.Application.Explorer;
using DiscordControlCenter.Core.Bots;
using DiscordControlCenter.Core.Explorer;

namespace DiscordControlCenter.App.ViewModels;

public sealed class ChannelsViewModel : ObservableObject, IDisposable
{
    private readonly IBotExplorerService _explorer;
    private readonly IPermissionResolutionService _permissions;
    private readonly UiDispatcher _dispatcher;
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _updateTimer;
    private BotExplorerSnapshot? _snapshot;
    private Guid? _botProfileId;
    private ulong? _serverId;
    private ulong? _selectedChannelId;
    private BotConnectionState _connectionState;
    private ChannelItemViewModel? _selectedChannel;
    private string _searchText = string.Empty;
    private string? _operationError;
    private bool _disposed;

    public ChannelsViewModel(
        IBotExplorerService explorer,
        IPermissionResolutionService permissions,
        UiDispatcher dispatcher)
    {
        _explorer = explorer;
        _permissions = permissions;
        _dispatcher = dispatcher;
        var uiDispatcher = System.Windows.Application.Current.Dispatcher;
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
        RefreshCommand = new AsyncRelayCommand(
            RefreshAsync,
            () => CanRefresh,
            HandleUnexpectedError);
        _explorer.CacheChanged += OnCacheChanged;
    }

    public ObservableCollection<ChannelGroupViewModel> ChannelGroups { get; } = [];

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
    public AsyncRelayCommand RefreshCommand { get; }

    public void SetBot(Guid? botProfileId, BotConnectionState connectionState)
    {
        RefreshCommand.Cancel();
        _botProfileId = botProfileId;
        _connectionState = connectionState;
        _serverId = null;
        _selectedChannelId = null;
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
        }

        ApplySnapshot();
    }

    public void SetServer(ulong? serverId)
    {
        _serverId = serverId;
        _selectedChannelId = null;
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
        _searchTimer.Stop();
        _searchTimer.Tick -= OnSearchTimer;
        _updateTimer.Stop();
        _updateTimer.Tick -= OnUpdateTimer;
        RefreshCommand.Dispose();
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
        RefreshCommand.NotifyCanExecuteChanged();
    }
}
