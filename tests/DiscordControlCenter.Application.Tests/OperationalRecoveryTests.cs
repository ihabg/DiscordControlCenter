using System.Collections.Immutable;
using System.Text.Json;
using DiscordControlCenter.Application.Operations;
using DiscordControlCenter.Core.Explorer;
using DiscordControlCenter.Core.Operations;

namespace DiscordControlCenter.Application.Tests;

public sealed class OperationalRecoveryTests
{
    [Fact]
    public void VoiceValidationUsesModeledServerTier()
    {
        var service = new VoiceChannelValidationService();
        var standard = OperationTestFixture.Server() with { BoostTier = "None" };
        var tierTwo = standard with { BoostTier = "Tier2" };

        var rejected = service.Validate(standard, 128_000, 0, null);
        var accepted = service.Validate(tierTwo, 256_000, 99, null);

        Assert.False(rejected.IsValid);
        Assert.Contains(rejected.Errors, error => error.Contains("96,000", StringComparison.Ordinal));
        Assert.True(accepted.IsValid);
        Assert.Empty(accepted.Errors);
        Assert.Equal(256_000, accepted.Capabilities.MaximumBitrate);
    }

    [Fact]
    public void VoiceValidationWarnsWhenExactCapabilityOrRegionIsUnknown()
    {
        var service = new VoiceChannelValidationService();
        var server = OperationTestFixture.Server() with { BoostTier = "FutureTier" };

        var result = service.Validate(server, 128_000, 10, "rotterdam");

        Assert.True(result.IsValid);
        Assert.False(result.Capabilities.IsBitrateCapabilityCertain);
        Assert.Equal(2, result.Warnings.Length);
    }

    [Fact]
    public void RecreateMixedStructureUsesDependencyOrderAndNewIds()
    {
        var server = OperationTestFixture.Server();
        var planner = CreatePlanner(server);
        var backup = Backup(
            State(OperationTestFixture.CategoryId, "Recovered", ChannelKind.Category, 0),
            State(OperationTestFixture.TextId, "alpha", ChannelKind.Text, 0, OperationTestFixture.CategoryId),
            State(OperationTestFixture.VoiceId, "briefing", ChannelKind.Voice, 1, OperationTestFixture.CategoryId),
            State(OperationTestFixture.OtherTextId, "beta", ChannelKind.Text, 2, OperationTestFixture.CategoryId));
        var resources = backup.Channels
            .Select((channel, index) => new RecreateResourceSelection(
                index,
                true,
                $"{channel.Name}-replacement",
                null,
                false))
            .ToImmutableArray();

        var result = planner.Plan(Request(backup, resources));

        Assert.True(result.IsSuccess, string.Join(" ", result.Errors));
        var plan = Assert.IsType<OperationPlan>(result.Plan);
        Assert.Equal(ChannelOperationType.RecreateStructure, plan.OperationType);
        Assert.Equal("backup-4b", plan.SourceBackupIdentifier);
        Assert.Equal(RecreateCompensationPolicy.KeepSuccessfulResources, plan.RecreateCompensationPolicy);
        Assert.All(plan.ProposedAfterState, state => Assert.Null(state.Id));
        Assert.Equal(OperationStepKind.CreateCategory, plan.Steps[0].Kind);
        Assert.All(
            plan.Steps.Skip(1).Take(3),
            step =>
            {
                Assert.Contains(
                    step.Kind,
                    new[] { OperationStepKind.CreateTextChannel, OperationStepKind.CreateVoiceChannel });
                Assert.Equal(plan.Steps[0].StepId, step.ParentResultStepId);
            });
        var reorder = Assert.Single(plan.Steps.Where(step => step.Kind == OperationStepKind.ReorderChannel));
        Assert.Equal(4, reorder.BatchAfterStates.Length);
        Assert.Equal(4, reorder.BatchResultStepIds.Length);
        Assert.Equal("RECREATE 3 CHANNELS", plan.ConfirmationRequirement.RequiredText);
        Assert.Equal(plan.Steps.Length, plan.EstimatedRequestCount);
    }

