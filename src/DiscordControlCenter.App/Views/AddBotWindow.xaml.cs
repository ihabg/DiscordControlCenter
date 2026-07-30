using System.Windows;
using DiscordControlCenter.App.ViewModels;

namespace DiscordControlCenter.App.Views;

public partial class AddBotWindow : Window
{
    private readonly AddBotDialogViewModel _viewModel;

    public AddBotWindow(AddBotDialogViewModel viewModel)
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
        DisplayNameBox.Focus();
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

    private void OnDisplayNameLostFocus(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _viewModel.ValidateDisplayName();
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
