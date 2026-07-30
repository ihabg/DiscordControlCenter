using System.Windows;
using DiscordControlCenter.App.ViewModels;

namespace DiscordControlCenter.App.Views;

public partial class ReplaceTokenWindow : Window
{
    private readonly ReplaceTokenDialogViewModel _viewModel;

    public ReplaceTokenWindow(ReplaceTokenDialogViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.RequestClose += OnRequestClose;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Loaded -= OnLoaded;
        TokenBox.Focus();
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _viewModel.Token = TokenBox.Password;
        TokenPlaceholder.Visibility = TokenBox.SecurePassword.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnTokenLostFocus(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _viewModel.ValidateToken();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _viewModel.Cancel();
    }

    private void OnRequestClose(object? sender, bool accepted)
    {
        _ = sender;
        DialogResult = accepted;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        _viewModel.RequestClose -= OnRequestClose;
        Loaded -= OnLoaded;
        Closed -= OnClosed;
        _viewModel.Dispose();
        TokenBox.Clear();
    }
}
