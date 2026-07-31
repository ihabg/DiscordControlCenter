using System.Windows;

namespace DiscordControlCenter.App.Views;

public partial class ScheduledApprovalDecisionWindow : Window
{
    public ScheduledApprovalDecisionWindow(object viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
