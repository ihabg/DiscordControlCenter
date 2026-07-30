using System.Collections.ObjectModel;
using System.Collections.Specialized;
using DiscordControlCenter.App.Mvvm;

namespace DiscordControlCenter.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly DashboardViewModel _dashboard;
    private readonly BotsViewModel _bots;
    private object _currentPage;
    private string _currentTitle = "Dashboard";
    private BotCardViewModel? _selectedBot;

    public MainWindowViewModel(DashboardViewModel dashboard, BotsViewModel bots)
    {
        _dashboard = dashboard;
        _bots = bots;
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
    }

    public ObservableCollection<NavigationItem> Navigation { get; }
    public ObservableCollection<BotCardViewModel> Bots => _bots.Bots;
    public string SearchText
    {
        get => _bots.SearchText;
        set => _bots.SearchText = value;
    }

    public BotCardViewModel? SelectedBot
    {
        get => _selectedBot;
        set => SetProperty(ref _selectedBot, value);
    }

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
        _dashboard.Dispose();
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
}
