using System.Collections.Immutable;
using DiscordControlCenter.Application.Bots;
using DiscordControlCenter.Application.Explorer;
using DiscordControlCenter.Application.Operations;
using DiscordControlCenter.Core.Bots;
using DiscordControlCenter.Core.Common;
using DiscordControlCenter.Core.Explorer;

namespace DiscordControlCenter.Application.Tests;

public sealed class ChannelOperationPreflightTests
{
    [Fact]
    public void DisconnectedBotIsBlocked()
    {
        var context = Context();
        context.Connection.SetState(BotConnectionState.Disconnected);

        var result = context.Preflight.Validate(context.Plan);

        Assert.False(result.IsAllowed);
        Assert.Contains(result.Issues, issue => issue.SafeCode == "BOT_DISCONNECTED");
    }

    [Fact]
    public void UnavailableServerIsBlocked()
    {
        var context = Context();
        var unavailable = OperationTestFixture.Server(
            availability: ServerAvailability.Unavailable);
        context.Explorer.SetSnapshot(OperationTestFixture.Snapshot(unavailable, 11));

        var result = context.Preflight.Validate(context.Plan);

        Assert.False(result.IsAllowed);
        Assert.Contains(result.Issues, issue => issue.SafeCode == "SERVER_UNAVAILABLE");
    }

