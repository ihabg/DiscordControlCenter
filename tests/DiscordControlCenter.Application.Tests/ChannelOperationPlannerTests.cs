using System.Collections.Immutable;
using DiscordControlCenter.Application.Operations;
using DiscordControlCenter.Core.Explorer;
using DiscordControlCenter.Core.Operations;

namespace DiscordControlCenter.Application.Tests;

public sealed class ChannelOperationPlannerTests
{
    [Fact]
    public void SingleTextCreationCapturesParentAndExactPreview()
    {
        var planner = CreatePlanner();

        var result = planner.PlanCreate(
            CreateRequest(
                new ChannelCreationItem(
                    "release-notes",
                    ChannelKind.Text,
                    OperationTestFixture.CategoryId,
                    "Ship notes",
                    false,
                    5,
                    null,
                    null,
                    2,
                    true)));

        Assert.True(result.IsSuccess);
        var plan = Assert.IsType<OperationPlan>(result.Plan);
        Assert.Equal(ChannelOperationType.CreateTextChannels, plan.OperationType);
        Assert.Equal(OperationRiskLevel.Low, plan.RiskLevel);
        Assert.Contains(PermissionBits.ManageRoles, plan.RequiredBotPermissions);
        Assert.Equal(OperationTestFixture.CategoryId, plan.ProposedAfterState[0].ParentCategoryId);
        Assert.Equal("Ship notes", plan.ProposedAfterState[0].Topic);
        Assert.Single(plan.Steps);
        var preview = planner.BuildPreview(plan, "Test bot");
        Assert.Contains(preview.PropertyChanges, change =>
            change.PropertyName.Contains("Resource", StringComparison.Ordinal)
            && change.AfterValue!.Contains("release-notes", StringComparison.Ordinal));
    }

