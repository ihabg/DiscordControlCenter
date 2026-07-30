using System.Collections.ObjectModel;
using System.Collections.Specialized;
using DiscordControlCenter.App.Mvvm;
using DiscordControlCenter.App.Services;
using DiscordControlCenter.Application.Bots;
using DiscordControlCenter.Application.Explorer;
using DiscordControlCenter.Core.Bots;
using DiscordControlCenter.Core.Explorer;

namespace DiscordControlCenter.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly DashboardViewModel _dashboard;
    private readonly BotsViewModel _bots;
    private readonly ServersViewModel _servers;
    private readonly ChannelsViewModel _channels;
    private readonly IBotConnectionManager _connectionManager;
    private readonly IBotExplorerService _explorer;
    private readonly UiDispatcher _dispatcher;
    private object _currentPage;
    private string _currentTitle = "Dashboard";
    private BotCardViewModel? _selectedBot;
    private ServerOptionViewModel? _selectedServer;
    private BotConnectionState _selectedConnectionState;
    private bool _updatingToolbarServers;

    public MainWindowViewModel(
        DashboardViewModel dashboard,
        BotsViewModel bots,
        ServersViewModel servers,
        ChannelsViewModel channels,
        IBotConnectionManager connectionManager,
        IBotExplorerService explorer,
        UiDispatcher dispatcher)
    {
        _dashboard = dashboard;
        _bots = bots;
        _servers = servers;
        _channels = channels;
        _connectionManager = connectionManager;
        _explorer = explorer;
        _dispatcher = dispatcher;
        _currentPage = dashboard;
        Navigation =
        [
            new("◫", "Dashboard"),
            new("◆", "Bots"),
            new("▰", "Servers"),
            new("#", "Channels"),
            new("♟", "Members"),
            new("♛", "Roles"),
            new("◖", "Voice"),
            new("✉", "Messages"),
            new("⚙", "Automation"),
            new("▣", "Backups"),
            new("≡", "Audit Log"),
            new("⚙", "Settings")
        ];
        NavigateCommand = new RelayCommand(Navigate);
        _bots.Bots.CollectionChanged += OnBotsChanged;
        _servers.ServerSelected += OnServerExplorerSelected;
        _connectionManager.StatusChanged += OnConnectionStatusChanged;
        _explorer.CacheChanged += OnExplorerCacheChanged;
    }

    public ObservableCollection<NavigationItem> Navigation { get; }
    public ObservableCollection<BotCardViewModel> Bots => _bots.Bots;
    public ObservableCollection<ServerOptionViewModel> ToolbarServers { get; } = [];
    public string SearchText
    {
        get => _bots.SearchText;
        set => _bots.SearchText = value;
    }

    public BotCardViewModel? SelectedBot
    {
        get => _selectedBot;
        set
        {
            if (!SetProperty(ref _selectedBot, value))
            {
                return;
            }

            _selectedConnectionState = value?.State ?? BotConnectionState.Disconnected;
            SelectedServer = null;
            UpdateToolbarServers();
            _servers.SetBot(value?.Id, _selectedConnectionState);
            _channels.SetBot(value?.Id, _selectedConnectionState);
            OnPropertyChanged(nameof(CanBrowseServers));
        }
    }

    public ServerOptionViewModel? SelectedServer
    {
        get => _selectedServer;
        set
        {
            if (_updatingToolbarServers && value is null)
            {
                return;
            }

            if (!SetProperty(ref _selectedServer, value))
            {
                return;
            }

            _servers.SetSelectedServer(value?.Id);
            _channels.SetServer(value?.Id);
        }
    }

    public bool CanBrowseServers =>
        SelectedBot is not null
        && _selectedConnectionState == BotConnectionState.Connected
        && ToolbarServers.Count > 0;

    public object CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public string CurrentTitle
    {
        get => _currentTitle;
        private set => SetProperty(ref _currentTitle, value);
    }

    public RelayCommand NavigateCommand { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(
            _bots.LoadAsync(cancellationToken),
            _dashboard.LoadAsync(cancellationToken));
    }

    public void Dispose()
    {
        _bots.Bots.CollectionChanged -= OnBotsChanged;
        _servers.ServerSelected -= OnServerExplorerSelected;
        _connectionManager.StatusChanged -= OnConnectionStatusChanged;
        _explorer.CacheChanged -= OnExplorerCacheChanged;
        _dashboard.Dispose();
        _servers.Dispose();
        _channels.Dispose();
        _bots.Dispose();
    }

    private void Navigate(object? parameter)
    {
        if (parameter is not NavigationItem item)
        {
            return;
        }

        CurrentTitle = item.Label;
        CurrentPage = item.Label switch
        {
            "Dashboard" => _dashboard,
            "Bots" => _bots,
            "Servers" => _servers,
            "Channels" => _channels,
            _ => new PlaceholderViewModel(
                item.Label,
                "This module is staged for a later milestone. The navigation and service boundaries are ready for it.")
        };
    }

    private void OnBotsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        _dashboard.UpdateTotalBots(_bots.Bots.Count);
        if (SelectedBot is not null && !_bots.Bots.Contains(SelectedBot))
        {
            SelectedBot = null;
        }

        if (SelectedBot is null && _bots.Bots.Count > 0)
        {
            SelectedBot = _bots.Bots[0];
        }
    }

    private void OnConnectionStatusChanged(object? sender, BotConnectionSnapshot snapshot)
    {
        _ = sender;
        if (SelectedBot?.Id != snapshot.BotProfileId)
        {
            return;
        }

        _dispatcher.Post(
            () =>
            {
                if (SelectedBot?.Id != snapshot.BotProfileId)
                {
                    return;
                }

                _selectedConnectionState = snapshot.State;
                if (snapshot.State != BotConnectionState.Connected)
                {
                    SelectedServer = null;
                }

                UpdateToolbarServers();
                _servers.SetConnectionState(snapshot.State);
                _channels.SetConnectionState(snapshot.State);
                OnPropertyChanged(nameof(CanBrowseServers));
            });
    }

    private void OnExplorerCacheChanged(object? sender, ExplorerCacheChanged update)
    {
        _ = sender;
        if (SelectedBot?.Id != update.BotProfileId)
        {
            return;
        }

        _dispatcher.Post(
            () =>
            {
                if (SelectedBot?.Id == update.BotProfileId)
                {
                    UpdateToolbarServers(update.Snapshot);
                }
            });
    }

    private void OnServerExplorerSelected(object? sender, ulong? serverId)
    {
        _ = sender;
        SelectedServer = serverId is ulong id
            ? ToolbarServers.FirstOrDefault(server => server.Id == id)
            : null;
    }

    private void UpdateToolbarServers(BotExplorerSnapshot? snapshot = null)
    {
        var selectedId = SelectedServer?.Id;
        _updatingToolbarServers = true;
        try
        {
            ToolbarServers.Clear();
            if (SelectedBot is not null && _selectedConnectionState == BotConnectionState.Connected)
            {
                snapshot ??= _explorer.GetSnapshot(SelectedBot.Id);
                foreach (var server in snapshot.Servers)
                {
                    ToolbarServers.Add(new ServerOptionViewModel(server));
                }
            }
        }
        finally
        {
            _updatingToolbarServers = false;
        }

        var selected = selectedId is ulong id
            ? ToolbarServers.FirstOrDefault(server => server.Id == id)
            : null;
        SelectedServer = selected;

        OnPropertyChanged(nameof(CanBrowseServers));
    }
}
