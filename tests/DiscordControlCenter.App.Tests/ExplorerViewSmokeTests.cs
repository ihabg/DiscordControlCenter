using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Windows;
using DiscordControlCenter.App.Mvvm;
using DiscordControlCenter.App.Services;
using DiscordControlCenter.App.ViewModels;
using DiscordControlCenter.App.Views;
using DiscordControlCenter.Application.Explorer;
using DiscordControlCenter.Application.Operations;
using DiscordControlCenter.Core.Bots;
using DiscordControlCenter.Core.Common;
using DiscordControlCenter.Core.Explorer;
using DiscordControlCenter.Core.Operations;

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
                    var member = new MemberItemViewModel(CreateMember());
                    var roleModel = new RoleReadModel(
                        20,
                        "Operator",
                        5,
                        PermissionBits.ViewChannel,
                        false);
                    var preflight = new HierarchyPreflightResult(
                        SafetyDecision.Allowed,
                        HierarchyReasonCode.Allowed,
                        "Allowed for test rendering.",
                        PermissionBits.ManageRoles,
                        10,
                        5,
                        DataCompleteness.Complete);
                    var role = new RoleItemViewModel(roleModel, preflight, preflight, true);
                    var voicePermission = new PermissionResolution(
                        PermissionBits.ViewChannel,
                        Enum.GetValues<PermissionBits>()
                            .Where(value => value != PermissionBits.None)
                            .Select(value => new PermissionResult(
                                "Test",
                                value.ToString(),
                                value,
                                PermissionStatus.Allowed,
                                "Test"))
                            .ToArray());
                    var voiceChannel = new VoiceChannelItemViewModel(
                        CreateVoiceChannel(),
                        voicePermission);
                    var membersView = new MembersView
                    {
                        DataContext = new MemberPageData(member, command)
                    };
                    var rolesView = new RolesView
                    {
                        DataContext = new RolePageData(role)
                    };
                    var permissionsView = new PermissionSimulatorView
                    {
                        DataContext = new PermissionPageData()
                    };
                    var voiceView = new VoiceView
                    {
                        DataContext = new VoicePageData(voiceChannel)
                    };
                    var operationScheduler = new UiScheduler();
                    var operationPlan = UiOperationTestData.Plan();
                    operationScheduler.Publish(
                        new QueuedOperationSnapshot(
                            operationPlan,
                            ChannelOperationState.Running,
                            1,
                            new OperationProgress(
                                operationPlan.OperationId,
                                ChannelOperationState.Running,
                                0,
                                1,
                                operationPlan.Steps.Length,
                                "Executing.",
                                DateTimeOffset.UtcNow),
                            null,
                            DateTimeOffset.UtcNow));
                    using var operationCenterViewModel = new OperationCenterViewModel(
                        operationScheduler,
                        new UiDispatcher(application.Dispatcher));
                    var operationCenterView = new OperationCenterView
                    {
                        DataContext = operationCenterViewModel
                    };
                    var panel = new System.Windows.Controls.StackPanel();
                    panel.Children.Add(serverView);
                    panel.Children.Add(channelView);
                    panel.Children.Add(membersView);
                    panel.Children.Add(rolesView);
                    panel.Children.Add(permissionsView);
                    panel.Children.Add(voiceView);
                    panel.Children.Add(operationCenterView);
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

                    var draftExplorer = new FakeExplorer(server);
                    var draftContext = new ChannelOperationContext(
                        draftExplorer.Snapshot.BotProfileId,
                        "Test bot",
                        server,
                        [server.Channels[0]]);
                    var editDraft = new ChannelOperationDraftWindow(
                        new ChannelOperationDraftViewModel(
                            draftContext,
                            ChannelOperationUiMode.Edit,
                            new ChannelOperationPlanner(draftExplorer)))
                    {
                        ShowInTaskbar = false
                    };
                    editDraft.Show();
                    editDraft.UpdateLayout();
                    editDraft.Close();

                    var limitedServer = CreateServer() with
                    {
                        Members = new MemberCollectionReadModel(
                            DataCompleteness.Limited,
                            false,
                            [CreateMember()],
                            5,
                            DateTimeOffset.UtcNow,
                            null)
                    };
                    var explorer = new FakeExplorer(limitedServer);
                    using var membersViewModel = new MembersViewModel(
                        explorer,
                        new UiDispatcher(application.Dispatcher));
                    var botId = explorer.Snapshot.BotProfileId;
                    membersViewModel.SetContext(
                        botId,
                        BotConnectionState.Connected,
                        limitedServer.Id);
                    Assert.True(membersViewModel.IsLimitedMode);
                    Assert.Single(membersViewModel.Members);

                    membersViewModel.SearchText = "does-not-match";
                    membersViewModel.MembersView.Refresh();
                    Assert.Empty(membersViewModel.MembersView.Cast<object>());
                    membersViewModel.SearchText = string.Empty;
                    membersViewModel.MembersView.Refresh();
                    membersViewModel.SelectedMember = membersViewModel.Members[0];

                    var fullServer = limitedServer with
                    {
                        Members = new MemberCollectionReadModel(
                            DataCompleteness.Complete,
                            true,
                            [],
                            0,
                            DateTimeOffset.UtcNow,
                            null)
                    };
                    explorer.Publish(fullServer);
                    Assert.False(membersViewModel.IsLimitedMode);
                    Assert.Null(membersViewModel.SelectedMember);
                    membersViewModel.SetConnectionState(BotConnectionState.Disconnected);
                    Assert.True(membersViewModel.IsDisconnected);
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

    private static MemberReadModel CreateMember() =>
        new(
            42,
            "member",
            "Member",
            "Member",
            "Member",
            null,
            false,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            [20],
            "Operator",
            5,
            null,
            false,
            null,
            null,
            true);

    private static ChannelReadModel CreateVoiceChannel()
    {
        var voice = new VoiceStateReadModel(
            42,
            "Member",
            false,
            30,
            "Voice",
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            null);
        return new ChannelReadModel(
            30,
            "Voice",
            ChannelKind.Voice,
            "Voice",
            1,
            DateTimeOffset.UtcNow,
            null,
            null,
            null,
            [],
            null,
            null,
            null,
            null,
            64_000,
            0,
            null,
            1,
            [],
            null,
            null,
            null)
        {
            VoiceMembers = [voice]
        };
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

    private sealed class MemberPageData(MemberItemViewModel member, RelayCommand command)
    {
        public ObservableCollection<MemberItemViewModel> Members { get; } = [member];
        public System.ComponentModel.ICollectionView MembersView =>
            System.Windows.Data.CollectionViewSource.GetDefaultView(Members);
        public ObservableCollection<RoleFilterOption> RoleFilters { get; } =
            [new(null, "All roles")];
        public IReadOnlyList<string> Filters { get; } = ["All members"];
        public MemberItemViewModel? SelectedMember { get; set; } = member;
        public RoleFilterOption? SelectedRoleFilter { get; set; }
        public string SelectedFilter { get; set; } = "All members";
        public string SearchText { get; set; } = string.Empty;
        public RelayCommand LoadMembersCommand { get; } = command;
        public bool IsLimitedMode { get; } = true;
        public string ModeTitle { get; } = "Limited member mode";
        public string ModeMessage { get; } = "Test";
        public string ProgressText { get; } = "1 loaded";
        public string LastRefreshedText { get; } = "Now";
        public string StateTitle { get; } = string.Empty;
        public string StateMessage { get; } = string.Empty;
    }

    private sealed class RolePageData(RoleItemViewModel role)
    {
        public ObservableCollection<RoleItemViewModel> Roles { get; } = [role];
        public RoleItemViewModel? SelectedRole { get; set; } = role;
        public string CompletenessText { get; } = "Exact";
        public string StateTitle { get; } = string.Empty;
        public string StateMessage { get; } = string.Empty;
    }

    private sealed class PermissionPageData
    {
        public ObservableCollection<PermissionSubjectOption> Subjects { get; } =
        [
            new(PermissionSubjectKind.SelectedBot, 1, "Selected bot", null, null),
            new(PermissionSubjectKind.Role, 2, "Role", null, null)
        ];
        public ObservableCollection<ChannelOption> Channels { get; } =
            [new(CreateVoiceChannel())];
        public ObservableCollection<PermissionComparisonItem> Comparison { get; } =
        [
            new(
                "General",
                "View Channel",
                PermissionBits.ViewChannel,
                PermissionStatus.Allowed,
                PermissionStatus.Denied,
                PermissionComparisonStatus.FirstOnly)
        ];
        public PermissionSubjectOption? FirstSubject { get; set; }
        public PermissionSubjectOption? SecondSubject { get; set; }
        public ChannelOption? SelectedChannel { get; set; }
        public string CompletenessMessage { get; } = "Complete";
        public string StateMessage { get; } = string.Empty;
    }

    private sealed class VoicePageData(VoiceChannelItemViewModel channel)
    {
        public ObservableCollection<VoiceChannelItemViewModel> Channels { get; } = [channel];
        public ObservableCollection<VoiceMemberItemViewModel> Members { get; } =
            [new(channel.Model.VoiceMembers[0])];
        public VoiceChannelItemViewModel? SelectedChannel { get; set; } = channel;
        public VoiceMemberItemViewModel? SelectedMember { get; set; }
        public string StateTitle { get; } = string.Empty;
        public string StateMessage { get; } = string.Empty;
    }

    private sealed class FakeExplorer(ServerReadModel server) : IBotExplorerService
    {
        public event EventHandler<ExplorerCacheChanged>? CacheChanged;

        public BotExplorerSnapshot Snapshot { get; private set; } = new(
            Guid.NewGuid(),
            1,
            ExplorerCacheState.Ready,
            [server],
            DateTimeOffset.UtcNow,
            null);

        public BotExplorerSnapshot GetSnapshot(Guid botProfileId)
        {
            Assert.Equal(Snapshot.BotProfileId, botProfileId);
            return Snapshot;
        }

        public Task<OperationResult> RefreshAsync(
            Guid botProfileId,
            CancellationToken cancellationToken)
        {
            _ = botProfileId;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> LoadMembersAsync(
            Guid botProfileId,
            ulong serverId,
            CancellationToken cancellationToken)
        {
            _ = botProfileId;
            _ = serverId;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(OperationResult.Success());
        }

        public IReadOnlyList<BotDiagnosticsReadModel> GetDiagnostics() => [];

        public void Publish(ServerReadModel updated)
        {
            Snapshot = Snapshot with
            {
                Version = Snapshot.Version + 1,
                Servers = [updated],
                RefreshedAt = DateTimeOffset.UtcNow
            };
            CacheChanged?.Invoke(
                this,
                new ExplorerCacheChanged(
                    Snapshot.BotProfileId,
                    ExplorerCacheUpdateKind.MembersStateChanged,
                    updated.Id,
                    Snapshot));
        }
    }
}