    [Fact]
    public void MissingManageChannelsIsBlocked()
    {
        var context = Context();
        context.Explorer.SetSnapshot(
            OperationTestFixture.Snapshot(
                OperationTestFixture.Server(PermissionBits.ViewChannel),
                11));

        var result = context.Preflight.Validate(context.Plan);

        Assert.False(result.IsAllowed);
        Assert.Contains(result.Issues, issue =>
            issue.SafeCode == "MISSING_PERMISSION"
            && issue.Message.Contains("ManageChannels", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingManageRolesIsBlockedForOverwritePlan()
    {
        var explorer = OperationTestFixture.Explorer();
        var planner = new ChannelOperationPlanner(explorer);
        var plan = planner.PlanLock(
            new ChannelLockRequest(
                OperationTestFixture.BotId,
                OperationTestFixture.ServerId,
                [OperationTestFixture.TextId],
                OperationTestFixture.EveryoneRoleId,
                false,
                false,
                null)).Plan!;
        explorer.SetSnapshot(
            OperationTestFixture.Snapshot(
                OperationTestFixture.Server(PermissionBits.ManageChannels),
                11));
        var preflight = CreatePreflight(explorer, new FakeConnectionManager());

        var result = preflight.Validate(plan);

        Assert.False(result.IsAllowed);
        Assert.Contains(result.Issues, issue =>
            issue.SafeCode == "MISSING_PERMISSION"
            && issue.Message.Contains("ManageRoles", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingTargetMarksPlanStale()
    {
        var context = Context();
        var current = OperationTestFixture.Server();
        context.Explorer.SetSnapshot(
            OperationTestFixture.Snapshot(
                current with
                {
                    Channels = current.Channels
                        .Where(channel => channel.Id != OperationTestFixture.TextId)
                        .ToImmutableArray()
                },
                11));

        var result = context.Preflight.Validate(context.Plan);

        Assert.True(result.IsStale);
        Assert.Contains(result.Issues, issue => issue.SafeCode == "TARGET_NOT_FOUND");
    }

    [Fact]
    public void RelevantNameChangeMarksPlanStaleAndDescribesChange()
    {
        var context = Context();
        var current = OperationTestFixture.Server();
        context.Explorer.SetSnapshot(
            OperationTestFixture.Snapshot(
                current with
                {
                    Channels = current.Channels
                        .Select(channel => channel.Id == OperationTestFixture.TextId
                            ? channel with { Name = "changed-elsewhere" }
                            : channel)
                        .ToImmutableArray()
                },
                11));

        var result = context.Preflight.Validate(context.Plan);

        Assert.True(result.IsStale);
        var issue = Assert.Single(result.Issues, issue => issue.SafeCode == "TARGET_CHANGED");
        Assert.Contains("name", issue.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("changed-elsewhere", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParentFingerprintChangeMarksCreationPlanStale()
    {
        var context = Context(createPlan: true);
        var current = OperationTestFixture.Server();
        context.Explorer.SetSnapshot(
            OperationTestFixture.Snapshot(
                current with
                {
                    Channels = current.Channels
                        .Select(channel => channel.Id == OperationTestFixture.CategoryId
                            ? channel with { Position = channel.Position + 1 }
                            : channel)
                        .ToImmutableArray()
                },
                11));

        var result = context.Preflight.Validate(context.Plan);

        Assert.True(result.IsStale);
        Assert.Contains(result.Issues, issue =>
            issue.SafeCode == "TARGET_CHANGED"
            && issue.TargetId == OperationTestFixture.CategoryId);
    }

    [Fact]
    public void UnrelatedSequenceUpdateDoesNotInvalidatePlan()
    {
        var context = Context();
        context.Explorer.SetSnapshot(
            OperationTestFixture.Snapshot(OperationTestFixture.Server(), 999));

        var result = context.Preflight.Validate(context.Plan);

        Assert.True(result.IsAllowed);
        Assert.False(result.IsStale);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void IncompletePermissionResultIsBlockedAsUnknown()
    {
        var context = Context(createPlan: true, uncategorized: true);
        var server = OperationTestFixture.Server();
        context.Explorer.SetSnapshot(
            OperationTestFixture.Snapshot(
                server with
                {
                    Roles = server.Roles
                        .Where(role => !role.IsEveryone)
                        .ToImmutableArray()
                },
                11));

        var result = context.Preflight.Validate(context.Plan);

        Assert.False(result.IsAllowed);
        Assert.Contains(result.Issues, issue => issue.SafeCode == "PERMISSION_UNKNOWN");
    }

    [Fact]
    public void ValidPlanEvaluatesEveryRequiredPrecondition()
    {
        var context = Context();

        var result = context.Preflight.Validate(context.Plan);

        Assert.True(result.IsAllowed);
        Assert.All(result.EvaluatedPreconditions, item => Assert.True(item.IsSatisfied));
        Assert.Contains(result.EvaluatedPreconditions, item =>
            item.Kind == Core.Operations.OperationPreconditionKind.BotConnected);
        Assert.Contains(result.EvaluatedPreconditions, item =>
            item.Kind == Core.Operations.OperationPreconditionKind.RequiredPermission);
    }

    [Fact]
    public void CategoryCloneChildrenDoNotConflictBeforeParentExists()
    {
        var explorer = OperationTestFixture.Explorer();
        var server = OperationTestFixture.Server();
        var duplicateUncategorized = OperationTestFixture.Channel(
            999,
            "general",
            ChannelKind.Text,
            9,
            null,
            []);
        explorer.SetSnapshot(
            OperationTestFixture.Snapshot(
                server with { Channels = server.Channels.Add(duplicateUncategorized) }));
        var plan = new ChannelOperationPlanner(explorer).PlanCloneCategory(
            new CloneCategoryRequest(
                OperationTestFixture.BotId,
                OperationTestFixture.ServerId,
                OperationTestFixture.CategoryId,
                "Cloned Category",
                [OperationTestFixture.TextId],
                false,
                false,
                false,
                null)).Plan!;

        var result = CreatePreflight(explorer, new FakeConnectionManager()).Validate(plan);

        Assert.True(result.IsAllowed);
        Assert.DoesNotContain(result.Issues, issue => issue.SafeCode == "CREATE_NAME_CONFLICT");
    }

    private static PreflightContext Context(
        bool createPlan = false,
        bool uncategorized = false)
    {
        var explorer = OperationTestFixture.Explorer();
        var planner = new ChannelOperationPlanner(explorer);
        var plan = createPlan
            ? planner.PlanCreate(
                new CreateChannelsRequest(
                    OperationTestFixture.BotId,
                    OperationTestFixture.ServerId,
                    [
                        new ChannelCreationItem(
                            "new-channel",
                            ChannelKind.Text,
                            uncategorized ? null : OperationTestFixture.CategoryId,
                            null,
                            false,
                            0,
                            null,
                            null,
                            null,
                            false)
                    ],
                    null)).Plan!
            : planner.PlanBulkRename(
                new BulkRenameRequest(
                    OperationTestFixture.BotId,
                    OperationTestFixture.ServerId,
                    [OperationTestFixture.TextId],
                    BulkRenameMode.Prefix,
                    "new-",
                    null,
                    1,
                    0,
                    null)).Plan!;
        var connection = new FakeConnectionManager();
        return new PreflightContext(
            explorer,
            connection,
            plan,
            CreatePreflight(explorer, connection));
    }

    private static ChannelOperationPreflightService CreatePreflight(
        IBotExplorerService explorer,
        IBotConnectionManager connection) =>
        new(
            connection,
            explorer,
            new PermissionResolutionService(),
            new RoleHierarchySafetyService());

    private sealed record PreflightContext(
        FakeOperationExplorer Explorer,
        FakeConnectionManager Connection,
        Core.Operations.OperationPlan Plan,
        ChannelOperationPreflightService Preflight);
}

internal sealed class FakeConnectionManager : IBotConnectionManager
{
    private BotConnectionSnapshot _snapshot = new(
        OperationTestFixture.BotId,
        BotConnectionState.Connected,
        50,
        0,
        new BotIdentity(500, "Control Bot", null),
        DateTimeOffset.UtcNow,
        null);

    public event EventHandler<BotConnectionSnapshot>? StatusChanged;
    public IReadOnlyCollection<BotConnectionSnapshot> Snapshots => [_snapshot];

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<OperationResult> ConnectAsync(Guid botProfileId, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Success());
    public Task<OperationResult> DisconnectAsync(Guid botProfileId, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Success());
    public Task ConnectAllAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task DisconnectAllAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void SetState(BotConnectionState state)
    {
        _snapshot = _snapshot with { State = state };
        StatusChanged?.Invoke(this, _snapshot);
    }
}
