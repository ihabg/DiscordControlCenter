using System.Collections.ObjectModel;
using System.Globalization;
using DiscordControlCenter.App.Mvvm;
using DiscordControlCenter.App.Services;
using DiscordControlCenter.Application.Bots;
using DiscordControlCenter.Application.Explorer;
using DiscordControlCenter.Core.Auditing;
using DiscordControlCenter.Core.Bots;
using DiscordControlCenter.Core.Explorer;

namespace DiscordControlCenter.App.ViewModels;

public sealed class DashboardViewModel : ObservableObject, IDisposable
{
    private readonly IBotProfileService _profileService;
    private readonly IBotConnectionManager _connectionManager;
    private readonly IAuditRepository _auditRepository;
    private readonly IBotExplorerService _explorer;
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
        IBotExplorerService explorer,
        UiDispatcher dispatcher)
    {
        _profileService = profileService;
        _connectionManager = connectionManager;
        _auditRepository = auditRepository;
        _explorer = explorer;
        _dispatcher = dispatcher;
        _activeVoiceConnections = 0;
        _connectionManager.StatusChanged += OnStatusChanged;
        _explorer.CacheChanged += OnCacheChanged;
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
    public ObservableCollection<BotDiagnosticItemViewModel> Diagnostics { get; } = [];

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var profilesTask = _profileService.GetAllAsync(cancellationToken);
        var auditTask = _auditRepository.GetRecentAsync(6, cancellationToken);
        await Task.WhenAll(profilesTask, auditTask);
        var profiles = await profilesTask;
        var auditEntries = await auditTask;
        TotalBots = profiles.Count;
        RefreshMetrics();
        RefreshDiagnostics();
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

    public void Dispose()
    {
        _connectionManager.StatusChanged -= OnStatusChanged;
        _explorer.CacheChanged -= OnCacheChanged;
    }

    private void OnStatusChanged(object? sender, BotConnectionSnapshot snapshot)
    {
        _ = sender;
        _ = snapshot;
        _dispatcher.Post(
            () =>
            {
                RefreshMetrics();
                RefreshDiagnostics();
            });
    }

    private void OnCacheChanged(object? sender, ExplorerCacheChanged update)
    {
        _ = sender;
        _ = update;
        _dispatcher.Post(RefreshDiagnostics);
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

    private void RefreshDiagnostics()
    {
        Diagnostics.Clear();
        foreach (var diagnostic in _explorer.GetDiagnostics())
        {
            Diagnostics.Add(new BotDiagnosticItemViewModel(diagnostic));
        }
    }
}

public sealed class BotDiagnosticItemViewModel(BotDiagnosticsReadModel model)
{
    public string DisplayName => model.DisplayName;
    public string Connection => model.ConnectionState;
    public string Latency => model.GatewayLatencyMilliseconds is int latency ? $"{latency} ms" : "Unavailable";
    public string LastReady => Format(model.LastReadyAt);
    public string LastDisconnect => Format(model.LastDisconnectedAt);
    public string LastReconnect => Format(model.LastReconnectedAt);
    public string CacheCounts =>
        $"{model.CachedServerCount} servers · {model.CachedChannelCount} channels · {model.CachedRoleCount} roles";
    public string Members =>
        $"{model.LoadedMemberCount:N0} loaded · {model.MemberCompleteness}";
    public string Sequence => model.LastAcceptedSequence.ToString(CultureInfo.CurrentCulture);
    public string Refresh => model.IsRefreshPending
        ? "Pending"
        : Format(model.LastSuccessfulExplorerRefresh);
    public string GatewayError => model.RecentGatewayError ?? "None";
    public string GuildMembers => model.FullMemberAccessEnabled
        ? model.MemberLoadingOperational ? "Enabled · operational" : "Enabled · unavailable"
        : "Disabled · limited mode";
    public string VoiceActivity => model.LastVoiceStateEventAt is DateTimeOffset activity
        ? $"{model.VoiceStateEventCount:N0} events · {activity.ToLocalTime():G}"
        : "No observed events";
    public string CacheAge => model.CacheAge is TimeSpan age
        ? age.TotalMinutes < 1 ? $"{age.TotalSeconds:0}s" : $"{age.TotalMinutes:0.#}m"
        : "Unavailable";

    private static string Format(DateTimeOffset? value) =>
        value?.ToLocalTime().ToString("G", CultureInfo.CurrentCulture) ?? "Never";
}
