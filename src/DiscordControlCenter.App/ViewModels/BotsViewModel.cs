using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using DiscordControlCenter.App.Mvvm;
using DiscordControlCenter.App.Services;
using DiscordControlCenter.Application.Bots;
using DiscordControlCenter.Core.Bots;

namespace DiscordControlCenter.App.ViewModels;

public sealed class BotsViewModel : ObservableObject, IDisposable
{
    private readonly IBotProfileService _profileService;
    private readonly IBotConnectionManager _connectionManager;
    private readonly IUserDialogService _dialogs;
    private readonly UiDispatcher _dispatcher;
    private readonly DispatcherTimer _searchTimer;
    private string _searchText = string.Empty;
    private bool _isLoading;
    private string? _errorMessage;
    private bool _disposed;

    public BotsViewModel(
        IBotProfileService profileService,
        IBotConnectionManager connectionManager,
        IUserDialogService dialogs,
        UiDispatcher dispatcher)
    {
        _profileService = profileService;
        _connectionManager = connectionManager;
        _dialogs = dialogs;
        _dispatcher = dispatcher;
        BotsView = CollectionViewSource.GetDefaultView(Bots);
        BotsView.Filter = FilterBot;
        _searchTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(250),
            DispatcherPriority.Background,
            ApplySearch,
            System.Windows.Application.Current.Dispatcher);
        _searchTimer.Stop();
        AddBotCommand = new AsyncRelayCommand(AddBotAsync, errorHandler: HandleUnexpectedError);
        ConnectAllCommand = new AsyncRelayCommand(
            ConnectAllAsync,
            () => Bots.Count > 0,
            HandleUnexpectedError);
        DisconnectAllCommand = new AsyncRelayCommand(
            DisconnectAllAsync,
            () => Bots.Any(bot => bot.State != BotConnectionState.Disconnected),
            HandleUnexpectedError);
        RetryLoadCommand = new AsyncRelayCommand(LoadAsync, errorHandler: HandleUnexpectedError);
        _connectionManager.StatusChanged += OnStatusChanged;
    }

    public ObservableCollection<BotCardViewModel> Bots { get; } = [];
    public ICollectionView BotsView { get; }

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

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool IsEmpty => !IsLoading && !HasError && Bots.Count == 0;
    public bool IsOffline => Bots.Count > 0 && Bots.All(bot => bot.State == BotConnectionState.Disconnected);

    public AsyncRelayCommand AddBotCommand { get; }
    public AsyncRelayCommand ConnectAllCommand { get; }
    public AsyncRelayCommand DisconnectAllCommand { get; }
    public AsyncRelayCommand RetryLoadCommand { get; }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var profiles = await _profileService.GetAllAsync(cancellationToken);
            var snapshots = _connectionManager.Snapshots.ToDictionary(item => item.BotProfileId);
            foreach (var existing in Bots)
            {
                existing.Removed -= OnBotRemoved;
                existing.Dispose();
            }

            Bots.Clear();
            foreach (var profile in profiles)
            {
                var snapshot = snapshots.GetValueOrDefault(
                    profile.Id,
                    BotConnectionSnapshot.Disconnected(profile.Id));
                AddCard(profile, snapshot);
            }

            NotifyCollectionState();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            ErrorMessage = "Bot profiles could not be loaded from local storage.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connectionManager.StatusChanged -= OnStatusChanged;
        _searchTimer.Stop();
        _searchTimer.Tick -= ApplySearch;
        AddBotCommand.Dispose();
        ConnectAllCommand.Dispose();
        DisconnectAllCommand.Dispose();
        RetryLoadCommand.Dispose();
        foreach (var bot in Bots)
        {
            bot.Removed -= OnBotRemoved;
            bot.Dispose();
        }
    }

    private async Task AddBotAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var profile = await _dialogs.ShowAddBotAsync();
        if (profile is null)
        {
            return;
        }

        AddCard(profile, BotConnectionSnapshot.Disconnected(profile.Id));
        NotifyCollectionState();
    }

    private async Task ConnectAllAsync(CancellationToken cancellationToken)
    {
        await _connectionManager.ConnectAllAsync(cancellationToken);
        NotifyCollectionState();
    }

    private async Task DisconnectAllAsync(CancellationToken cancellationToken)
    {
        await _connectionManager.DisconnectAllAsync(cancellationToken);
        NotifyCollectionState();
    }

    private void AddCard(BotProfile profile, BotConnectionSnapshot snapshot)
    {
        var card = new BotCardViewModel(
            profile,
            snapshot,
            _connectionManager,
            _profileService,
            _dialogs);
        card.Removed += OnBotRemoved;
        Bots.Add(card);
    }

    private void OnStatusChanged(object? sender, BotConnectionSnapshot snapshot) =>
        _dispatcher.Post(
            () =>
            {
                Bots.FirstOrDefault(bot => bot.Id == snapshot.BotProfileId)?.Update(snapshot);
                NotifyCollectionState();
            });

    private void OnBotRemoved(object? sender, Guid id)
    {
        var bot = Bots.FirstOrDefault(item => item.Id == id);
        if (bot is null)
        {
            return;
        }

        bot.Removed -= OnBotRemoved;
        Bots.Remove(bot);
        bot.Dispose();
        NotifyCollectionState();
    }

    private bool FilterBot(object item)
    {
        if (item is not BotCardViewModel bot || string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        return bot.DisplayName.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || bot.Username.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || bot.UserId.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    private void ApplySearch(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        _searchTimer.Stop();
        BotsView.Refresh();
    }

    private void NotifyCollectionState()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsOffline));
        ConnectAllCommand.NotifyCanExecuteChanged();
        DisconnectAllCommand.NotifyCanExecuteChanged();
    }

    private void HandleUnexpectedError(Exception exception)
    {
        _ = exception;
        ErrorMessage = "An unexpected Bot Manager error occurred.";
    }
}
