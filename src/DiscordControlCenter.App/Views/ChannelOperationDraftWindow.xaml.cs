using System.Windows;
using DiscordControlCenter.App.ViewModels;
using DiscordControlCenter.Core.Operations;

namespace DiscordControlCenter.App.Views;

public partial class ChannelOperationDraftWindow : Window
{
    private readonly ChannelOperationDraftViewModel _viewModel;

    public ChannelOperationDraftWindow(ChannelOperationDraftViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public OperationPlan? Plan { get; private set; }

    private void GeneratePreviewClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Plan = _viewModel.TryBuildPlan();
        if (Plan is not null)
        {
            DialogResult = true;
        }
    }
}
