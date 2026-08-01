using System.Windows;
using System.Windows.Controls;

namespace DiscordControlCenter.App.Views;

public partial class ScheduledMessagesView : UserControl
{
    public ScheduledMessagesView() => InitializeComponent();

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 1180;
        ListColumn.Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(1.1, GridUnitType.Star);
        ListColumn.MinWidth = narrow ? 0 : 320;
        SplitterColumn.Width = narrow ? new GridLength(0) : new GridLength(12);
        DetailColumn.Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(1, GridUnitType.Star);
        DetailColumn.MinWidth = narrow ? 0 : 300;
        ListRow.Height = narrow ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        SplitterRow.Height = narrow ? new GridLength(12) : new GridLength(0);
        DetailRow.Height = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(ListPane, 0); Grid.SetRow(ListPane, 0);
        Grid.SetColumn(ScheduleSplitter, narrow ? 0 : 1); Grid.SetRow(ScheduleSplitter, narrow ? 1 : 0);
        Grid.SetColumn(DetailPane, narrow ? 0 : 2); Grid.SetRow(DetailPane, narrow ? 2 : 0);
        ScheduleSplitter.Height = narrow ? 12 : double.NaN;
        ScheduleSplitter.Width = narrow ? double.NaN : 12;
    }
}
