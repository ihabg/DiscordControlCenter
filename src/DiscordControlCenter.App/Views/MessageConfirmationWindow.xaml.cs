using System.Windows;
using DiscordControlCenter.App.ViewModels;

namespace DiscordControlCenter.App.Views;

public partial class MessageConfirmationWindow : Window
{
    public MessageConfirmationWindow(MessageConfirmationViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MessageConfirmationViewModel { CanConfirm: true })
        {
            DialogResult = true;
        }
    }
}
