using System.Collections.ObjectModel;
using System.Windows.Threading;
using DiscordControlCenter.App.Mvvm;
using DiscordControlCenter.App.Services;
using DiscordControlCenter.Application.Explorer;
using DiscordControlCenter.Core.Bots;
using DiscordControlCenter.Core.Explorer;

namespace DiscordControlCenter.App.ViewModels;

public sealed class ServersViewModel : ObservableObject, IDisposable
{
    private readonly IBotExplorerService _explorer;
    private readonly IPermissionResolutionService _permissions;
    private readonly UiDispatcher _dispatcher;
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _updateTimer;
    private BotExplorerSnapshot? _snapshot;
    private Guid? _botProfileId;
    private BotConnectionState _connectionState;
    private ServerItemViewModel? _selectedServer;
    private ulong? _requestedServerId;
    private string _searchText = string.Empty;
    private string? _operationError;
    private long _selectionGeneration;
    private bool _disposed;
    private bool _suppressSelectionEvent;

    public ServersViewModel(
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
        RefreshCommand = new AsyncRelayCommand(
            RefreshAsync,
            () => CanRefresh,
            HandleUnexpectedError);
        _explorer.CacheChanged += OnCacheChanged;
    }

    public event EventHandler<ulong?>? ServerSelected;

    public ObservableCollection<ServerItemViewModel> Servers { get; } = [];

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

    public ServerItemViewModel? SelectedServer
    {
        get => _selectedServer;
        set
        {
            if (!SetProperty(ref _selectedServer, value))
            {
                return;
            }

            _requestedServerId = value?.Id;
            NotifySelectedServerChanged();
            if (!_suppressSelectionEvent)
            {
                ServerSelected?.Invoke(this, value?.Id);
            }
        }
    }

    public bool IsLoading => _snapshot?.State == ExplorerCacheState.Loading;
    public bool IsDisconnected =>
        _botProfileId is not null && _connectionState != BotConnectionState.Connected;
    public bool HasBotSelection => _botProfileId is not null;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool IsEmpty =>
        _connectionState == BotConnectionState.Connected
        && !IsLoading
        && !HasError
        && Servers.Count == 0;
    public bool HasServers => Servers.Count > 0;
    public bool CanRefresh =>
        _botProfileId is not null
        && _connectionState == BotConnectionState.Connected
        && !RefreshCommand.IsRunning;
    public string? ErrorMessage => _operationError ?? _snapshot?.ErrorMessage;
    public string StateTitle => !HasBotSelection
        ? "Select a bot"
        : IsDisconnected
            ? "Bot is disconnected"
            : HasError
                ? "Server Explorer needs attention"
                : IsLoading
                    ? "Loading servers"
                    : "No servers found";
    public string StateMessage => !HasBotSelection
        ? "Choose a saved bot from the toolbar to browse its servers."
        : IsDisconnected
            ? "Connect this bot from Bot Manager. Server Explorer never connects bots automatically."
            : HasError
                ? ErrorMessage ?? "Discord server information could not be loaded."
                : IsLoading
                    ? "Reading the selected bot's gateway cache."
                    : string.IsNullOrWhiteSpace(SearchText)
                        ? "The selected bot does not currently have access to any servers."
                        : "No server name or ID matches the current search.";

    public AsyncRelayCommand RefreshCommand { get; }

    public void SetBot(Guid? botProfileId, BotConnectionState connectionState)
    {
        RefreshCommand.Cancel();
        _selectionGeneration++;
        _botProfileId = botProfileId;
        _connectionState = connectionState;
        _requestedServerId = null;
        _snapshot = botProfileId is Guid id
            ? _explorer.GetSnapshot(id)
            : null;
        _operationError = null;
        ApplySnapshot();
        NotifyStateChanged();
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
            _requestedServerId = null;
        }

        ApplySnapshot();
        NotifyStateChanged();
    }

    public void SetSelectedServer(ulong? serverId)
    {
        _requestedServerId = serverId;
        SelectRequestedServer();
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

        var generation = _selectionGeneration;
        _operationError = null;
        NotifyStateChanged();
        var result = await _explorer.RefreshAsync(botProfileId, cancellationToken);
        if (generation != _selectionGeneration || _botProfileId != botProfileId)
        {
            return;
        }

        if (!result.IsSuccess)
        {
            _operationError = result.Error;
        }

        _snapshot = _explorer.GetSnapshot(botProfileId);
        ApplySnapshot();
        NotifyStateChanged();
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
        NotifyStateChanged();
    }

    private void ApplySnapshot()
    {
        var selectedId = _requestedServerId ?? SelectedServer?.Id;
        var models = _snapshot is null
            ? []
            : ExplorerSearch.FilterServers(_snapshot.Servers, SearchText);
        Servers.Clear();
        if (_snapshot is not null)
        {
            foreach (var server in models)
            {
                Servers.Add(
                    new ServerItemViewModel(
                        server,
                        _permissions.ResolveServer(
                            _snapshot.BotProfileId,
                            _snapshot.Version,
                            server)));
            }
        }

        _requestedServerId = selectedId;
        SelectRequestedServer();
        NotifyStateChanged();
    }

    private void SelectRequestedServer()
    {
        var requestedServerId = _requestedServerId;
        var selected = requestedServerId is ulong id
            ? Servers.FirstOrDefault(server => server.Id == id)
            : null;
        _suppressSelectionEvent = true;
        try
        {
            SelectedServer = selected;
        }
        finally
        {
            _requestedServerId = requestedServerId;
            _suppressSelectionEvent = false;
        }
    }

    private void HandleUnexpectedError(Exception exception)
    {
        _ = exception;
        _operationError = "An unexpected Server Explorer error occurred.";
        NotifyStateChanged();
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(IsDisconnected));
        OnPropertyChanged(nameof(HasBotSelection));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasServers));
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(ErrorMessage));
        OnPropertyChanged(nameof(StateTitle));
        OnPropertyChanged(nameof(StateMessage));
        RefreshCommand.NotifyCanExecuteChanged();
    }

    private void NotifySelectedServerChanged()
    {
        OnPropertyChanged(nameof(SelectedServer));
    }
}
