using System.Collections.ObjectModel;
using DiscordControlCenter.App.Mvvm;
using DiscordControlCenter.App.Services;
using DiscordControlCenter.Application.Bots;
using DiscordControlCenter.Core.Auditing;
using DiscordControlCenter.Core.Bots;

namespace DiscordControlCenter.App.ViewModels;

public sealed class DashboardViewModel : ObservableObject, IDisposable
{
    private readonly IBotProfileService _profileService;
    private readonly IBotConnectionManager _connectionManager;
    private readonly IAuditRepository _auditRepository;
    private readonly UiDispatcher _dispatcher;
    private int _totalBots;
    private int _connectedBots;
    private int _availableServers;
    private int? _averageLatency;
    private int _connectionProblems;
    private readonly int _activeVoiceConnections;

    public DashboardViewModel(
        IBotProfileService profileService,
        IBotConnectionManager connectionManager,
        IAuditRepository auditRepository,
        UiDispatcher dispatcher)
    {
        _profileService = profileService;
        _connectionManager = connectionManager;
        _auditRepository = auditRepository;
        _dispatcher = dispatcher;
        _activeVoiceConnections = 0;
        _connectionManager.StatusChanged += OnStatusChanged;
    }

    public int TotalBots
    {
        get => _totalBots;
        private set
        {
            if (SetProperty(ref _totalBots, value))
            {
                OnPropertyChanged(nameof(DisconnectedBots));
            }
        }
    }

    public int ConnectedBots
    {
        get => _connectedBots;
        private set
        {
            if (SetProperty(ref _connectedBots, value))
            {
                OnPropertyChanged(nameof(DisconnectedBots));
            }
        }
    }

    public int DisconnectedBots => Math.Max(0, TotalBots - ConnectedBots);

    public int AvailableServers
    {
        get => _availableServers;
        private set => SetProperty(ref _availableServers, value);
    }

    public string AverageLatency => _averageLatency is int latency ? $"{latency} ms" : "—";

    public int ConnectionProblems
    {
        get => _connectionProblems;
        private set => SetProperty(ref _connectionProblems, value);
    }

    public int ActiveVoiceConnections => _activeVoiceConnections;
    public ObservableCollection<string> RecentActions { get; } = [];

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var profilesTask = _profileService.GetAllAsync(cancellationToken);
        var auditTask = _auditRepository.GetRecentAsync(6, cancellationToken);
        await Task.WhenAll(profilesTask, auditTask);
        var profiles = await profilesTask;
        var auditEntries = await auditTask;
        TotalBots = profiles.Count;
        RefreshMetrics();
        RecentActions.Clear();
        foreach (var entry in auditEntries)
        {
            RecentActions.Add($"{entry.Timestamp.ToLocalTime():g}  •  {entry.Description}");
        }
    }

    public void UpdateTotalBots(int totalBots)
    {
        TotalBots = totalBots;
        RefreshMetrics();
    }

    public void Dispose() => _connectionManager.StatusChanged -= OnStatusChanged;

    private void OnStatusChanged(object? sender, BotConnectionSnapshot snapshot)
    {
        _ = sender;
        _ = snapshot;
        _dispatcher.Post(RefreshMetrics);
    }

    private void RefreshMetrics()
    {
        var snapshots = _connectionManager.Snapshots;
        ConnectedBots = snapshots.Count(item => item.State == BotConnectionState.Connected);
        AvailableServers = snapshots
            .Where(item => item.State == BotConnectionState.Connected)
            .Sum(item => item.ServerCount);
        var latencies = snapshots
            .Where(item => item.State == BotConnectionState.Connected)
            .Select(item => item.GatewayLatencyMilliseconds)
            .OfType<int>()
            .ToArray();
        _averageLatency = latencies.Length == 0 ? null : (int)latencies.Average();
        OnPropertyChanged(nameof(AverageLatency));
        ConnectionProblems = snapshots.Count(
            item => item.State is BotConnectionState.Faulted or BotConnectionState.Reconnecting);
    }
}
