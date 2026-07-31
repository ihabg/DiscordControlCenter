using System.Windows;
using System.Windows.Input;
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

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        SystemCommands.MinimizeWindow(this);
    }

    private void OnMaximizeRestoreClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
            return;
        }

        SystemCommands.MaximizeWindow(this);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        SystemCommands.CloseWindow(this);
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        MaximizeRestoreButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        _ = sender;
        if (e.Key == Key.Escape && ToolbarSearchBox.IsKeyboardFocusWithin && !string.IsNullOrEmpty(ToolbarSearchBox.Text))
        {
            ToolbarSearchBox.Clear();
            e.Handled = true;
        }
    }
}
