using System.Windows;

namespace DiscordControlCenter.UiHarness;

public partial class HarnessWindow : Window
{
    public HarnessWindow()
    {
        InitializeComponent();
        DataContext = new HarnessWindowViewModel(this);
    }
}
