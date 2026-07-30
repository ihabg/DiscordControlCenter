using System.Collections.ObjectModel;
using System.Globalization;
using DiscordControlCenter.App.Mvvm;
using DiscordControlCenter.App.Services;
using DiscordControlCenter.Application.Explorer;
using DiscordControlCenter.Core.Bots;
using DiscordControlCenter.Core.Explorer;

namespace DiscordControlCenter.App.ViewModels;

public sealed class VoiceChannelItemViewModel(
    ChannelReadModel model,
    PermissionResolution permissions)
{
    public ChannelReadModel Model { get; } = model;
    public ulong Id => Model.Id;
    public string Name => Model.Name;
    public string IdText => Id.ToString(CultureInfo.InvariantCulture);
    public string Category => Model.CategoryName ?? "Uncategorized";
    public string TypeName => Model.Kind == ChannelKind.Stage ? "Stage" : "Voice";
    public string UserLimitText => Model.UserLimit is int limit
        ? limit == 0 ? "Unlimited" : limit.ToString(CultureInfo.CurrentCulture)
        : "Unavailable";
    public string BitrateText => Model.Bitrate is int bitrate
        ? $"{bitrate / 1000d:0.#} kbps"
        : "Unavailable";
    public string OccupancyText => $"{Model.VoiceMembers.Length:N0} connected";
    public string ViewPermission => Format(PermissionBits.ViewChannel);
    public string ConnectPermission => Format(PermissionBits.Connect);
    public string SpeakPermission => Format(PermissionBits.Speak);
    public string MovePermission => Format(PermissionBits.MoveMembers);
    public string MutePermission => Format(PermissionBits.MuteMembers);
    public string DeafenPermission => Format(PermissionBits.DeafenMembers);

    private string Format(PermissionBits permission)
    {
        var result = permissions.Permissions.First(item => item.Permission == permission);
        return result.Status switch
        {
            PermissionStatus.Allowed => "Allowed",
            PermissionStatus.AllowedThroughAdministrator => "Administrator",
            PermissionStatus.Unknown => "Unknown",
            PermissionStatus.NotApplicable => "Not applicable",
            _ => "Denied"
        };
    }
}

public sealed class VoiceMemberItemViewModel(VoiceStateReadModel model)
{
    public VoiceStateReadModel Model { get; } = model;
    public ulong Id => Model.UserId;
    public string DisplayName => Model.DisplayName;
    public string IdText => Id.ToString(CultureInfo.InvariantCulture);
    public string AccountType => Model.IsBot ? "Bot" : "Human";
    public string SelfMuted => YesNo(Model.IsSelfMuted);
    public string SelfDeafened => YesNo(Model.IsSelfDeafened);
    public string ServerMuted => YesNo(Model.IsServerMuted);
    public string ServerDeafened => YesNo(Model.IsServerDeafened);
    public string Streaming => YesNo(Model.IsStreaming);
    public string Video => YesNo(Model.IsVideoing);
    public string Suppressed => YesNo(Model.IsSuppressed);
    public string RequestToSpeak => Model.RequestToSpeakAt?
        .ToLocalTime()
        .ToString("G", CultureInfo.CurrentCulture) ?? "Unavailable";

    private static string YesNo(bool value) => value ? "Yes" : "No";
}

public sealed class VoiceViewModel : ObservableObject, IDisposable
{
    private readonly IBotExplorerService _explorer;
    private readonly IPermissionResolutionService _permissions;
    private readonly UiDispatcher _dispatcher;
    private Guid? _botProfileId;
    private ulong? _serverId;
    private BotConnectionState _connectionState;
    private BotExplorerSnapshot? _snapshot;
    private VoiceChannelItemViewModel? _selectedChannel;
    private VoiceMemberItemViewModel? _selectedMember;
    private bool _disposed;

    public VoiceViewModel(
        IBotExplorerService explorer,
        IPermissionResolutionService permissions,
        UiDispatcher dispatcher)
    {
        _explorer = explorer;
        _permissions = permissions;
        _dispatcher = dispatcher;
        _explorer.CacheChanged += OnCacheChanged;
    }

    public ObservableCollection<VoiceChannelItemViewModel> Channels { get; } = [];
    public ObservableCollection<VoiceMemberItemViewModel> Members { get; } = [];

    public VoiceChannelItemViewModel? SelectedChannel
    {
        get => _selectedChannel;
        set
        {
            if (SetProperty(ref _selectedChannel, value))
            {
                ApplyMembers();
            }
        }
    }

    public VoiceMemberItemViewModel? SelectedMember
    {
        get => _selectedMember;
        set => SetProperty(ref _selectedMember, value);
    }

    public bool HasContext => CurrentServer is not null
        && _connectionState == BotConnectionState.Connected;
    public bool HasChannels => Channels.Count > 0;
    public string StateTitle => _botProfileId is null
        ? "Select a bot"
        : _connectionState != BotConnectionState.Connected
            ? "Bot is disconnected"
            : _serverId is null
                ? "Select a server"
                : HasChannels ? string.Empty : "No accessible voice channels";
    public string StateMessage => _botProfileId is null
        ? "Choose a bot from the toolbar."
        : _connectionState != BotConnectionState.Connected
            ? "Connect this bot from Bot Manager."
            : _serverId is null
                ? "Choose a server from the toolbar."
                : HasChannels
                    ? string.Empty
                    : "The bot cannot currently see a voice or stage channel.";

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
        var selectedId = SelectedChannel?.Id;
        Channels.Clear();
        var server = CurrentServer;
        if (server is not null && _snapshot is not null)
        {
            foreach (var channel in server.Channels
                         .Where(channel => channel.Kind is ChannelKind.Voice or ChannelKind.Stage)
                         .OrderBy(channel => channel.Position)
                         .ThenBy(channel => channel.Name, StringComparer.OrdinalIgnoreCase))
            {
                Channels.Add(
                    new VoiceChannelItemViewModel(
                        channel,
                        _permissions.ResolveChannel(
                            _snapshot.BotProfileId,
                            _snapshot.Version,
                            server,
                            channel)));
            }
        }

        SelectedChannel = selectedId is ulong id
            ? Channels.FirstOrDefault(channel => channel.Id == id)
            : Channels.FirstOrDefault();
        ApplyMembers();
        OnPropertyChanged(nameof(HasContext));
        OnPropertyChanged(nameof(HasChannels));
        OnPropertyChanged(nameof(StateTitle));
        OnPropertyChanged(nameof(StateMessage));
    }

    private void ApplyMembers()
    {
        var selectedId = SelectedMember?.Id;
        Members.Clear();
        if (SelectedChannel is not null)
        {
            foreach (var member in SelectedChannel.Model.VoiceMembers
                         .OrderBy(member => member.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                Members.Add(new VoiceMemberItemViewModel(member));
            }
        }

        SelectedMember = selectedId is ulong id
            ? Members.FirstOrDefault(member => member.Id == id)
            : null;
    }
}