    [Fact]
    public void RecreateCanMapChildrenToExistingCategoryWithoutCreatingIt()
    {
        var server = OperationTestFixture.Server();
        var planner = CreatePlanner(server);
        var backup = Backup(
            State(900, "Legacy", ChannelKind.Category, 0),
            State(901, "legacy-chat", ChannelKind.Text, 0, 900));
        var resources = ImmutableArray.Create(
            new RecreateResourceSelection(
                0,
                true,
                "Legacy",
                OperationTestFixture.CategoryId,
                false),
            new RecreateResourceSelection(1, true, "legacy-chat-replacement", null, false));

        var result = planner.Plan(Request(backup, resources));

        Assert.True(result.IsSuccess, string.Join(" ", result.Errors));
        var plan = Assert.IsType<OperationPlan>(result.Plan);
        var create = Assert.Single(plan.Steps);
        Assert.Equal(OperationStepKind.CreateTextChannel, create.Kind);
        Assert.Equal(OperationTestFixture.CategoryId, create.After!.ParentCategoryId);
        Assert.Null(create.ParentResultStepId);
        Assert.Contains(OperationTestFixture.CategoryId, plan.ExactTargetIds);
    }

    [Fact]
    public void RecreateBlocksNameConflictsAndUnresolvedCriticalRoleMappings()
    {
        var server = OperationTestFixture.Server();
        var planner = CreatePlanner(server);
        var backup = Backup(
            State(
                901,
                "legacy-chat",
                ChannelKind.Text,
                0,
                null,
                [
                    new ChannelPermissionOverwriteSnapshot(
                        999,
                        PermissionTargetKind.Role,
                        "Missing role",
                        1,
                        0)
                ]));
        var resources = ImmutableArray.Create(
            new RecreateResourceSelection(0, true, "general", null, true));
        var unresolved = ImmutableArray.Create(
            new RoleMapping(
                999,
                PermissionTargetKind.Role,
                "Missing role",
                null,
                null,
                RoleMappingChoice.Manual,
                true,
                false));

        var result = planner.Plan(Request(backup, resources, unresolved, includeOverwrites: true));

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error => error.Contains("already exists", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("critical role mapping", StringComparison.Ordinal));
    }

    [Fact]
    public void RecreateExcludesMemberOverwritesUnlessExplicitlyResolved()
    {
        var server = OperationTestFixture.Server();
        var planner = CreatePlanner(server);
        var backup = Backup(
            State(
                901,
                "legacy-chat",
                ChannelKind.Text,
                0,
                null,
                [
                    new ChannelPermissionOverwriteSnapshot(
                        777,
                        PermissionTargetKind.User,
                        "Former member",
                        1,
                        0)
                ]));
        var resources = ImmutableArray.Create(
            new RecreateResourceSelection(0, true, "legacy-chat-replacement", null, true));

        var result = planner.Plan(Request(backup, resources));

        Assert.True(result.IsSuccess, string.Join(" ", result.Errors));
        Assert.Empty(result.Plan!.ProposedAfterState[0].PermissionOverwrites);
    }

