using System.Windows;
using DiscordControlCenter.App.ViewModels;

namespace DiscordControlCenter.App.Views;

public partial class OperationConfirmationWindow : Window
{
    private readonly OperationConfirmationViewModel _viewModel;

    public OperationConfirmationWindow(OperationConfirmationViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.Confirmed += OnConfirmed;
        InitializeComponent();
        Closed += OnClosed;
    }

    private void OnConfirmed(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        DialogResult = true;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        _viewModel.Confirmed -= OnConfirmed;
        Closed -= OnClosed;
    }
}
