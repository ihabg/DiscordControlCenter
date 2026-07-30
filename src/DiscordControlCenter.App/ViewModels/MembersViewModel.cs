using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Threading;
using DiscordControlCenter.App.Mvvm;
using DiscordControlCenter.App.Services;
using DiscordControlCenter.Application.Explorer;
using DiscordControlCenter.Core.Bots;
using DiscordControlCenter.Core.Explorer;

namespace DiscordControlCenter.App.ViewModels;

public sealed class MembersViewModel : ObservableObject, IDisposable
{
    private static readonly string[] AvailableFilters =
        ["All members", "Humans", "Bots", "In voice", "Boosters", "Timed out", "Pending"];
    private readonly IBotExplorerService _explorer;
    private readonly UiDispatcher _dispatcher;
    private readonly DispatcherTimer _searchTimer;
    private readonly IReadOnlyList<string> _filters;
    private Guid? _botProfileId;
    private ulong? _serverId;
    private BotConnectionState _connectionState;
    private BotExplorerSnapshot? _snapshot;
    private string _searchText = string.Empty;
    private string _selectedFilter = AvailableFilters[0];
    private RoleFilterOption? _selectedRoleFilter;
    private MemberItemViewModel? _selectedMember;
    private long _generation;
    private string? _operationError;
    private bool _disposed;

