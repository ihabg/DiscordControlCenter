using System.Windows;
using DiscordControlCenter.App.ViewModels;

namespace DiscordControlCenter.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _ = sender;
        Loaded -= OnLoaded;
        await _viewModel.InitializeAsync(CancellationToken.None);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        Closed -= OnClosed;
        _viewModel.Dispose();
    }
}