    [Fact]
    public void RecreateRejectsUnsupportedBackupResource()
    {
        var server = OperationTestFixture.Server();
        var planner = CreatePlanner(server);
        var backup = Backup(State(904, "legacy-forum", ChannelKind.Forum, 0));
        var resources = ImmutableArray.Create(
            new RecreateResourceSelection(0, true, "legacy-forum-replacement", null, true));

        var result = planner.Plan(Request(backup, resources));

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error => error.Contains("unsupported", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StartupRecoveryDoesNotReplayOperationThatWasNotStarted()
    {
        var plan = ChannelOperationExecutionTests.CreatePlan(1);
        var history = new MemoryHistoryRepository();
        await history.AddAsync(History(plan, ChannelOperationState.Running), CancellationToken.None);
        var journal = new RecoveryJournal();
        var reconciliation = new RecordingReconciliation(
            OperationReconciliationStatus.ConfirmedNotApplied);
        var service = new OperationRecoveryService(
            history,
            journal,
            journal,
            OperationTestFixture.Explorer(),
            reconciliation);

        var assessments = await service.InspectInterruptedAsync(CancellationToken.None);

        var assessment = Assert.Single(assessments);
        Assert.Equal(RecoveryClassification.NotStarted, assessment.Classification);
        Assert.True(assessment.RequiresUserApproval);
        Assert.Equal(1, reconciliation.CallCount);
        var updated = await history.GetAsync(plan.OperationId, CancellationToken.None);
        Assert.Equal(ChannelOperationState.ReconciliationRequired, updated!.State);
        Assert.Equal("STARTUP_RECOVERY_REQUIRED", updated.SafeErrorCodes);
        Assert.Single(journal.Transitions);
    }

    [Fact]
    public async Task StartupRecoveryUsesCheckpointThenInspectsOnlyIncompleteStep()
    {
        var plan = ChannelOperationExecutionTests.CreatePlan(2);
        var now = DateTimeOffset.UtcNow;
        var completedStep = new OperationStepResult(
            plan.Steps[0].StepId,
            plan.Steps[0].Order,
            plan.Steps[0].Description,
            true,
            false,
            701,
            now,
            now,
            1,
            null,
            false,
            false);
        var checkpoint = new ChannelOperationResult(
            plan.OperationId,
            plan.CorrelationId,
            ChannelOperationState.Running,
            now,
            now,
            [completedStep],
            1,
            0,
            0,
            null,
            new OperationReconciliationResult(
                OperationReconciliationStatus.NotRequired,
                "Checkpoint",
                [],
                now),
            null,
            plan.CompensationCapability,
            "Not run");
        var history = new MemoryHistoryRepository();
        await history.AddAsync(
            History(plan, ChannelOperationState.Running, checkpoint),
            CancellationToken.None);
        var journal = new RecoveryJournal();
        var reconciliation = new RecordingReconciliation(
            OperationReconciliationStatus.ConfirmedApplied,
            [702]);
        var service = new OperationRecoveryService(
            history,
            journal,
            journal,
            OperationTestFixture.Explorer(),
            reconciliation);

        var assessment = Assert.Single(
            await service.InspectInterruptedAsync(CancellationToken.None));

        Assert.Equal(RecoveryClassification.CompletedAfterReconciliation, assessment.Classification);
        Assert.Equal(2, assessment.ReconciledSteps.Count(step => step.Succeeded));
        Assert.Equal(1, reconciliation.CallCount);
        var updated = await history.GetAsync(plan.OperationId, CancellationToken.None);
        Assert.Equal(ChannelOperationState.Completed, updated!.State);
        Assert.Null(updated.SafeErrorCodes);
    }

    [Fact]
    public async Task StartupRecoveryClassifiesUnavailableServerWithoutMutationOrInspection()
    {
        var plan = ChannelOperationExecutionTests.CreatePlan(1);
        var history = new MemoryHistoryRepository();
        await history.AddAsync(History(plan, ChannelOperationState.Running), CancellationToken.None);
        var journal = new RecoveryJournal();
        var reconciliation = new RecordingReconciliation(
            OperationReconciliationStatus.ConfirmedApplied);
        var explorer = new FakeOperationExplorer(
            BotExplorerSnapshot.Disconnected(OperationTestFixture.BotId));
        var service = new OperationRecoveryService(
            history,
            journal,
            journal,
            explorer,
            reconciliation);

        var assessment = Assert.Single(
            await service.InspectInterruptedAsync(CancellationToken.None));

        Assert.Equal(RecoveryClassification.UnableToInspect, assessment.Classification);
        Assert.Equal(0, reconciliation.CallCount);
        Assert.Contains("No Discord mutation", assessment.SafeSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartupRecoveryBlocksUnsupportedPersistedPlanSchema()
    {
        var plan = ChannelOperationExecutionTests.CreatePlan(1) with { SchemaVersion = 99 };
        var history = new MemoryHistoryRepository();
        await history.AddAsync(History(plan, ChannelOperationState.Running), CancellationToken.None);
        var journal = new RecoveryJournal();
        var reconciliation = new RecordingReconciliation(
            OperationReconciliationStatus.ConfirmedApplied);
        var service = new OperationRecoveryService(
            history,
            journal,
            journal,
            OperationTestFixture.Explorer(),
            reconciliation);

        var assessment = Assert.Single(
            await service.InspectInterruptedAsync(CancellationToken.None));

        Assert.Equal(RecoveryClassification.UnsupportedPlanSchema, assessment.Classification);
        Assert.Equal(0, reconciliation.CallCount);
        Assert.True(assessment.RequiresUserApproval);
    }

    private static RecreateStructurePlanner CreatePlanner(ServerReadModel server)
    {
        var explorer = new FakeOperationExplorer(OperationTestFixture.Snapshot(server));
        var voiceValidation = new VoiceChannelValidationService();
        return new RecreateStructurePlanner(
            explorer,
            new ChannelOperationPlanner(explorer, voiceValidation),
            voiceValidation);
    }

    private static RecreateStructureRequest Request(
        ServerStructureBackup backup,
        ImmutableArray<RecreateResourceSelection> resources,
        ImmutableArray<RoleMapping>? mappings = null,
        bool includeOverwrites = false) =>
        new(
            OperationTestFixture.BotId,
            OperationTestFixture.ServerId,
            backup.BackupIdentifier,
            backup,
            resources,
            mappings ?? [],
            includeOverwrites,
            RecreateCompensationPolicy.KeepSuccessfulResources,
            "Phase 4B test");

    private static ServerStructureBackup Backup(
        params ChannelOperationStateSnapshot[] states) =>
        new(
            "backup-4b",
            Guid.NewGuid(),
            Guid.NewGuid(),
            OperationTestFixture.BotId,
            OperationTestFixture.ServerId,
            "Disposable Test Server",
            10,
            DateTimeOffset.UtcNow,
            states.ToImmutableArray());

    private static ChannelOperationStateSnapshot State(
        ulong id,
        string name,
        ChannelKind kind,
        int position,
        ulong? parentId = null,
        ImmutableArray<ChannelPermissionOverwriteSnapshot>? overwrites = null) =>
        new(
            id,
            name,
            kind,
            position,
            parentId,
            parentId is null ? null : "Recovered",
            kind == ChannelKind.Text ? "Recovered topic" : null,
            kind == ChannelKind.Text ? false : null,
            kind == ChannelKind.Text ? 0 : null,
            kind == ChannelKind.Text ? 60 : null,
            kind == ChannelKind.Voice ? 64_000 : null,
            kind == ChannelKind.Voice ? 0 : null,
            null,
            overwrites ?? []);

    private static OperationHistoryEntry History(
        OperationPlan plan,
        ChannelOperationState state,
        ChannelOperationResult? result = null) =>
        new(
            plan.OperationId,
            plan.CorrelationId,
            plan.OperationType,
            plan.BotProfileId,
            plan.ServerId,
            plan.ServerNameSnapshot,
            string.Join(',', plan.ExactTargetIds),
            string.Join(", ", plan.Steps.Select(step => step.Target.DisplayName)),
            plan.CreatedAt,
            plan.CreatedAt,
            null,
            state,
            result?.CompletedCount ?? 0,
            result?.FailedCount ?? 0,
            result?.CancelledCount ?? 0,
            "Not run",
            null,
            null,
            0,
            plan.AuditReason,
            JsonSerializer.Serialize(plan),
            result is null ? null : JsonSerializer.Serialize(result));
}

internal sealed class RecoveryJournal :
    IOperationHistoryQueryRepository,
    IManualReconciliationRepository
{
    public List<OperationStateTransition> Transitions { get; } = [];
    public List<ManualReconciliationDecision> Decisions { get; } = [];

    public Task<PagedResult<OperationHistoryEntry>> QueryAsync(
        OperationHistoryQuery query,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<OperationHistoryDetail?> GetDetailAsync(
        Guid operationId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task AddTransitionAsync(
        OperationStateTransition transition,
        CancellationToken cancellationToken)
    {
        Transitions.Add(transition);
        return Task.CompletedTask;
    }

    public Task AddManualDecisionAsync(
        ManualReconciliationDecision decision,
        CancellationToken cancellationToken)
    {
        Decisions.Add(decision);
        return Task.CompletedTask;
    }

    public Task AddAsync(
        ManualReconciliationDecision decision,
        CancellationToken cancellationToken)
    {
        Decisions.Add(decision);
        return Task.CompletedTask;
    }
}

internal sealed class RecordingReconciliation(
    OperationReconciliationStatus status,
    ImmutableArray<ulong>? matches = null) : IOperationReconciliationService
{
    private int _callCount;
    public int CallCount => Volatile.Read(ref _callCount);

    public Task<OperationReconciliationResult> ReconcileAsync(
        OperationPlan plan,
        OperationStep operationStep,
        ChannelWriteOutcome uncertainOutcome,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _callCount);
        return Task.FromResult(
            new OperationReconciliationResult(
                status,
                status.ToString(),
                matches ?? [],
                DateTimeOffset.UtcNow));
    }
}
