using System.Windows;

namespace DiscordControlCenter.App.Views;

public partial class DraftDiscardConfirmationWindow : Window
{
    public DraftDiscardConfirmationWindow(string actionDescription)
    {
        InitializeComponent();
        ActionText.Text = actionDescription;
    }

    private void Discard_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
