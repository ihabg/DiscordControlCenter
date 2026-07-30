using System.Collections.ObjectModel;
using DiscordControlCenter.App.Mvvm;
using DiscordControlCenter.App.Services;
using DiscordControlCenter.Application.Explorer;
using DiscordControlCenter.Core.Bots;
using DiscordControlCenter.Core.Explorer;

namespace DiscordControlCenter.App.ViewModels;

public sealed class RolesViewModel : ObservableObject, IDisposable
{
    private readonly IBotExplorerService _explorer;
    private readonly IRoleHierarchySafetyService _hierarchy;
    private readonly UiDispatcher _dispatcher;
    private Guid? _botProfileId;
    private ulong? _serverId;
    private BotConnectionState _connectionState;
    private BotExplorerSnapshot? _snapshot;
    private RoleItemViewModel? _selectedRole;
    private bool _disposed;

    public RolesViewModel(
        IBotExplorerService explorer,
        IRoleHierarchySafetyService hierarchy,
        UiDispatcher dispatcher)
    {
        _explorer = explorer;
        _hierarchy = hierarchy;
        _dispatcher = dispatcher;
        _explorer.CacheChanged += OnCacheChanged;
    }

    public ObservableCollection<RoleItemViewModel> Roles { get; } = [];

    public RoleItemViewModel? SelectedRole
    {
        get => _selectedRole;
        set => SetProperty(ref _selectedRole, value);
    }

    public bool HasBot => _botProfileId is not null;
    public bool HasServer => _serverId is not null;
    public bool IsDisconnected => HasBot && _connectionState != BotConnectionState.Connected;
    public bool HasRoles => Roles.Count > 0;
    public string CompletenessText => CurrentServer?.Members.Completeness switch
    {
        DataCompleteness.Complete => "Member counts are exact",
        DataCompleteness.Partial or DataCompleteness.Loading => "Member counts are partial",
        _ => "Member counts are unavailable"
    };
    public string StateTitle => !HasBot
        ? "Select a bot"
        : IsDisconnected
            ? "Bot is disconnected"
            : !HasServer
                ? "Select a server"
                : HasRoles ? string.Empty : "No roles available";
    public string StateMessage => !HasBot
        ? "Choose a saved bot from the toolbar."
        : IsDisconnected
            ? "Connect this bot from Bot Manager."
            : !HasServer
                ? "Choose a server from the toolbar."
                : HasRoles
                    ? string.Empty
                    : "The bot cannot currently see role metadata for this server.";

    public void SetContext(
        Guid? botProfileId,
        BotConnectionState connectionState,
        ulong? serverId)
    {
        _botProfileId = botProfileId;
        _connectionState = connectionState;
        _serverId = serverId;
        _snapshot = botProfileId is Guid id ? _explorer.GetSnapshot(id) : null;
        ApplySnapshot();
    }

    public void SetConnectionState(BotConnectionState state)
    {
        _connectionState = state;
        ApplySnapshot();
    }

    public void SetServer(ulong? serverId)
    {
        _serverId = serverId;
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
    }

    private ServerReadModel? CurrentServer => _serverId is ulong serverId
        ? _snapshot?.Servers.FirstOrDefault(server => server.Id == serverId)
        : null;

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
                _snapshot = update.Snapshot;
                ApplySnapshot();
            });
    }

    private void ApplySnapshot()
    {
        var selectedId = SelectedRole?.Id;
        Roles.Clear();
        var server = CurrentServer;
        if (server is not null)
        {
            foreach (var role in ExplorerSearch.OrderRoles(server.Roles))
            {
                Roles.Add(
                    new RoleItemViewModel(
                        role,
                        _hierarchy.CanManageRole(server, role),
                        _hierarchy.CanAssignRole(server, role),
                        server.BotRoleIds.Contains(role.Id)
                            && role.Position == server.BotRolePosition));
            }
        }

        SelectedRole = selectedId is ulong id
            ? Roles.FirstOrDefault(role => role.Id == id)
            : null;
        NotifyState();
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(HasBot));
        OnPropertyChanged(nameof(HasServer));
        OnPropertyChanged(nameof(IsDisconnected));
        OnPropertyChanged(nameof(HasRoles));
        OnPropertyChanged(nameof(CompletenessText));
        OnPropertyChanged(nameof(StateTitle));
        OnPropertyChanged(nameof(StateMessage));
    }
}
