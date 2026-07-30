using System.Collections.Immutable;
using DiscordControlCenter.Application.Operations;
using DiscordControlCenter.Core.Explorer;
using DiscordControlCenter.Core.Operations;

namespace DiscordControlCenter.Application.Tests;

public sealed class ChannelOperationReconciliationTests
{
    [Fact]
    public async Task CreateTimeoutWithExactlyOneMatchIsApplied()
    {
        var (plan, step, explorer) = CreateContext();
        var server = OperationTestFixture.Server();
        explorer.SetSnapshot(
            OperationTestFixture.Snapshot(
                server with
                {
                    Channels = server.Channels.Add(
                        OperationTestFixture.Channel(
                            900,
                            step.After!.Name,
                            step.After.Kind,
                            step.After.Position,
                            step.After.ParentCategoryId,
                            []) with
                        { CreatedAt = DateTimeOffset.UtcNow })
                },
                11));

        var result = await Reconcile(plan, step, explorer);

        Assert.Equal(OperationReconciliationStatus.ConfirmedApplied, result.Status);
        Assert.Equal(900UL, Assert.Single(result.MatchingResourceIds));
    }

    [Fact]
    public async Task CreateTimeoutWithNoMatchIsKnownNotApplied()
    {
        var (plan, step, explorer) = CreateContext();

        var result = await Reconcile(plan, step, explorer);

        Assert.Equal(OperationReconciliationStatus.ConfirmedNotApplied, result.Status);
        Assert.Empty(result.MatchingResourceIds);
    }

    [Fact]
    public async Task MultipleCreateMatchesAreAmbiguous()
    {
        var (plan, step, explorer) = CreateContext();
        var server = OperationTestFixture.Server();
        var first = OperationTestFixture.Channel(
            900, step.After!.Name, step.After.Kind, 1, step.After.ParentCategoryId, [])
            with
        { CreatedAt = DateTimeOffset.UtcNow };
        var second = first with { Id = 901, Position = 2 };
        explorer.SetSnapshot(
            OperationTestFixture.Snapshot(
                server with { Channels = server.Channels.Add(first).Add(second) },
                11));

        var result = await Reconcile(plan, step, explorer);

        Assert.Equal(OperationReconciliationStatus.Ambiguous, result.Status);
        Assert.Equal(2, result.MatchingResourceIds.Length);
    }

    [Fact]
    public async Task UpdateAlreadyAppliedMatchesPlannedAfterFingerprint()
    {
        var explorer = OperationTestFixture.Explorer();
        var plan = new ChannelOperationPlanner(explorer).PlanBulkRename(
            new BulkRenameRequest(
                OperationTestFixture.BotId,
                OperationTestFixture.ServerId,
                [OperationTestFixture.TextId],
                BulkRenameMode.Prefix,
                "done-",
                null,
                1,
                0,
                null)).Plan!;
        var step = plan.Steps[0];
        var server = OperationTestFixture.Server();
        explorer.SetSnapshot(
            OperationTestFixture.Snapshot(
                server with
                {
                    Channels = server.Channels
                        .Select(channel => channel.Id == OperationTestFixture.TextId
                            ? channel with { Name = step.After!.Name }
                            : channel)
                        .ToImmutableArray()
                },
                11));

        var result = await Reconcile(plan, step, explorer);

        Assert.Equal(OperationReconciliationStatus.ConfirmedApplied, result.Status);
    }

    [Fact]
    public async Task UpdateStillAtBeforeStateIsKnownNotApplied()
    {
        var explorer = OperationTestFixture.Explorer();
        var plan = new ChannelOperationPlanner(explorer).PlanBulkRename(
            new BulkRenameRequest(
                OperationTestFixture.BotId,
                OperationTestFixture.ServerId,
                [OperationTestFixture.TextId],
                BulkRenameMode.Prefix,
                "done-",
                null,
                1,
                0,
                null)).Plan!;

        var result = await Reconcile(plan, plan.Steps[0], explorer);

        Assert.Equal(OperationReconciliationStatus.ConfirmedNotApplied, result.Status);
    }

    [Fact]
    public async Task DeleteAlreadyCompletedIsApplied()
    {
        var explorer = OperationTestFixture.Explorer();
        var plan = new ChannelOperationPlanner(explorer).PlanDelete(
            new DeleteChannelsRequest(
                OperationTestFixture.BotId,
                OperationTestFixture.ServerId,
                [OperationTestFixture.TextId],
                false,
                false,
                [],
                null)).Plan!;
        var server = OperationTestFixture.Server();
        explorer.SetSnapshot(
            OperationTestFixture.Snapshot(
                server with
                {
                    Channels = server.Channels
                        .Where(channel => channel.Id != OperationTestFixture.TextId)
                        .ToImmutableArray()
                },
                11));

        var result = await Reconcile(plan, plan.Steps[0], explorer);

        Assert.Equal(OperationReconciliationStatus.ConfirmedApplied, result.Status);
        Assert.Empty(result.MatchingResourceIds);
    }

    private static (
        OperationPlan Plan,
        OperationStep Step,
        FakeOperationExplorer Explorer) CreateContext()
    {
        var explorer = OperationTestFixture.Explorer();
        var plan = new ChannelOperationPlanner(explorer).PlanCreate(
            new CreateChannelsRequest(
                OperationTestFixture.BotId,
                OperationTestFixture.ServerId,
                [
                    new ChannelCreationItem(
                        "created-after-timeout",
                        ChannelKind.Text,
                        OperationTestFixture.CategoryId,
                        null,
                        false,
                        0,
                        null,
                        null,
                        null,
                        false)
                ],
                null)).Plan!;
        return (plan, plan.Steps[0], explorer);
    }

    private static Task<OperationReconciliationResult> Reconcile(
        OperationPlan plan,
        OperationStep step,
        FakeOperationExplorer explorer) =>
        new ChannelOperationReconciliationService(explorer).ReconcileAsync(
            plan,
            step,
            new ChannelWriteOutcome(
                false,
                null,
                null,
                OperationOutcomeCertainty.Uncertain),
            CancellationToken.None);
}
