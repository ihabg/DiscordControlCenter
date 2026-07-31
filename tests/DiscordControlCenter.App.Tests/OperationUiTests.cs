using System.Collections.Immutable;
using System.Windows;
using System.Windows.Threading;
using DiscordControlCenter.App.Services;
using DiscordControlCenter.App.ViewModels;
using DiscordControlCenter.Application.Explorer;
using DiscordControlCenter.Application.Operations;
using DiscordControlCenter.Core.Bots;
using DiscordControlCenter.Core.Common;
using DiscordControlCenter.Core.Explorer;
using DiscordControlCenter.Core.Operations;

namespace DiscordControlCenter.App.Tests;

public sealed class OperationUiTests
{
    [Fact]
    public void TypedConfirmationRequiresExactCaseSensitiveText()
    {
        var requirement = new OperationConfirmationRequirement(
            OperationConfirmationKind.TypedText,
            "Type the phrase.",
            "DELETE 2 CHANNELS");
        var plan = UiOperationTestData.Plan(requirement);
        var viewModel = new OperationConfirmationViewModel(
            plan,
            UiOperationTestData.Preview(plan, requirement));

        viewModel.ConfirmationText = "delete 2 channels";
        Assert.False(viewModel.CanConfirm);
        viewModel.ConfirmationText = "DELETE 2 CHANNELS ";
        Assert.False(viewModel.CanConfirm);
        viewModel.ConfirmationText = "DELETE 2 CHANNELS";
        Assert.True(viewModel.CanConfirm);
    }

    [Fact]
    public void ConfirmationCannotSubmitSamePlanTwice()
    {
        var plan = UiOperationTestData.Plan();
        var viewModel = new OperationConfirmationViewModel(
            plan,
            UiOperationTestData.Preview(plan));
        var submissions = 0;
        viewModel.Confirmed += (_, _) => submissions++;

        viewModel.ConfirmCommand.Execute(null);
        viewModel.ConfirmCommand.Execute(null);

        Assert.Equal(1, submissions);
        Assert.False(viewModel.CanConfirm);
    }

    [Fact]
    public void OperationItemDisplaysPartialAndStaleRetryGuidance()
    {
        var plan = UiOperationTestData.Plan();
        var now = DateTimeOffset.UtcNow;
        var failure = new OperationFailure(
            OperationFailureKind.StalePlan,
            "PLAN_STALE",
            "The target changed.",
            null,
            false,
            OperationOutcomeCertainty.KnownFailed);
        var result = new ChannelOperationResult(
            plan.OperationId,
            plan.CorrelationId,
            ChannelOperationState.PartiallyCompleted,
            now.AddSeconds(-2),
            now,
            [
                new OperationStepResult(
                    plan.Steps[0].StepId,
                    1,
                    "Rename general",
                    true,
                    false,
                    301,
                    now.AddSeconds(-2),
                    now,
                    1,
                    null,
                    false,
                    false)
            ],
            1,
            0,
            0,
            failure,
            new OperationReconciliationResult(
                OperationReconciliationStatus.ManualReviewRequired,
                "Review current state.",
                [],
                now),
            "backup-1",
            plan.CompensationCapability,
            "Partial compensation only.");
        var item = new OperationItemViewModel(
            new QueuedOperationSnapshot(
                plan,
                result.State,
                0,
                null,
                result,
                plan.CreatedAt));

        Assert.True(item.CanRegeneratePreview);
        Assert.Contains("never replayed blindly", item.RetryGuidance, StringComparison.Ordinal);
        Assert.Equal("PLAN_STALE", item.ErrorCodeText);
        Assert.Contains("1 completed", item.CountsText, StringComparison.Ordinal);
        Assert.Equal("Review current state.", item.ReconciliationText);
    }