    [Fact]
    public void BatchCreationProducesOneOrderedStepPerName()
    {
        var planner = CreatePlanner();
        var items = Enumerable.Range(1, 3)
            .Select(index => TextCreation($"batch-{index}"))
            .ToImmutableArray();

        var result = planner.PlanCreate(
            new CreateChannelsRequest(
                OperationTestFixture.BotId,
                OperationTestFixture.ServerId,
                items,
                "batch test"));

        Assert.True(result.IsSuccess);
        Assert.Equal([1, 2, 3], result.Plan!.Steps.Select(step => step.Order));
        Assert.Equal(OperationRiskLevel.Moderate, result.Plan.RiskLevel);
        Assert.Equal(3, result.Plan.EstimatedRequestCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("name\nwith-break")]
    public void InvalidCreationNameIsRejected(string name)
    {
        var result = CreatePlanner().PlanCreate(CreateRequest(TextCreation(name)));

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error =>
            error.Contains("valid", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UnsupportedPropertyAndTypeCombinationIsRejected()
    {
        var categoryWithTopic = new ChannelCreationItem(
            "category",
            ChannelKind.Category,
            null,
            "not supported",
            null,
            null,
            null,
            null,
            null,
            false);

        var result = CreatePlanner().PlanCreate(CreateRequest(categoryWithTopic));

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error =>
            error.Contains("text-only", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DuplicateRequestedNamesAreRejectedAtomically()
    {
        var result = CreatePlanner().PlanCreate(
            CreateRequest(TextCreation("same"), TextCreation("SAME")));

        Assert.False(result.IsSuccess);
        Assert.Null(result.Plan);
        Assert.Contains(result.Errors, error =>
            error.Contains("unique", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UnchangedEditProducesNoOperation()
    {
        var result = CreatePlanner().PlanEdit(
            new EditChannelRequest(
                OperationTestFixture.BotId,
                OperationTestFixture.ServerId,
                OperationTestFixture.TextId,
                OptionalChange.Unchanged<string>(),
                OptionalChange.Unchanged<ulong?>(),
                OptionalChange.Unchanged<int>(),
                OptionalChange.Unchanged<string?>(),
                OptionalChange.Unchanged<bool>(),
                OptionalChange.Unchanged<int>(),
                OptionalChange.Unchanged<int>(),
                OptionalChange.Unchanged<int>(),
                OptionalChange.Unchanged<int>(),
                OptionalChange.Unchanged<string?>(),
                null));

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error =>
            error.Contains("would change", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EditPreviewContainsOnlyChangedValues()
    {
        var planner = CreatePlanner();
        var result = planner.PlanEdit(
            new EditChannelRequest(
                OperationTestFixture.BotId,
                OperationTestFixture.ServerId,
                OperationTestFixture.TextId,
                OptionalChange.Unchanged<string>(),
                OptionalChange.Unchanged<ulong?>(),
                OptionalChange.Unchanged<int>(),
                OptionalChange.To<string?>("New topic"),
                OptionalChange.Unchanged<bool>(),
                OptionalChange.To(10),
                OptionalChange.Unchanged<int>(),
                OptionalChange.Unchanged<int>(),
                OptionalChange.Unchanged<int>(),
                OptionalChange.Unchanged<string?>(),
                null));

        var preview = planner.BuildPreview(result.Plan!, "Test bot");

        Assert.Contains(preview.PropertyChanges, change =>
            change.PropertyName.Contains("Topic", StringComparison.Ordinal)
            && change.BeforeValue == "Original topic"
            && change.AfterValue == "New topic");
        Assert.Contains(preview.PropertyChanges, change =>
            change.PropertyName.Contains("Slow mode", StringComparison.Ordinal)
            && change.AfterValue == "10");
        Assert.DoesNotContain(preview.PropertyChanges, change =>
            change.PropertyName.Contains("Name", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(BulkRenameMode.Prefix, "pre-", null, "pre-general", "pre-random")]
    [InlineData(BulkRenameMode.Suffix, "-old", null, "general-old", "random-old")]
    [InlineData(BulkRenameMode.FindAndReplace, "a", "x", "generxl", "rxndom")]
    [InlineData(BulkRenameMode.SequentialNumbering, "channel-", null, "channel-03", "channel-04")]
    public void BulkRenameModesGenerateExactNames(
        BulkRenameMode mode,
        string value,
        string? replacement,
        string firstExpected,
        string secondExpected)
    {
        var result = CreatePlanner().PlanBulkRename(
            new BulkRenameRequest(
                OperationTestFixture.BotId,
                OperationTestFixture.ServerId,
                [OperationTestFixture.TextId, OperationTestFixture.OtherTextId],
                mode,
                value,
                replacement,
                3,
                2,
                null));

        Assert.True(result.IsSuccess);
        Assert.Equal(
            [firstExpected, secondExpected],
            result.Plan!.ProposedAfterState.Select(state => state.Name));
    }

    [Fact]
    public void BulkRenameRejectsDuplicateFinalNames()
    {
        var result = CreatePlanner().PlanBulkRename(
            new BulkRenameRequest(
                OperationTestFixture.BotId,
                OperationTestFixture.ServerId,
                [OperationTestFixture.TextId, OperationTestFixture.OtherTextId],
                BulkRenameMode.ExactReplacement,
                "duplicate",
                null,
                1,
                0,
                null));

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error =>
            error.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MovePlanPreservesSelectedRelativeOrder()
    {
        var result = CreatePlanner().PlanMove(
            new MoveChannelsRequest(
                OperationTestFixture.BotId,
                OperationTestFixture.ServerId,
                [OperationTestFixture.OtherTextId, OperationTestFixture.TextId],
                null,
                MovePlacement.PreserveRelativeOrder,
                null,
                null));

        Assert.True(result.IsSuccess);
        Assert.Equal(
            [OperationTestFixture.TextId, OperationTestFixture.OtherTextId],
            result.Plan!.Steps.Select(step => step.Target.Id));
        Assert.All(result.Plan.ProposedAfterState, state => Assert.Null(state.ParentCategoryId));
    }

    [Fact]
    public void ReorderWithinCategoryUsesOneBulkPositionRequestAndExactPreview()
    {
        var planner = CreatePlanner();
        var result = planner.PlanMove(
            new MoveChannelsRequest(
                OperationTestFixture.BotId,
                OperationTestFixture.ServerId,
                [OperationTestFixture.TextId, OperationTestFixture.OtherTextId],
                OperationTestFixture.CategoryId,
                MovePlacement.AfterChannel,
                OperationTestFixture.VoiceId,
                null));

        Assert.True(result.IsSuccess);
        var step = Assert.Single(result.Plan!.Steps);
        Assert.Equal(OperationStepKind.ReorderChannel, step.Kind);
        Assert.Equal(2, step.BatchBeforeStates.Length);
        Assert.Equal(2, step.BatchAfterStates.Length);
        Assert.Equal(1, result.Plan.EstimatedRequestCount);
        var preview = planner.BuildPreview(result.Plan, "Test bot");
        Assert.Equal(
            2,
            preview.PropertyChanges.Count(change =>
                change.PropertyName.Contains("Position", StringComparison.Ordinal)));
    }

    [Fact]
    public void ChannelCloneCopiesOnlySupportedStructuralState()
    {
        var result = CreatePlanner().PlanClone(
            new CloneChannelRequest(
                OperationTestFixture.BotId,
                OperationTestFixture.ServerId,
                OperationTestFixture.TextId,
                "general-copy",
                OperationTestFixture.CategoryId,
                true,
                null));

        Assert.True(result.IsSuccess);
        var after = Assert.Single(result.Plan!.ProposedAfterState);
        Assert.Null(after.Id);
        Assert.Equal("Original topic", after.Topic);
        Assert.NotEmpty(after.PermissionOverwrites);
        Assert.Contains(PermissionBits.ManageRoles, result.Plan.RequiredBotPermissions);
    }

    [Fact]
    public void CategoryCloneCreatesParentThenBoundChildren()
    {
        var result = CreatePlanner().PlanCloneCategory(
            new CloneCategoryRequest(
                OperationTestFixture.BotId,
                OperationTestFixture.ServerId,
                OperationTestFixture.CategoryId,
                "Operations copy",
                [OperationTestFixture.TextId, OperationTestFixture.VoiceId],
                true,
                false,
                true,
                null));

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Plan!.Steps.Length);
        var parent = result.Plan.Steps[0];
        Assert.Equal(OperationStepKind.CreateCategory, parent.Kind);
        Assert.All(
            result.Plan.Steps.Skip(1),
            child => Assert.Equal(parent.StepId, child.ParentResultStepId));
    }

    [Fact]
    public void TextLockChangesOnlySelectedBitsAndRetainsCompensationBeforeState()
    {
        var result = CreatePlanner().PlanLock(
            LockRequest(OperationTestFixture.TextId, isUnlock: false, secondary: false));

        Assert.True(result.IsSuccess);
        var step = Assert.Single(result.Plan!.Steps);
        var change = Assert.IsType<PermissionOverwriteChange>(step.PermissionOverwriteChange);
        var sendMessages = 1UL << 11;
        var unrelatedAllowed = (1UL << 10) | (1UL << 6);
        var unrelatedDenied = 1UL << 15;
        Assert.Equal(unrelatedAllowed, change.After!.AllowedRaw);
        Assert.Equal(unrelatedDenied | sendMessages, change.After.DeniedRaw);
        Assert.Equal(unrelatedAllowed, change.Before!.AllowedRaw);
        Assert.Equal(unrelatedDenied, change.Before.DeniedRaw);
        Assert.Equal(change.Before, step.Compensation!.RestoreOverwrite);
    }

    [Fact]
    public void VoiceLockChangesConnectAndOptionalSpeakOnly()
    {
        var result = CreatePlanner().PlanLock(
            LockRequest(OperationTestFixture.VoiceId, isUnlock: false, secondary: true));

        var change = Assert.Single(result.Plan!.Steps).PermissionOverwriteChange!;
        var selected = (1UL << 20) | (1UL << 21);
        Assert.Equal(
            (1UL << 9) | selected,
            change.After!.DeniedRaw);
        Assert.Equal(1UL << 10, change.After.AllowedRaw);
    }

    [Fact]
    public void UnlockPreservesUnrelatedDeniedBits()
    {
        var server = OperationTestFixture.Server();
        var channels = server.Channels
            .Select(channel => channel.Id == OperationTestFixture.TextId
                ? channel with
                {
                    PermissionOverwrites =
                    [
                        OperationTestFixture.Overwrite(
                            OperationTestFixture.EveryoneRoleId,
                            PermissionBits.ViewChannel,
                            PermissionBits.SendMessages | PermissionBits.AttachFiles)
                    ]
                }
                : channel)
            .ToImmutableArray();
        var planner = CreatePlanner(server with { Channels = channels });

        var result = planner.PlanLock(
            LockRequest(OperationTestFixture.TextId, isUnlock: true, secondary: false));

        var after = Assert.Single(result.Plan!.Steps).PermissionOverwriteChange!.After!;
        Assert.Equal(1UL << 15, after.DeniedRaw);
        Assert.Equal(1UL << 10, after.AllowedRaw);
    }

    [Fact]
    public void LockCreatesMissingRoleOverwriteWithoutDeletingOthers()
    {
        var result = CreatePlanner().PlanLock(
            new ChannelLockRequest(
                OperationTestFixture.BotId,
                OperationTestFixture.ServerId,
                [OperationTestFixture.VoiceId],
                OperationTestFixture.TargetRoleId,
                false,
                false,
                null));

        var change = Assert.Single(result.Plan!.Steps).PermissionOverwriteChange!;
        Assert.Null(change.Before);
        Assert.Equal(1UL << 20, change.After!.DeniedRaw);
        Assert.Equal(
            OperationStepKind.DeletePermissionOverwrite,
            result.Plan.Steps[0].Compensation!.StepKind);
    }

    [Fact]
    public void PermissionSynchronizationShowsAddUpdateAndRemoveDiffs()
    {
        var result = CreatePlanner().PlanSynchronizePermissions(
            new SynchronizePermissionsRequest(
                OperationTestFixture.BotId,
                OperationTestFixture.ServerId,
                [OperationTestFixture.TextId],
                null));

        Assert.True(result.IsSuccess);
        var changes = result.Plan!.Steps
            .Select(step => step.PermissionOverwriteChange!)
            .ToArray();
        Assert.Contains(changes, change =>
            change.TargetId == OperationTestFixture.TargetRoleId && change.Before is null);
        Assert.Contains(changes, change =>
            change.TargetId == OperationTestFixture.EveryoneRoleId
            && change.Before is not null
            && change.After is not null);
        Assert.Contains(result.Plan.RequiredBotPermissions, permission =>
            permission == PermissionBits.ManageRoles);
    }

    [Fact]
    public void PermissionSynchronizationWithoutParentIsRejected()
    {
        var server = OperationTestFixture.Server();
        var channels = server.Channels
            .Select(channel => channel.Id == OperationTestFixture.TextId
                ? channel with { CategoryId = null, CategoryName = null }
                : channel)
            .ToImmutableArray();

        var result = CreatePlanner(server with { Channels = channels })
            .PlanSynchronizePermissions(
                new SynchronizePermissionsRequest(
                    OperationTestFixture.BotId,
                    OperationTestFixture.ServerId,
                    [OperationTestFixture.TextId],
                    null));

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error =>
            error.Contains("parent", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CategoryOnlyDeleteKeepsChildrenAndRequiresBackup()
    {
        var result = CreatePlanner().PlanDelete(
            DeleteRequest(deleteCategoryOnly: true, includeAllChildren: false));

        Assert.True(result.IsSuccess);
        Assert.Equal(ChannelOperationType.DeleteCategoryOnly, result.Plan!.OperationType);
        Assert.Single(result.Plan.Steps);
        Assert.True(result.Plan.IsDestructive);
        Assert.Contains(result.Plan.ExactBeforeState, state =>
            state.Id == OperationTestFixture.TextId);
        Assert.Contains(
            result.Plan.ProposedAfterState,
            state => state.Id == OperationTestFixture.TextId && state.ParentCategoryId is null);
    }

    [Fact]
    public void CategoryAndChildrenDeleteExpandsChildrenBeforeCategory()
    {
        var server = OperationTestFixture.Server();
        var supportedServer = server with
        {
            Channels = server.Channels
                .Where(channel => channel.Kind != ChannelKind.Forum)
                .ToImmutableArray()
        };
        var result = CreatePlanner(supportedServer).PlanDelete(
            DeleteRequest(deleteCategoryOnly: false, includeAllChildren: true));

        Assert.True(result.IsSuccess);
        Assert.Equal(ChannelOperationType.DeleteCategoryWithChildren, result.Plan!.OperationType);
        Assert.Equal(OperationTestFixture.CategoryId, result.Plan.Steps[^1].Target.Id);
        Assert.All(
            result.Plan.Steps[..^1],
            step => Assert.NotEqual(OperationTestFixture.CategoryId, step.Target.Id));
        Assert.Equal(OperationConfirmationKind.TypedTextAndServerName, result.Plan.ConfirmationRequirement.Kind);
        Assert.Contains(
            $"DELETE {result.Plan.Steps.Length} CHANNELS",
            result.Plan.ConfirmationRequirement.RequiredText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MultipleOrdinaryDeletesUseExactTypedCount()
    {
        var result = CreatePlanner().PlanDelete(
            new DeleteChannelsRequest(
                OperationTestFixture.BotId,
                OperationTestFixture.ServerId,
                [OperationTestFixture.TextId, OperationTestFixture.VoiceId],
                false,
                false,
                [],
                null));

        Assert.True(result.IsSuccess);
        Assert.Equal("DELETE 2 CHANNELS", result.Plan!.ConfirmationRequirement.RequiredText);
        Assert.Equal(OperationRiskLevel.Irreversible, result.Plan.RiskLevel);
    }

    [Fact]
    public void UnsupportedForumDeletionIsBlocked()
    {
        var result = CreatePlanner().PlanDelete(
            new DeleteChannelsRequest(
                OperationTestFixture.BotId,
                OperationTestFixture.ServerId,
                [OperationTestFixture.ForumId],
                false,
                false,
                [],
                null));

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error =>
            error.Contains("unsupported", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AuditReasonIsSanitizedAndBounded()
    {
        var unsafeReason = new string('x', 600) + "\r\nsecret";
        var result = CreatePlanner().PlanCreate(
            CreateRequest(TextCreation("audit-test")) with { AuditReason = unsafeReason });

        Assert.True(result.IsSuccess);
        Assert.Equal(480, result.Plan!.AuditReason!.Length);
        Assert.DoesNotContain('\r', result.Plan.AuditReason);
        Assert.DoesNotContain('\n', result.Plan.AuditReason);
    }

    private static ChannelOperationPlanner CreatePlanner(ServerReadModel? server = null) =>
        new(
            new FakeOperationExplorer(
                OperationTestFixture.Snapshot(server ?? OperationTestFixture.Server())));

    private static ChannelCreationItem TextCreation(string name) =>
        new(
            name,
            ChannelKind.Text,
            OperationTestFixture.CategoryId,
            null,
            false,
            0,
            null,
            null,
            null,
            false);

    private static CreateChannelsRequest CreateRequest(
        params ChannelCreationItem[] items) =>
        new(
            OperationTestFixture.BotId,
            OperationTestFixture.ServerId,
            items.ToImmutableArray(),
            null);

    private static ChannelLockRequest LockRequest(
        ulong channelId,
        bool isUnlock,
        bool secondary) =>
        new(
            OperationTestFixture.BotId,
            OperationTestFixture.ServerId,
            [channelId],
            OperationTestFixture.EveryoneRoleId,
            isUnlock,
            secondary,
            null);

    private static DeleteChannelsRequest DeleteRequest(
        bool deleteCategoryOnly,
        bool includeAllChildren) =>
        new(
            OperationTestFixture.BotId,
            OperationTestFixture.ServerId,
            [OperationTestFixture.CategoryId],
            deleteCategoryOnly,
            includeAllChildren,
            [],
            null);
}
