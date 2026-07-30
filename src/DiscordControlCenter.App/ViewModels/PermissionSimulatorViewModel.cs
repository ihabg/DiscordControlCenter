using System.Collections.ObjectModel;
using System.Globalization;
using DiscordControlCenter.App.Mvvm;
using DiscordControlCenter.App.Services;
using DiscordControlCenter.Application.Explorer;
using DiscordControlCenter.Core.Bots;
using DiscordControlCenter.Core.Explorer;

namespace DiscordControlCenter.App.ViewModels;

public enum PermissionSubjectKind
{
    SelectedBot,
    Member,
    Role
}

public sealed record PermissionSubjectOption(
    PermissionSubjectKind Kind,
    ulong Id,
    string Label,
    MemberReadModel? Member,
    RoleReadModel? Role)
{
    public override string ToString() => Label;
}

public sealed record ChannelOption(ChannelReadModel Model)
{
    public string Label => $"{Model.Name} · {Model.TypeName}";
    public override string ToString() => Label;
}

public sealed class PermissionSimulatorViewModel : ObservableObject, IDisposable
{
    private readonly IBotExplorerService _explorer;
    private readonly IPermissionResolutionService _permissions;
    private readonly UiDispatcher _dispatcher;
    private Guid? _botProfileId;
    private ulong? _serverId;
    private BotConnectionState _connectionState;
    private BotExplorerSnapshot? _snapshot;
    private PermissionSubjectOption? _firstSubject;
    private PermissionSubjectOption? _secondSubject;
    private ChannelOption? _selectedChannel;
    private bool _disposed;

    public PermissionSimulatorViewModel(
        IBotExplorerService explorer,
        IPermissionResolutionService permissions,
        UiDispatcher dispatcher)
    {
        _explorer = explorer;
        _permissions = permissions;
        _dispatcher = dispatcher;
        _explorer.CacheChanged += OnCacheChanged;
    }

    public ObservableCollection<PermissionSubjectOption> Subjects { get; } = [];
    public ObservableCollection<ChannelOption> Channels { get; } = [];
    public ObservableCollection<PermissionComparisonItem> Comparison { get; } = [];

    public PermissionSubjectOption? FirstSubject
    {
        get => _firstSubject;
        set
        {
            if (SetProperty(ref _firstSubject, value))
            {
                Recalculate();
            }
        }
    }

    public PermissionSubjectOption? SecondSubject
    {
        get => _secondSubject;
        set
        {
            if (SetProperty(ref _secondSubject, value))
            {
                Recalculate();
            }
        }
    }

    public ChannelOption? SelectedChannel
    {
        get => _selectedChannel;
        set
        {
            if (SetProperty(ref _selectedChannel, value))
            {
                Recalculate();
            }
        }
    }

    public bool HasContext => CurrentServer is not null
        && _connectionState == BotConnectionState.Connected;
    public string StateMessage => _botProfileId is null
        ? "Select a bot from the toolbar."
        : _connectionState != BotConnectionState.Connected
            ? "Connect the selected bot."
            : _serverId is null
                ? "Select a server from the toolbar."
                : Subjects.Count == 0
                    ? "No permission subjects are available."
                    : string.Empty;
    public string CompletenessMessage => CurrentServer?.Members.Completeness is DataCompleteness.Complete
        ? "Member-role data is complete; member explanations are exact."
        : "Incomplete member-role data produces Unknown results instead of confident explanations.";

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
        var firstKey = (FirstSubject?.Kind, FirstSubject?.Id);
        var secondKey = (SecondSubject?.Kind, SecondSubject?.Id);
        var channelId = SelectedChannel?.Model.Id;
        Subjects.Clear();
        Channels.Clear();
        var server = CurrentServer;
        if (server is not null && _connectionState == BotConnectionState.Connected)
        {
            Subjects.Add(
                new PermissionSubjectOption(
                    PermissionSubjectKind.SelectedBot,
                    server.BotUserId,
                    $"Selected bot · {server.BotNickname ?? server.BotHighestRole ?? server.BotUserId.ToString(CultureInfo.InvariantCulture)}",
                    null,
                    null));
            foreach (var member in server.Members.Members
                         .Where(member => member.Id != server.BotUserId)
                         .OrderBy(member => member.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                Subjects.Add(
                    new PermissionSubjectOption(
                        PermissionSubjectKind.Member,
                        member.Id,
                        $"Member · {member.DisplayName}",
                        member,
                        null));
            }

            foreach (var role in server.Roles.OrderByDescending(role => role.Position))
            {
                Subjects.Add(
                    new PermissionSubjectOption(
                        PermissionSubjectKind.Role,
                        role.Id,
                        $"Role · {role.Name}",
                        null,
                        role));
            }

            foreach (var channel in server.Channels
                         .Where(channel => channel.Kind != ChannelKind.Category)
                         .OrderBy(channel => channel.Position)
                         .ThenBy(channel => channel.Name, StringComparer.OrdinalIgnoreCase))
            {
                Channels.Add(new ChannelOption(channel));
            }
        }

        FirstSubject = Subjects.FirstOrDefault(subject =>
                subject.Kind == firstKey.Kind && subject.Id == firstKey.Id)
            ?? Subjects.FirstOrDefault();
        SecondSubject = Subjects.FirstOrDefault(subject =>
                subject.Kind == secondKey.Kind && subject.Id == secondKey.Id)
            ?? Subjects.Skip(1).FirstOrDefault()
            ?? Subjects.FirstOrDefault();
        SelectedChannel = Channels.FirstOrDefault(channel => channel.Model.Id == channelId)
            ?? Channels.FirstOrDefault();
        Recalculate();
        OnPropertyChanged(nameof(HasContext));
        OnPropertyChanged(nameof(StateMessage));
        OnPropertyChanged(nameof(CompletenessMessage));
    }

    private void Recalculate()
    {
        Comparison.Clear();
        var server = CurrentServer;
        if (server is null
            || _snapshot is null
            || FirstSubject is null
            || SecondSubject is null)
        {
            return;
        }

        var first = Resolve(FirstSubject, server);
        var second = Resolve(SecondSubject, server);
        foreach (var item in _permissions.Compare(first, second).Permissions)
        {
            Comparison.Add(item);
        }
    }

    private PermissionResolution Resolve(
        PermissionSubjectOption subject,
        ServerReadModel server)
    {
        var channel = SelectedChannel?.Model;
        return subject.Kind switch
        {
            PermissionSubjectKind.SelectedBot when channel is not null =>
                _permissions.ResolveChannel(
                    _snapshot!.BotProfileId,
                    _snapshot.Version,
                    server,
                    channel),
            PermissionSubjectKind.SelectedBot =>
                _permissions.ResolveServer(_snapshot!.BotProfileId, _snapshot.Version, server),
            PermissionSubjectKind.Member when subject.Member is not null =>
                _permissions.ResolveMember(
                    _snapshot!.BotProfileId,
                    _snapshot.Version,
                    server,
                    subject.Member,
                    channel),
            PermissionSubjectKind.Role when subject.Role is not null =>
                _permissions.ResolveRole(
                    _snapshot!.BotProfileId,
                    _snapshot.Version,
                    server,
                    subject.Role,
                    channel),
            _ => new PermissionResolution(PermissionBits.None, [])
        };
    }
}