    [Fact]
    public void ReconciliationStepOptionNeverUsesRawOperationStepObjectText()
    {
        var plan = UiOperationTestData.Plan();
        var step = new OperationStepResult(
            plan.Steps[0].StepId,
            2,
            "Delete channel \"phase4a-validation-beta\"",
            false,
            false,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            1,
            null,
            false,
            false);

        var option = new ReconciliationStepOption(step);

        Assert.Equal("Step 2 — Delete channel \"phase4a-validation-beta\"", option.DisplayText);
        Assert.Equal(option.DisplayText, option.ToString());
        Assert.DoesNotContain("OperationStepResult", option.DisplayText, StringComparison.Ordinal);
        Assert.DoesNotContain("{", option.DisplayText, StringComparison.Ordinal);
    }

    [Fact]
    public void OperationCenterAppliesProgressAndCancellationState()
    {
        var scheduler = new UiScheduler();
        var dispatcher = new UiDispatcher(Dispatcher.CurrentDispatcher);
        using var viewModel = new OperationCenterViewModel(scheduler, dispatcher);
        var plan = UiOperationTestData.Plan();
        scheduler.Publish(
            new QueuedOperationSnapshot(
                plan,
                ChannelOperationState.Running,
                0,
                new OperationProgress(
                    plan.OperationId,
                    ChannelOperationState.Running,
                    0,
                    1,
                    1,
                    "Executing.",
                    DateTimeOffset.UtcNow),
                null,
                DateTimeOffset.UtcNow));

        Assert.Single(viewModel.Operations);
        Assert.True(viewModel.CanCancelSelected);
        Assert.Equal("Executing.", viewModel.SelectedOperation!.StatusSummary);
        viewModel.CancelCommand.Execute(null);
        Assert.Equal(plan.OperationId, scheduler.CancelledOperationId);

        scheduler.Publish(
            scheduler.Snapshots[0] with
            {
                State = ChannelOperationState.Cancelling,
                Progress = scheduler.Snapshots[0].Progress! with
                {
                    State = ChannelOperationState.Cancelling,
                    Message = "Cancellation requested."
                }
            });
        Assert.Equal("Cancelling", viewModel.SelectedOperation.StateText);
    }

