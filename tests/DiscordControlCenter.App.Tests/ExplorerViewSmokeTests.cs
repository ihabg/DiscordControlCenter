using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Windows;
using DiscordControlCenter.App.Mvvm;
using DiscordControlCenter.App.ViewModels;
using DiscordControlCenter.App.Views;
using DiscordControlCenter.Core.Explorer;

namespace DiscordControlCenter.App.Tests;

public sealed class ExplorerViewSmokeTests
{
    [Fact]
    public void PopulatedExplorerTemplatesMeasureWithoutExceptions()
    {
        Exception? failure = null;
        var thread = new Thread(
            () =>
            {
                try
                {
                    var application = new App();
                    application.InitializeComponent();
                    var server = CreateServer();
                    var permission = new PermissionResolution(
                        PermissionBits.ViewChannel,
                        Array.Empty<PermissionResult>());
                    var serverItem = new ServerItemViewModel(server, permission);
                    var channelItem = new ChannelItemViewModel(server.Channels[0], permission);
                    var command = new RelayCommand(_ => { });
                    var serverView = new ServersView
                    {
                        DataContext = new ServerPageData(serverItem, command)
                    };
                    var channelView = new ChannelsView
                    {
                        DataContext = new ChannelPageData(channelItem, command)
                    };
                    var panel = new System.Windows.Controls.StackPanel();
                    panel.Children.Add(serverView);
                    panel.Children.Add(channelView);
                    var window = new Window
                    {
                        Width = 1400,
                        Height = 900,
                        Content = panel,
                        ShowInTaskbar = false
                    };
                    window.Show();
                    window.UpdateLayout();
                    window.Close();
                    application.Shutdown();
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }

    private static ServerReadModel CreateServer()
    {
        var channel = new ChannelReadModel(
            10,
            "general",
            ChannelKind.Text,
            "Text",
            1,
            DateTimeOffset.UtcNow,
            null,
            null,
            null,
            ImmutableArray<PermissionOverwriteReadModel>.Empty,
            "Topic",
            false,
            0,
            60,
            null,
            null,
            null,
            null,
            ImmutableArray<string>.Empty,
            null,
            null,
            null);
        return new ServerReadModel(
            1,
            "Test server",
            null,
            "Description",
            2,
            DateTimeOffset.UtcNow,
            5,
            0,
            1,
            0,
            0,
            0,
            1,
            0,
            "None",
            0,
            null,
            "@everyone",
            0,
            99,
            ImmutableArray<ulong>.Empty,
            ImmutableArray<RoleReadModel>.Empty,
            [channel],
            ServerAvailability.Available,
            DateTimeOffset.UtcNow);
    }

    private sealed class ServerPageData(ServerItemViewModel server, RelayCommand command)
    {
        public ObservableCollection<ServerItemViewModel> Servers { get; } = [server];
        public ServerItemViewModel? SelectedServer { get; set; }
        public RelayCommand RefreshCommand { get; } = command;
        public string SearchText { get; set; } = string.Empty;
        public bool HasServers { get; } = true;
        public bool IsLoading { get; }
        public bool HasError { get; }
        public string StateTitle { get; } = string.Empty;
        public string StateMessage { get; } = string.Empty;
    }

    private sealed class ChannelPageData(ChannelItemViewModel channel, RelayCommand command)
    {
        public ObservableCollection<ChannelGroupViewModel> ChannelGroups { get; } =
            [new("Uncategorized", null, [channel])];
        public ChannelItemViewModel? SelectedChannel { get; } = channel;
        public RelayCommand SelectChannelCommand { get; } = command;
        public RelayCommand RefreshCommand { get; } = command;
        public string SearchText { get; set; } = string.Empty;
        public string ServerName { get; } = "Test server";
        public bool HasChannels { get; } = true;
        public bool IsLoading { get; }
        public bool HasError { get; }
        public string StateTitle { get; } = string.Empty;
        public string StateMessage { get; } = string.Empty;
    }
}
