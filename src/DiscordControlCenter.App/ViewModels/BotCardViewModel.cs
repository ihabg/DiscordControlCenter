using System.Globalization;
using DiscordControlCenter.App.Mvvm;
using DiscordControlCenter.App.Services;
using DiscordControlCenter.Application.Bots;
using DiscordControlCenter.Core.Bots;

namespace DiscordControlCenter.App.ViewModels;

public sealed class BotCardViewModel : ObservableObject, IDisposable
{
    private readonly IBotConnectionManager _connectionManager;
    private readonly IBotProfileService _profileService;
    private readonly IUserDialogService _dialogs;
    private BotProfile _profile;
    private BotConnectionSnapshot _snapshot;
    private string? _operationError;

    public BotCardViewModel(
        BotProfile profile,
        BotConnectionSnapshot snapshot,
        IBotConnectionManager connectionManager,
        IBotProfileService profileService,
        IUserDialogService dialogs)
    {
        _profile = profile;
        _snapshot = snapshot;
        _connectionManager = connectionManager;
        _profileService = profileService;
        _dialogs = dialogs;
        ConnectCommand = new AsyncRelayCommand(
            ConnectAsync,
            () => State is BotConnectionState.Disconnected or BotConnectionState.Faulted,
            HandleUnexpectedError);
        DisconnectCommand = new AsyncRelayCommand(
            DisconnectAsync,
            () => State is not BotConnectionState.Disconnected and not BotConnectionState.Disconnecting,
            HandleUnexpectedError);
        RemoveCommand = new AsyncRelayCommand(RemoveAsync, CanRemove, HandleUnexpectedError);
        ReplaceTokenCommand = new AsyncRelayCommand(
            ReplaceTokenAsync,
            CanRemove,
            HandleUnexpectedError);
        ErrorDetailsCommand = new RelayCommand(
            _ => _dialogs.ShowBotError(DisplayName, ErrorMessage ?? "No details are available."),
            _ => HasError);
    }

    public event EventHandler<Guid>? Removed;

    public Guid Id => _profile.Id;
    public string DisplayName => _profile.DisplayName;
    public string Username => _snapshot.Identity?.Username ?? _profile.DiscordUsername ?? "Not connected";
    public string UserId => (_snapshot.Identity?.UserId ?? _profile.DiscordUserId)?
        .ToString(CultureInfo.InvariantCulture) ?? "—";
    public string? AvatarUrl => _snapshot.Identity?.AvatarUrl ?? _profile.AvatarUrl;
    public string MaskedToken =>
        _profile.ProtectedToken.Length == 0 ? "Unavailable" : BotProfile.MaskedToken;
    public BotConnectionState State => _snapshot.State;
    public string StateText => State.ToString();
    public string LatencyText => _snapshot.GatewayLatencyMilliseconds is int latency ? $"{latency} ms" : "—";
    public string ServerCountText => State == BotConnectionState.Connected
        ? _snapshot.ServerCount.ToString(CultureInfo.CurrentCulture)
        : "—";
    public string LastConnectedText =>
        (_snapshot.LastConnectedAt ?? _profile.LastConnectedAt)?
            .ToLocalTime()
            .ToString("g", CultureInfo.CurrentCulture) ?? "Never";
    public string? ErrorMessage => _snapshot.ErrorMessage ?? _operationError;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand DisconnectCommand { get; }
    public AsyncRelayCommand RemoveCommand { get; }
    public AsyncRelayCommand ReplaceTokenCommand { get; }
    public RelayCommand ErrorDetailsCommand { get; }

    public void Update(BotConnectionSnapshot snapshot)
    {
        if (snapshot.BotProfileId != Id)
        {
            return;
        }

        _snapshot = snapshot;
        _operationError = null;
        NotifySnapshotChanged();
    }

    public void Dispose()
    {
        ConnectCommand.Dispose();
        DisconnectCommand.Dispose();
        RemoveCommand.Dispose();
        ReplaceTokenCommand.Dispose();
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        _operationError = null;
        var result = await _connectionManager.ConnectAsync(Id, cancellationToken);
        if (!result.IsSuccess)
        {
            _operationError = result.Error;
            OnPropertyChanged(nameof(ErrorMessage));
            OnPropertyChanged(nameof(HasError));
            _dialogs.ShowError("Connection failed", result.Error ?? "The bot could not connect.");
        }
    }

    private async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        var result = await _connectionManager.DisconnectAsync(Id, cancellationToken);
        if (!result.IsSuccess)
        {
            _dialogs.ShowError("Disconnect failed", result.Error ?? "The bot could not disconnect.");
        }
    }

    private async Task RemoveAsync(CancellationToken cancellationToken)
    {
        if (!_dialogs.ConfirmRemove(_profile))
        {
            return;
        }

        var result = await _profileService.RemoveAsync(Id, cancellationToken);
        if (!result.IsSuccess)
        {
            _dialogs.ShowError("Remove failed", result.Error ?? "The bot profile could not be removed.");
            return;
        }

        Removed?.Invoke(this, Id);
    }

    private async Task ReplaceTokenAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var updated = await _dialogs.ShowReplaceTokenAsync(_profile);
        if (updated is null)
        {
            return;
        }

        _profile = updated;
        OnPropertyChanged(nameof(Username));
        OnPropertyChanged(nameof(UserId));
        OnPropertyChanged(nameof(AvatarUrl));
        OnPropertyChanged(nameof(MaskedToken));
    }

    private bool CanRemove() =>
        State is BotConnectionState.Disconnected or BotConnectionState.Faulted;

    private void HandleUnexpectedError(Exception exception)
    {
        _ = exception;
        _operationError = "An unexpected operation error occurred.";
        OnPropertyChanged(nameof(ErrorMessage));
        OnPropertyChanged(nameof(HasError));
    }

    private void NotifySnapshotChanged()
    {
        OnPropertyChanged(nameof(Username));
        OnPropertyChanged(nameof(UserId));
        OnPropertyChanged(nameof(AvatarUrl));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(LatencyText));
        OnPropertyChanged(nameof(ServerCountText));
        OnPropertyChanged(nameof(LastConnectedText));
        OnPropertyChanged(nameof(ErrorMessage));
        OnPropertyChanged(nameof(HasError));
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
        ReplaceTokenCommand.NotifyCanExecuteChanged();
        ErrorDetailsCommand.NotifyCanExecuteChanged();
    }
}