    [Fact]
    public void ChannelSelectionClearsAndMixedUnsupportedTypesDisableActions()
    {
        Exception? failure = null;
        var thread = new Thread(
            () =>
            {
                try
                {
                    var explorer = new UiExplorer();
                    var scheduler = new UiScheduler();
                    using var viewModel = new ChannelsViewModel(
                        explorer,
                        new PermissionResolutionService(),
                        new UiOperationDialog(),
                        scheduler,
                        new UiDispatcher(Dispatcher.CurrentDispatcher));
                    viewModel.SetBot(
                        explorer.Snapshot.BotProfileId,
                        BotConnectionState.Connected,
                        "Test bot");
                    viewModel.SetServer(explorer.Snapshot.Servers[0].Id);
                    var category = viewModel.ChannelGroups
                        .Select(group => group.Category)
                        .First(item => item is not null)!;
                    var ordinary = viewModel.ChannelGroups
                        .SelectMany(group => group.Channels)
                        .First(channel => channel.Model.Kind == ChannelKind.Text);
                    var unsupported = viewModel.ChannelGroups
                        .SelectMany(group => group.Channels)
                        .First(channel => channel.Model.Kind == ChannelKind.Forum);

                    viewModel.ToggleOperationSelectionCommand.Execute(ordinary);
                    Assert.True(viewModel.CanEditOperation);
                    viewModel.ToggleOperationSelectionCommand.Execute(unsupported);
                    Assert.False(viewModel.CanRenameOperation);
                    Assert.Contains(
                        "supported",
                        viewModel.RenameOperationExplanation,
                        StringComparison.OrdinalIgnoreCase);
                    viewModel.ClearOperationSelectionCommand.Execute(null);
                    Assert.Equal(0, viewModel.SelectedOperationCount);
                    Assert.False(ordinary.IsOperationSelected);
                    Assert.False(unsupported.IsOperationSelected);

                    viewModel.ToggleOperationSelectionCommand.Execute(category);
                    viewModel.ToggleOperationSelectionCommand.Execute(ordinary);
                    Assert.True(viewModel.CanDeleteOperation);
                    Assert.True(viewModel.CanCloneOperation);
                    viewModel.ToggleOperationSelectionCommand.Execute(unsupported);
                    Assert.False(viewModel.CanDeleteOperation);
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
}

internal sealed class UiScheduler : IChannelOperationScheduler
{
    private readonly List<QueuedOperationSnapshot> _snapshots = [];
    public event EventHandler<QueuedOperationSnapshot>? OperationChanged;
    public IReadOnlyList<QueuedOperationSnapshot> Snapshots => _snapshots;
    public Guid? CancelledOperationId { get; private set; }

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<QueueSubmissionResult> EnqueueAsync(
        OperationPlan plan,
        CancellationToken cancellationToken) =>
        Task.FromResult(new QueueSubmissionResult(true, 1, null));

    public bool Cancel(Guid operationId)
    {
        CancelledOperationId = operationId;
        return true;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Publish(QueuedOperationSnapshot snapshot)
    {
        var index = _snapshots.FindIndex(item =>
            item.Plan.OperationId == snapshot.Plan.OperationId);
        if (index < 0)
        {
            _snapshots.Insert(0, snapshot);
        }
        else
        {
            _snapshots[index] = snapshot;
        }

        OperationChanged?.Invoke(this, snapshot);
    }
}

internal sealed class UiOperationDialog : IChannelOperationDialogService
{
    public Task<bool> ConfigurePreviewConfirmAndQueueAsync(
        ChannelOperationContext context,
        ChannelOperationUiMode mode,
        CancellationToken cancellationToken) =>
        Task.FromResult(false);
}

internal sealed class UiExplorer : IBotExplorerService
{
    public UiExplorer()
    {
        var botId = Guid.NewGuid();
        var serverId = 100UL;
        var everyone = new RoleReadModel(
            serverId,
            "@everyone",
            0,
            PermissionBits.ViewChannel,
            true);
        var botRole = new RoleReadModel(
            200,
            "Bot",
            10,
            PermissionBits.Administrator,
            false);
        var category = Channel(300, "Category", ChannelKind.Category, null);
        var text = Channel(301, "general", ChannelKind.Text, 300);
        var forum = Channel(302, "forum", ChannelKind.Forum, 300);
        var server = new ServerReadModel(
            serverId,
            "Test Server",
            null,
            null,
            999,
            DateTimeOffset.UtcNow,
            1,
            1,
            1,
            0,
            1,
            0,
            2,
            0,
            "None",
            0,
            null,
            "Bot",
            10,
            500,
            [200],
            [everyone, botRole],
            [category, text, forum],
            ServerAvailability.Available,
            DateTimeOffset.UtcNow);
        Snapshot = new BotExplorerSnapshot(
            botId,
            1,
            ExplorerCacheState.Ready,
            [server],
            DateTimeOffset.UtcNow,
            null);
    }

    public event EventHandler<ExplorerCacheChanged>? CacheChanged
    {
        add { }
        remove { }
    }
    public BotExplorerSnapshot Snapshot { get; }
    public BotExplorerSnapshot GetSnapshot(Guid botProfileId) => Snapshot;
    public Task<OperationResult> RefreshAsync(Guid botProfileId, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Success());
    public Task<OperationResult> LoadMembersAsync(
        Guid botProfileId,
        ulong serverId,
        CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Success());
    public IReadOnlyList<BotDiagnosticsReadModel> GetDiagnostics() => [];

    private static ChannelReadModel Channel(
        ulong id,
        string name,
        ChannelKind kind,
        ulong? categoryId) =>
        new(
            id,
            name,
            kind,
            kind.ToString(),
            (int)(id - 300),
            DateTimeOffset.UtcNow,
            categoryId,
            categoryId is null ? null : "Category",
            null,
            ImmutableArray<PermissionOverwriteReadModel>.Empty,
            null,
            kind == ChannelKind.Text ? false : null,
            kind == ChannelKind.Text ? 0 : null,
            null,
            null,
            null,
            null,
            null,
            [],
            null,
            null,
            null);
}