    public MembersViewModel(IBotExplorerService explorer, UiDispatcher dispatcher)
    {
        _explorer = explorer;
        _dispatcher = dispatcher;
        _filters = AvailableFilters.ToArray();
        MembersView = CollectionViewSource.GetDefaultView(Members);
        MembersView.Filter = FilterMember;
        _searchTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(250),
            DispatcherPriority.Background,
            ApplyFilters,
            System.Windows.Application.Current.Dispatcher);
        _searchTimer.Stop();
        LoadMembersCommand = new AsyncRelayCommand(
            LoadMembersAsync,
            () => CanLoadMembers,
            HandleUnexpectedError);
        RetryCommand = new AsyncRelayCommand(
            LoadMembersAsync,
            () => CanLoadMembers,
            HandleUnexpectedError);
        _explorer.CacheChanged += OnCacheChanged;
        RoleFilters.Add(new RoleFilterOption(null, "All roles"));
    }

    public ObservableCollection<MemberItemViewModel> Members { get; } = [];
    public ICollectionView MembersView { get; }
    public ObservableCollection<RoleFilterOption> RoleFilters { get; } = [];
    public IReadOnlyList<string> Filters => _filters;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                RestartFilterTimer();
            }
        }
    }

    public string SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (SetProperty(ref _selectedFilter, value))
            {
                RestartFilterTimer();
            }
        }
    }

    public RoleFilterOption? SelectedRoleFilter
    {
        get => _selectedRoleFilter;
        set
        {
            if (SetProperty(ref _selectedRoleFilter, value))
            {
                RestartFilterTimer();
            }
        }
    }

    public MemberItemViewModel? SelectedMember
    {
        get => _selectedMember;
        set => SetProperty(ref _selectedMember, value);
    }

    public bool HasBot => _botProfileId is not null;
    public bool HasServer => _serverId is not null;
    public bool IsDisconnected => HasBot && _connectionState != BotConnectionState.Connected;
    public bool IsLimitedMode => CurrentMembers?.Completeness == DataCompleteness.Limited;
    public bool IsLoading => CurrentMembers?.Completeness == DataCompleteness.Loading;
    public bool HasError => CurrentMembers?.Completeness == DataCompleteness.Failed
        || !string.IsNullOrWhiteSpace(_operationError);
    public bool CanLoadMembers =>
        HasBot
        && HasServer
        && _connectionState == BotConnectionState.Connected
        && CurrentMembers?.FullAccessEnabled == true
        && !LoadMembersCommand.IsRunning;
    public string ModeTitle => IsLimitedMode ? "Limited member mode" : "Full member access";
    public string ModeMessage => IsLimitedMode
        ? "Only the bot itself and members represented by accessible voice state are shown. This is not a complete server member list. Enable Server Members Intent in the Discord Developer Portal, then enable full member access for this bot in Bot Manager."
        : CurrentMembers?.FullAccessEnabled == true
            ? "GuildMembers is enabled locally. Use Refresh members to request the complete list."
            : "Full member access is unavailable for this context.";
    public string ProgressText
    {
        get
        {
            var members = CurrentMembers;
            if (members is null)
            {
                return "No member snapshot";
            }

            var expected = members.ExpectedMemberCount?
                .ToString(CultureInfo.CurrentCulture) ?? "unknown";
            return $"{members.LoadedMemberCount:N0} loaded / {expected} expected · {members.Completeness}";
        }
    }

    public string LastRefreshedText => CurrentMembers?.LastRefreshedAt?
        .ToLocalTime()
        .ToString("G", CultureInfo.CurrentCulture) ?? "Never";
    public string StateTitle => !HasBot
        ? "Select a bot"
        : IsDisconnected
            ? "Bot is disconnected"
            : !HasServer
                ? "Select a server"
                : IsLoading
                    ? "Loading members"
                    : CurrentMembers?.Completeness == DataCompleteness.Cancelled
                        ? "Member loading cancelled"
                    : HasError
                        ? "Member loading needs attention"
                        : MembersView.IsEmpty
                            ? "No members found"
                            : string.Empty;
    public string StateMessage => !HasBot
        ? "Choose a saved bot from the toolbar."
        : IsDisconnected
            ? "Connect this bot from Bot Manager."
            : !HasServer
                ? "Choose a server from the toolbar."
                : IsLoading
                    ? "Discord.Net is paging members through Discord's rate-limit-aware API."
                    : CurrentMembers?.Completeness == DataCompleteness.Cancelled
                        ? "Any previously loaded members were retained safely. Retry when ready."
                    : HasError
                        ? _operationError ?? CurrentMembers?.ErrorMessage ?? "Member loading failed."
                        : MembersView.IsEmpty
                            ? "No member matches the current search and filters."
                            : string.Empty;

    public AsyncRelayCommand LoadMembersCommand { get; }
    public AsyncRelayCommand RetryCommand { get; }

    public void SetContext(
        Guid? botProfileId,
        BotConnectionState connectionState,
        ulong? serverId)
    {
        LoadMembersCommand.Cancel();
        RetryCommand.Cancel();
        Interlocked.Increment(ref _generation);
        _botProfileId = botProfileId;
        _connectionState = connectionState;
        _serverId = serverId;
        _snapshot = botProfileId is Guid id ? _explorer.GetSnapshot(id) : null;
        _operationError = null;
        ApplySnapshot();
    }

    public void SetConnectionState(BotConnectionState state)
    {
        _connectionState = state;
        if (state != BotConnectionState.Connected)
        {
            LoadMembersCommand.Cancel();
        }

        ApplySnapshot();
    }

    public void SetServer(ulong? serverId)
    {
        LoadMembersCommand.Cancel();
        RetryCommand.Cancel();
        Interlocked.Increment(ref _generation);
        _serverId = serverId;
        _operationError = null;
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
        _searchTimer.Tick -= ApplyFilters;
        LoadMembersCommand.Dispose();
        RetryCommand.Dispose();
    }

    private MemberCollectionReadModel? CurrentMembers => CurrentServer?.Members;
    private ServerReadModel? CurrentServer => _serverId is ulong serverId
        ? _snapshot?.Servers.FirstOrDefault(server => server.Id == serverId)
        : null;

    private async Task LoadMembersAsync(CancellationToken cancellationToken)
    {
        if (_botProfileId is not Guid botId || _serverId is not ulong serverId)
        {
            return;
        }

        var generation = Interlocked.Read(ref _generation);
        _operationError = null;
        var result = await _explorer.LoadMembersAsync(botId, serverId, cancellationToken);
        if (generation != Interlocked.Read(ref _generation)
            || _botProfileId != botId
            || _serverId != serverId)
        {
            return;
        }

        if (!result.IsSuccess)
        {
            _operationError = result.Error;
        }

        _snapshot = _explorer.GetSnapshot(botId);
        ApplySnapshot();
    }

    private void OnCacheChanged(object? sender, ExplorerCacheChanged update)
    {
        _ = sender;
        if (_botProfileId != update.BotProfileId)
        {
            return;
        }

        _dispatcher.Post(
            () =>
            {
                if (_botProfileId == update.BotProfileId)
                {
                    _snapshot = update.Snapshot;
                    ApplySnapshot();
                }
            });
    }

    private void ApplySnapshot()
    {
        var selectedId = SelectedMember?.Id;
        var server = CurrentServer;
        SyncMembers(server?.Members.Members ?? []);
        SyncRoles(server?.Roles ?? []);
        SelectedMember = selectedId is ulong id
            ? Members.FirstOrDefault(member => member.Id == id)
            : null;
        MembersView.Refresh();
        NotifyState();
    }

    private void SyncMembers(IEnumerable<MemberReadModel> models)
    {
        var incoming = models.ToDictionary(model => model.Id);
        for (var index = Members.Count - 1; index >= 0; index--)
        {
            if (!incoming.ContainsKey(Members[index].Id))
            {
                Members.RemoveAt(index);
            }
        }

        foreach (var model in incoming.Values
                     .OrderBy(member => member.DisplayName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(member => member.Id))
        {
            var existing = Members.FirstOrDefault(member => member.Id == model.Id);
            if (existing is null)
            {
                Members.Add(new MemberItemViewModel(model));
            }
            else
            {
                existing.Update(model);
            }
        }
    }

    private void SyncRoles(IEnumerable<RoleReadModel> roles)
    {
        var selectedId = SelectedRoleFilter?.Id;
        RoleFilters.Clear();
        RoleFilters.Add(new RoleFilterOption(null, "All roles"));
        foreach (var role in roles
                     .Where(role => !role.IsEveryone)
                     .OrderByDescending(role => role.Position)
                     .ThenBy(role => role.Name, StringComparer.OrdinalIgnoreCase))
        {
            RoleFilters.Add(new RoleFilterOption(role.Id, role.Name));
        }

        SelectedRoleFilter = RoleFilters.FirstOrDefault(role => role.Id == selectedId)
            ?? RoleFilters[0];
    }

    private bool FilterMember(object item)
    {
        if (item is not MemberItemViewModel member)
        {
            return false;
        }

        var term = SearchText.Trim();
        var searchMatches = string.IsNullOrEmpty(term)
            || member.Model.Username.Contains(term, StringComparison.OrdinalIgnoreCase)
            || (member.Model.GlobalDisplayName?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
            || (member.Model.Nickname?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
            || member.IdText.Contains(term, StringComparison.OrdinalIgnoreCase);
        var filterMatches = SelectedFilter switch
        {
            "Humans" => !member.IsBot,
            "Bots" => member.IsBot,
            "In voice" => member.IsInVoice,
            "Boosters" => member.IsBoosting,
            "Timed out" => member.IsTimedOut,
            "Pending" => member.IsPending,
            _ => true
        };
        var roleMatches = SelectedRoleFilter?.Id is not ulong roleId
            || member.Model.RoleIds.Contains(roleId);
        return searchMatches && filterMatches && roleMatches;
    }

    private void RestartFilterTimer()
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void ApplyFilters(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        _searchTimer.Stop();
        MembersView.Refresh();
        NotifyState();
    }

    private void HandleUnexpectedError(Exception exception)
    {
        _ = exception;
        _operationError = "An unexpected Members Explorer error occurred.";
        NotifyState();
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(HasBot));
        OnPropertyChanged(nameof(HasServer));
        OnPropertyChanged(nameof(IsDisconnected));
        OnPropertyChanged(nameof(IsLimitedMode));
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(CanLoadMembers));
        OnPropertyChanged(nameof(ModeTitle));
        OnPropertyChanged(nameof(ModeMessage));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(LastRefreshedText));
        OnPropertyChanged(nameof(StateTitle));
        OnPropertyChanged(nameof(StateMessage));
        LoadMembersCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
    }
}
