using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using DiscordControlCenter.Application.Explorer;
using DiscordControlCenter.Core.Explorer;
using DiscordControlCenter.Core.Operations;

namespace DiscordControlCenter.Application.Operations;

public interface IBackupCatalogService
{
    Task<PagedResult<BackupCatalogItem>> QueryAsync(
        BackupQuery query,
        CancellationToken cancellationToken);

    Task<BackupDetail?> GetDetailAsync(
        string backupIdentifier,
        CancellationToken cancellationToken);

    Task SetPinnedAsync(string backupIdentifier, bool pinned, CancellationToken cancellationToken);

    Task DeleteLocalAsync(
        IReadOnlyCollection<string> backupIdentifiers,
        string safeReason,
        CancellationToken cancellationToken);

    Task<BackupRetentionPolicy> GetRetentionPolicyAsync(CancellationToken cancellationToken);

    Task SaveRetentionPolicyAsync(
        BackupRetentionPolicy policy,
        CancellationToken cancellationToken);

    Task<BackupCleanupPreview> PreviewCleanupAsync(
        BackupRetentionPolicy policy,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

public interface IRecreateStructurePlanner
{
    ChannelPlanResult Plan(RecreateStructureRequest request);

    OperationPreview BuildPreview(OperationPlan plan, string botDisplayName);
}

public interface IVoiceChannelValidationService
{
    VoiceChannelCapabilities GetCapabilities(ServerReadModel server);

    VoiceChannelValidationResult Validate(
        ServerReadModel server,
        int? bitrate,
        int? userLimit,
        string? regionOverride);
}

public interface IOperationRecoveryService
{
    Task<ImmutableArray<RecoveryAssessment>> InspectInterruptedAsync(
        CancellationToken cancellationToken);

    Task RecordDecisionAsync(
        ManualReconciliationDecision decision,
        CancellationToken cancellationToken);
}

public interface IOperationExportService
{
    Task<int> ExportHistoryJsonAsync(
        Stream destination,
        OperationHistoryQuery query,
        CancellationToken cancellationToken);

    Task<int> ExportHistoryCsvAsync(
        Stream destination,
        OperationHistoryQuery query,
        CancellationToken cancellationToken);

    Task<int> ExportBackupMetadataJsonAsync(
        Stream destination,
        BackupQuery query,
        CancellationToken cancellationToken);
}

public sealed class BackupCatalogService(
    IBackupCatalogRepository catalogRepository,
    IOperationBackupRepository backupRepository,
    IBotExplorerService explorer) : IBackupCatalogService
{
    private static readonly ImmutableArray<string> UnrecoverableData =
    [
        "Original Discord channel IDs",
        "Messages, threads, pins, and message links",
        "Webhook identities, invites, integrations, and external references",
        "Voice history or voice content"
    ];

    public async Task<PagedResult<BackupCatalogItem>> QueryAsync(
        BackupQuery query,
        CancellationToken cancellationToken)
    {
        if (query.Compatibility is BackupCompatibility.FullySupported
            or BackupCompatibility.PartiallySupported
            or BackupCompatibility.Unsupported)
        {
            return await QueryByLiveCompatibilityAsync(query, cancellationToken)
                .ConfigureAwait(false);
        }

        var page = await catalogRepository.QueryAsync(query, cancellationToken).ConfigureAwait(false);
        var enriched = ImmutableArray.CreateBuilder<BackupCatalogItem>(page.Items.Length);
        foreach (var item in page.Items)
        {
            enriched.Add(await ClassifyAsync(item, cancellationToken).ConfigureAwait(false));
        }

        return page with { Items = enriched.ToImmutable() };
    }

    public async Task<BackupDetail?> GetDetailAsync(
        string backupIdentifier,
        CancellationToken cancellationToken)
    {
        var catalog = await catalogRepository
            .GetCatalogItemAsync(backupIdentifier, cancellationToken)
            .ConfigureAwait(false);
        if (catalog is null)
        {
            return null;
        }

        ServerStructureBackup? backup;
        try
        {
            backup = await backupRepository.GetAsync(backupIdentifier, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            return new BackupDetail(
                catalog with
                {
                    Compatibility = BackupCompatibility.Corrupt,
                    SafeIssue = "The structural backup JSON is corrupt."
                },
                null,
                [],
                [],
                UnrecoverableData,
                null);
        }

        if (backup is null)
        {
            return null;
        }

        var enriched = Enrich(catalog, backup);
        var server = CurrentServer(backup.BotProfileId, backup.ServerId);
        var roleIds = server?.Roles.Select(role => role.Id).ToHashSet() ?? [];
        var missingRoles = backup.Channels
            .SelectMany(channel => channel.PermissionOverwrites)
            .Where(overwrite => overwrite.TargetType == PermissionTargetKind.Role)
            .Select(overwrite => overwrite.TargetId)
            .Where(id => !roleIds.Contains(id))
            .Distinct()
            .Order()
            .ToImmutableArray();
        var unsupported = backup.Channels
            .Where(channel => channel.Kind is not (
                ChannelKind.Category or ChannelKind.Text or ChannelKind.Voice))
            .Select(channel => $"{channel.Name}: {channel.Kind} cannot be recreated safely.")
            .ToImmutableArray();
        var technicalJson = JsonSerializer.Serialize(
            backup,
            OperationalJson.Indented);
        return new BackupDetail(
            enriched,
            backup,
            unsupported,
            missingRoles,
            UnrecoverableData,
            technicalJson);
    }

    public Task SetPinnedAsync(
        string backupIdentifier,
        bool pinned,
        CancellationToken cancellationToken) =>
        catalogRepository.SetPinnedAsync(backupIdentifier, pinned, cancellationToken);

    public Task DeleteLocalAsync(
        IReadOnlyCollection<string> backupIdentifiers,
        string safeReason,
        CancellationToken cancellationToken) =>
        catalogRepository.DeleteLocalAsync(backupIdentifiers, safeReason, cancellationToken);

    public Task<BackupRetentionPolicy> GetRetentionPolicyAsync(CancellationToken cancellationToken) =>
        catalogRepository.GetRetentionPolicyAsync(cancellationToken);

    public Task SaveRetentionPolicyAsync(
        BackupRetentionPolicy policy,
        CancellationToken cancellationToken) =>
        catalogRepository.SaveRetentionPolicyAsync(policy, cancellationToken);

    public Task<BackupCleanupPreview> PreviewCleanupAsync(
        BackupRetentionPolicy policy,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        catalogRepository.PreviewCleanupAsync(policy, now, cancellationToken);

    private async Task<PagedResult<BackupCatalogItem>> QueryByLiveCompatibilityAsync(
        BackupQuery query,
        CancellationToken cancellationToken)
    {
        var requested = query.Compatibility!.Value;
        var firstIndex = (query.PageNumber - 1) * query.PageSize;
        var lastIndex = firstIndex + query.PageSize;
        var items = ImmutableArray.CreateBuilder<BackupCatalogItem>(query.PageSize);
        var matchingCount = 0;
        var sourcePage = 1;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await catalogRepository.QueryAsync(
                    query with
                    {
                        Compatibility = null,
                        PageNumber = sourcePage,
                        PageSize = 100
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            foreach (var item in page.Items)
            {
                var classified = await ClassifyAsync(item, cancellationToken).ConfigureAwait(false);
                if (classified.Compatibility != requested)
                {
                    continue;
                }

                if (matchingCount >= firstIndex && matchingCount < lastIndex)
                {
                    items.Add(classified);
                }

                matchingCount++;
            }

            if (sourcePage >= page.TotalPages)
            {
                break;
            }

            sourcePage++;
        }

        return new PagedResult<BackupCatalogItem>(
            items.ToImmutable(),
            query.PageNumber,
            query.PageSize,
            matchingCount);
    }

    private async Task<BackupCatalogItem> ClassifyAsync(
        BackupCatalogItem item,
        CancellationToken cancellationToken)
    {
        try
        {
            var backup = await backupRepository
                .GetAsync(item.BackupIdentifier, cancellationToken)
                .ConfigureAwait(false);
            return backup is null
                ? item with
                {
                    Compatibility = BackupCompatibility.Corrupt,
                    SafeIssue = "The structural backup record is missing."
                }
                : Enrich(item, backup);
        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            return item with
            {
                Compatibility = BackupCompatibility.Corrupt,
                SafeIssue = "The structural backup JSON is corrupt."
            };
        }
    }

    private BackupCatalogItem Enrich(
        BackupCatalogItem item,
        ServerStructureBackup backup)
    {
        if (backup.SchemaVersion > 2)
        {
            return item with
            {
                Compatibility = BackupCompatibility.NewerSchema,
                SafeIssue = "The backup schema is newer than this application."
            };
        }

        var server = CurrentServer(backup.BotProfileId, backup.ServerId);
        var roleIds = server?.Roles.Select(role => role.Id).ToHashSet() ?? [];
        var missingRole = backup.Channels
            .SelectMany(channel => channel.PermissionOverwrites)
            .Any(overwrite =>
                overwrite.TargetType == PermissionTargetKind.Role
                && !roleIds.Contains(overwrite.TargetId));
        var supportedCount = backup.Channels.Count(channel =>
            channel.Kind is ChannelKind.Category or ChannelKind.Text or ChannelKind.Voice);
        var unsupported = supportedCount != backup.Channels.Length;
        var compatibility = server is null
            ? BackupCompatibility.PartiallySupported
            : supportedCount == 0 && backup.Channels.Length > 0
                ? BackupCompatibility.Unsupported
                : unsupported || missingRole
                ? BackupCompatibility.PartiallySupported
                : BackupCompatibility.FullySupported;
        return item with
        {
            IsServerAccessible = server?.Availability == ServerAvailability.Available,
            AllReferencedRolesExist = !missingRole,
            Compatibility = compatibility,
            SafeIssue = server is null
                ? "The original server is not currently accessible to the source bot."
                : unsupported
                    ? "Some resources use unsupported channel types."
                    : missingRole
                        ? "One or more referenced roles no longer exist."
                        : null
        };
    }

    private ServerReadModel? CurrentServer(Guid botProfileId, ulong serverId) =>
        explorer.GetSnapshot(botProfileId).Servers.FirstOrDefault(server => server.Id == serverId);
}

public sealed class VoiceChannelValidationService : IVoiceChannelValidationService
{
    private const int MinimumBitrate = 8_000;
    private const int MaximumUserLimit = 99;

    public VoiceChannelCapabilities GetCapabilities(ServerReadModel server)
    {
        var maximum = server.BoostTier.ToUpperInvariant() switch
        {
            "TIER1" => 128_000,
            "TIER2" => 256_000,
            "TIER3" => 384_000,
            "NONE" => 96_000,
            _ => (int?)null
        };
        return new VoiceChannelCapabilities(
            MinimumBitrate,
            maximum,
            MaximumUserLimit,
            ImmutableArray<string>.Empty,
            maximum is not null,
            maximum is null
                ? "Discord.Net validation; exact server tier capability is unknown."
                : $"Server boost tier {server.BoostTier}.");
    }

    public VoiceChannelValidationResult Validate(
        ServerReadModel server,
        int? bitrate,
        int? userLimit,
        string? regionOverride)
    {
        var capabilities = GetCapabilities(server);
        var errors = ImmutableArray.CreateBuilder<string>();
        var warnings = ImmutableArray.CreateBuilder<string>();
        if (bitrate is { } value)
        {
            if (value < capabilities.MinimumBitrate)
            {
                errors.Add($"Bitrate must be at least {capabilities.MinimumBitrate:N0} bps.");
            }
            else if (capabilities.MaximumBitrate is { } maximum && value > maximum)
            {
                errors.Add(
                    $"Bitrate exceeds the modeled {server.BoostTier} server limit of {maximum:N0} bps.");
            }
            else if (!capabilities.IsBitrateCapabilityCertain && value > 96_000)
            {
                warnings.Add(
                    "The exact server bitrate capability is unknown. Discord will validate this value; an invalid-value rejection will not be retried.");
            }
        }

        if (userLimit is < 0 or > MaximumUserLimit)
        {
            errors.Add($"User limit must be between 0 and {MaximumUserLimit}.");
        }

        if (!string.IsNullOrWhiteSpace(regionOverride)
            && capabilities.SupportedRegions.Length > 0
            && !capabilities.SupportedRegions.Contains(
                regionOverride,
                StringComparer.OrdinalIgnoreCase))
        {
            errors.Add("The requested region override is not supported by the current server model.");
        }
        else if (!string.IsNullOrWhiteSpace(regionOverride)
                 && capabilities.SupportedRegions.Length == 0)
        {
            warnings.Add(
                "Available voice regions are not exposed by the current snapshot; Discord.Net will validate the override.");
        }

        return new VoiceChannelValidationResult(
            errors.Count == 0,
            errors.ToImmutable(),
            warnings.ToImmutable(),
            capabilities);
    }
}

public sealed class RecreateStructurePlanner(
    IBotExplorerService explorer,
    IChannelOperationPlanner channelPlanner,
    IVoiceChannelValidationService voiceValidation) : IRecreateStructurePlanner
{
    public ChannelPlanResult Plan(RecreateStructureRequest request)
    {
        if (request.Backup.SchemaVersion > 2)
        {
            return ChannelPlanResult.Failure("The selected backup uses a newer unsupported schema.");
        }

        var snapshot = explorer.GetSnapshot(request.BotProfileId);
        var server = snapshot.Servers.FirstOrDefault(candidate =>
            candidate.Id == request.ServerId && candidate.Availability == ServerAvailability.Available);
        if (snapshot.State != ExplorerCacheState.Ready || server is null)
        {
            return ChannelPlanResult.Failure("The target bot must be connected to an available target server.");
        }

        var selected = request.Resources.Where(resource => resource.Include).ToArray();
        if (selected.Length == 0)
        {
            return ChannelPlanResult.Failure("Select at least one supported backup resource.");
        }

        var errors = new List<string>();
        var validSelections = selected
            .Where(resource => resource.BackupIndex >= 0
                               && resource.BackupIndex < request.Backup.Channels.Length)
            .ToArray();
        var duplicateIndexes = validSelections
            .GroupBy(resource => resource.BackupIndex)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        var indexed = validSelections
            .GroupBy(resource => resource.BackupIndex)
            .ToDictionary(group => group.Key, group => group.First());
        if (indexed.Count != selected.Length || duplicateIndexes.Length > 0)
        {
            errors.Add("One or more resource selections no longer match the backup.");
        }

        var selectedStates = indexed
            .Select(pair => (Index: pair.Key, Selection: pair.Value, State: request.Backup.Channels[pair.Key]))
            .ToArray();
        foreach (var item in selectedStates)
        {
            if (item.State.Kind is not (ChannelKind.Category or ChannelKind.Text or ChannelKind.Voice))
            {
                errors.Add($"{item.State.Name} uses unsupported type {item.State.Kind}.");
            }

            if (string.IsNullOrWhiteSpace(item.Selection.ProposedName)
                || item.Selection.ProposedName.Trim().Length > 100)
            {
                errors.Add($"{item.State.Name} has an invalid proposed replacement name.");
            }

            if (item.Selection.ExistingCategoryId is { } existingCategoryId
                && (item.State.Kind != ChannelKind.Category
                    || !server.Channels.Any(channel =>
                        channel.Id == existingCategoryId
                        && channel.Kind == ChannelKind.Category)))
            {
                errors.Add(
                    $"{item.State.Name} has an unavailable or ambiguous existing-category mapping.");
            }
        }

        var duplicateNames = selectedStates
            .Where(item => item.Selection.ExistingCategoryId is null)
            .GroupBy(
                item => (item.State.Kind, Name: item.Selection.ProposedName.Trim()),
                new KindNameComparer())
            .Where(group => group.Count() > 1)
            .Select(group => group.Key.Name);
        errors.AddRange(duplicateNames.Select(name => $"Planned name “{name}” is duplicated."));
        foreach (var item in selectedStates.Where(item => item.Selection.ExistingCategoryId is null))
        {
            if (server.Channels.Any(channel =>
                    channel.Kind == item.State.Kind
                    && string.Equals(
                        channel.Name,
                        item.Selection.ProposedName.Trim(),
                        StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"A target {item.State.Kind} named “{item.Selection.ProposedName.Trim()}” already exists.");
            }

            if (item.State.Kind == ChannelKind.Voice)
            {
                var validation = voiceValidation.Validate(
                    server,
                    item.State.Bitrate,
                    item.State.UserLimit,
                    item.State.RegionOverride);
                errors.AddRange(validation.Errors.Select(error => $"{item.State.Name}: {error}"));
            }
        }

        var roleMappings = request.RoleMappings.ToDictionary(mapping =>
            (mapping.OriginalTargetId, mapping.TargetType));
        if (request.IncludePermissionOverwrites
            && request.RoleMappings.Any(mapping => mapping.IsCritical && !mapping.IsResolved))
        {
            errors.Add("Every critical role mapping must be resolved before recreation.");
        }

        if (server.Channels.Length
            + selectedStates.Count(item => item.Selection.ExistingCategoryId is null) > 500)
        {
            errors.Add("The recreation would exceed the determinable 500-channel server limit.");
        }

        if (errors.Count > 0)
        {
            return ChannelPlanResult.Failure(errors);
        }

        var steps = new List<OperationStep>();
        var categoryCreateSteps = new Dictionary<int, OperationStep>();
        var order = 1;
        foreach (var item in selectedStates
                     .Where(item => item.State.Kind == ChannelKind.Category
                                    && item.Selection.ExistingCategoryId is null)
                     .OrderBy(item => item.State.Position))
        {
            var after = MapState(item.State, item.Selection, null, null, request, roleMappings);
            var step = CreateStep(order++, after, $"Create replacement category {after.Name}");
            steps.Add(step);
            categoryCreateSteps[item.Index] = step;
        }

        var channelCreateSteps = new List<(OperationStep Step, ChannelOperationStateSnapshot State)>();
        foreach (var item in selectedStates
                     .Where(item => item.State.Kind is ChannelKind.Text or ChannelKind.Voice)
                     .OrderBy(item => item.State.ParentCategoryId)
                     .ThenBy(item => item.State.Position))
        {
            ulong? parentId = null;
            string? parentName = null;
            Guid? parentStepId = null;
            if (!item.Selection.RecreateUncategorized && item.State.ParentCategoryId is { } sourceParent)
            {
                var sourceCategoryIndex = -1;
                for (var index = 0; index < request.Backup.Channels.Length; index++)
                {
                    var candidate = request.Backup.Channels[index];
                    if (candidate.Id == sourceParent && candidate.Kind == ChannelKind.Category)
                    {
                        sourceCategoryIndex = index;
                        break;
                    }
                }
                if (indexed.TryGetValue(sourceCategoryIndex, out var categorySelection))
                {
                    if (categorySelection.ExistingCategoryId is { } existingId)
                    {
                        var existing = server.Channels.First(channel =>
                            channel.Id == existingId && channel.Kind == ChannelKind.Category);
                        parentId = existing.Id;
                        parentName = existing.Name;
                    }
                    else if (categoryCreateSteps.TryGetValue(sourceCategoryIndex, out var categoryStep))
                    {
                        parentStepId = categoryStep.StepId;
                        parentName = categoryStep.After!.Name;
                    }
                }
                else
                {
                    errors.Add(
                        $"{item.State.Name} has no resolved category mapping. Choose Uncategorized or map its category.");
                    continue;
                }
            }

            var after = MapState(item.State, item.Selection, parentId, parentName, request, roleMappings);
            var step = CreateStep(order++, after, $"Create replacement {after.Kind} channel {after.Name}") with
            {
                ParentResultStepId = parentStepId
            };
            steps.Add(step);
            channelCreateSteps.Add((step, after));
        }

        if (errors.Count > 0)
        {
            return ChannelPlanResult.Failure(errors);
        }

        var created = categoryCreateSteps.Values
            .Select(step => (Step: step, State: step.After!))
            .Concat(channelCreateSteps)
            .OrderBy(item => item.State.Kind == ChannelKind.Category ? 0 : 1)
            .ThenBy(item => item.State.Position)
            .ToArray();
        if (created.Length > 1)
        {
            steps.Add(
                new OperationStep(
                    Guid.NewGuid(),
                    order,
                    OperationStepKind.ReorderChannel,
                    "Apply final replacement resource positions",
                    new OperationTarget(
                        server.Id,
                        server.Name,
                        OperationTargetKind.Server,
                        null,
                        string.Empty),
                    null,
                    null,
                    null,
                    false,
                    null)
                {
                    BatchAfterStates = created.Select(item => item.State).ToImmutableArray(),
                    BatchResultStepIds = created.Select(item => item.Step.StepId).ToImmutableArray()
                });
        }

        var channelCount = channelCreateSteps.Count;
        var includeOverwrites = request.IncludePermissionOverwrites
            && selectedStates.Any(item => !item.State.PermissionOverwrites.IsDefaultOrEmpty);
        var mappedCategoryIds = selectedStates
            .Where(item => item.Selection.ExistingCategoryId is not null)
            .Select(item => item.Selection.ExistingCategoryId!.Value)
            .Distinct()
            .ToImmutableArray();
        var exactBefore = mappedCategoryIds
            .Select(id => server.Channels.First(channel => channel.Id == id))
            .Select(channel => ChannelOperationPlanner.ToState(channel, server))
            .ToImmutableArray();
        var confirmation = channelCount >= 3
            ? new OperationConfirmationRequirement(
                OperationConfirmationKind.TypedText,
                "Type the exact replacement channel count.",
                $"RECREATE {channelCount} CHANNELS")
            : new OperationConfirmationRequirement(
                OperationConfirmationKind.Explicit,
                "Confirm creation of replacement resources with new Discord IDs.",
                null);
        var requiredPermissions = includeOverwrites
            ? ImmutableArray.Create(PermissionBits.ManageChannels, PermissionBits.ManageRoles)
            : ImmutableArray.Create(PermissionBits.ManageChannels);
        var plan = new OperationPlan(
            Guid.NewGuid(),
            Guid.NewGuid(),
            request.BotProfileId,
            server.Id,
            server.Name,
            snapshot.LastAcceptedSequence,
            DateTimeOffset.UtcNow,
            ChannelOperationType.RecreateStructure,
            $"Recreate {channelCount} replacement channel{Plural(channelCount)}",
            mappedCategoryIds,
            exactBefore,
            created.Select(item => item.State).ToImmutableArray(),
            requiredPermissions,
            BuildPreconditions(server, mappedCategoryIds, includeOverwrites),
            includeOverwrites || created.Length > 10
                ? OperationRiskLevel.High
                : OperationRiskLevel.Moderate,
            steps.ToImmutableArray(),
            steps.Count,
            confirmation,
            request.CompensationPolicy == RecreateCompensationPolicy.AttemptCleanupCreatedResources
                ? OperationCompensationCapability.BestEffort
                : OperationCompensationCapability.None,
            SanitizeReason(request.AuditReason))
        {
            SourceBackupIdentifier = request.BackupIdentifier,
            RecreateCompensationPolicy = request.CompensationPolicy,
            CompatibilityWarnings = BuildCompatibilityWarnings(
                request,
                selectedStates.Select(item => item.Index).ToHashSet(),
                roleMappings)
        };
        return ChannelPlanResult.Success(plan);
    }

    public OperationPreview BuildPreview(OperationPlan plan, string botDisplayName)
    {
        var preview = channelPlanner.BuildPreview(plan, botDisplayName);
        var mappedOverwrites = plan.Steps
            .Where(step => step.Kind is OperationStepKind.CreateCategory
                or OperationStepKind.CreateTextChannel
                or OperationStepKind.CreateVoiceChannel)
            .SelectMany(step => step.After?.PermissionOverwrites ?? [])
            .Select(overwrite =>
                new PermissionOverwriteChange(
                    overwrite.TargetId,
                    overwrite.TargetType,
                    overwrite.TargetDisplayName,
                    null,
                    overwrite,
                    [],
                    []))
            .ToImmutableArray();
        var reusedCategories = plan.ExactBeforeState
            .Where(state => state.Kind == ChannelKind.Category)
            .Select(state => $"Existing category reused: {state.Name} ({state.Id}).")
            .ToImmutableArray();
        return preview with
        {
            PermissionOverwriteChanges =
                preview.PermissionOverwriteChanges.AddRange(mappedOverwrites),
            Consequences =
            [
                .. preview.Consequences,
                $"Backup source: {plan.SourceBackupIdentifier}",
                "This creates replacement resources with new Discord IDs.",
                .. reusedCategories,
                "Messages, threads, pins, links, webhooks, invites, history, and external references are not recovered.",
                plan.RecreateCompensationPolicy switch
                {
                    RecreateCompensationPolicy.AttemptCleanupCreatedResources =>
                        "If a later step fails, safe cleanup of newly created replacement resources may be attempted.",
                    RecreateCompensationPolicy.StopForManualReview =>
                        "A partial failure stops for manual review; successful replacement resources are kept.",
                    _ => "Successfully created replacement resources are kept after partial failure."
                }
            ]
        };
    }

    private static ImmutableArray<string> BuildCompatibilityWarnings(
        RecreateStructureRequest request,
        HashSet<int> selectedIndexes,
        IReadOnlyDictionary<(ulong Id, PermissionTargetKind Kind), RoleMapping> mappings)
    {
        var warnings = ImmutableArray.CreateBuilder<string>();
        var skippedUnsupported = request.Backup.Channels
            .Select((channel, index) => (channel, index))
            .Count(item =>
                !selectedIndexes.Contains(item.index)
                && item.channel.Kind is not (
                    ChannelKind.Category or ChannelKind.Text or ChannelKind.Voice));
        if (skippedUnsupported > 0)
        {
            warnings.Add(
                $"{skippedUnsupported} unsupported backup resource(s) are excluded from this plan.");
        }

        if (!request.IncludePermissionOverwrites
            && request.Backup.Channels.Any(channel => !channel.PermissionOverwrites.IsDefaultOrEmpty))
        {
            warnings.Add("Permission overwrites are excluded by explicit user choice.");
        }
        else if (request.IncludePermissionOverwrites)
        {
            foreach (var mapping in mappings.Values.OrderBy(item => item.OriginalDisplayName))
            {
                warnings.Add(
                    mapping.Choice == RoleMappingChoice.Skip
                        ? $"Overwrite target skipped: {mapping.OriginalDisplayName} ({mapping.OriginalTargetId})."
                        : $"Overwrite mapped: {mapping.OriginalDisplayName} ({mapping.OriginalTargetId}) → "
                          + $"{mapping.TargetDisplayName} ({mapping.TargetId}).");
            }
        }

        return warnings.ToImmutable();
    }

    private static ChannelOperationStateSnapshot MapState(
        ChannelOperationStateSnapshot source,
        RecreateResourceSelection selection,
        ulong? parentId,
        string? parentName,
        RecreateStructureRequest request,
        IReadOnlyDictionary<(ulong Id, PermissionTargetKind Kind), RoleMapping> mappings)
    {
        var overwrites = request.IncludePermissionOverwrites
            ? source.PermissionOverwrites
                .Select(overwrite => MapOverwrite(overwrite, mappings))
                .Where(overwrite => overwrite is not null)
                .Select(overwrite => overwrite!)
                .ToImmutableArray()
            : ImmutableArray<ChannelPermissionOverwriteSnapshot>.Empty;
        return source with
        {
            Id = null,
            Name = selection.ProposedName.Trim(),
            ParentCategoryId = parentId,
            ParentCategoryName = parentName,
            PermissionOverwrites = overwrites
        };
    }

    private static ChannelPermissionOverwriteSnapshot? MapOverwrite(
        ChannelPermissionOverwriteSnapshot overwrite,
        IReadOnlyDictionary<(ulong Id, PermissionTargetKind Kind), RoleMapping> mappings)
    {
        if (!mappings.TryGetValue((overwrite.TargetId, overwrite.TargetType), out var mapping)
            || !mapping.IsResolved
            || mapping.Choice == RoleMappingChoice.Skip
            || mapping.TargetId is null)
        {
            return null;
        }

        return overwrite with
        {
            TargetId = mapping.TargetId.Value,
            TargetDisplayName = mapping.TargetDisplayName ?? mapping.TargetId.Value.ToString(CultureInfo.InvariantCulture)
        };
    }

    private static OperationStep CreateStep(
        int order,
        ChannelOperationStateSnapshot after,
        string description)
    {
        var kind = after.Kind switch
        {
            ChannelKind.Category => OperationStepKind.CreateCategory,
            ChannelKind.Text => OperationStepKind.CreateTextChannel,
            ChannelKind.Voice => OperationStepKind.CreateVoiceChannel,
            _ => throw new InvalidOperationException("Unsupported replacement channel type.")
        };
        return new OperationStep(
            Guid.NewGuid(),
            order,
            kind,
            description,
            new OperationTarget(
                0,
                after.Name,
                after.Kind == ChannelKind.Category
                    ? OperationTargetKind.Category
                    : OperationTargetKind.Channel,
                after.ParentCategoryId,
                ChannelOperationPlanner.Fingerprint(after)),
            null,
            after,
            null,
            false,
            new OperationCompensation(
                OperationCompensationCapability.BestEffort,
                OperationStepKind.DeleteChannel,
                null,
                null,
                null,
                "Delete this newly created replacement resource if the selected compensation policy permits it."));
    }

    private static ImmutableArray<OperationPrecondition> BuildPreconditions(
        ServerReadModel server,
        ImmutableArray<ulong> targetIds,
        bool requiresManageRoles) =>
    [
        new(OperationPreconditionKind.BotConnected, "The selected bot remains connected.", true, null),
        new(OperationPreconditionKind.ServerAvailable, "The target server remains available.", true, null),
        .. targetIds.Select(id =>
            new OperationPrecondition(
                OperationPreconditionKind.TargetExists,
                $"Mapped category {id} still exists.",
                server.Channels.Any(channel => channel.Id == id),
                "MAPPED_CATEGORY_MISSING")),
        new(
            OperationPreconditionKind.RequiredPermission,
            "The bot still has Manage Channels.",
            true,
            null),
        .. requiresManageRoles
            ? new[]
            {
                new OperationPrecondition(
                    OperationPreconditionKind.RequiredPermission,
                    "The bot still has Manage Roles for mapped overwrites.",
                    true,
                    null)
            }
            : []
    ];

    private static string? SanitizeReason(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var safe = new string(value.Where(character => !char.IsControl(character)).ToArray()).Trim();
        return safe.Length <= 200 ? safe : safe[..200];
    }

    private static string Plural(int count) => count == 1 ? string.Empty : "s";

    private sealed class KindNameComparer :
        IEqualityComparer<(ChannelKind Kind, string Name)>
    {
        public bool Equals(
            (ChannelKind Kind, string Name) x,
            (ChannelKind Kind, string Name) y) =>
            x.Kind == y.Kind
            && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((ChannelKind Kind, string Name) obj) =>
            HashCode.Combine(obj.Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name));
    }
}

public sealed class OperationRecoveryService(
    IOperationHistoryRepository history,
    IOperationHistoryQueryRepository queries,
    IManualReconciliationRepository decisions,
    IBotExplorerService explorer,
    IOperationReconciliationService reconciliation) : IOperationRecoveryService
{
    public async Task<ImmutableArray<RecoveryAssessment>> InspectInterruptedAsync(
        CancellationToken cancellationToken)
    {
        var interrupted = await history.GetInterruptedAsync(cancellationToken).ConfigureAwait(false);
        var assessments = ImmutableArray.CreateBuilder<RecoveryAssessment>();
        foreach (var entry in interrupted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OperationPlan? plan = null;
            ChannelOperationResult? persistedResult = null;
            RecoveryClassification classification;
            string summary;
            var reconciledSteps = ImmutableArray<OperationStepResult>.Empty;
            try
            {
                plan = JsonSerializer.Deserialize<OperationPlan>(entry.PlanJson);
                persistedResult = entry.ResultJson is null
                    ? null
                    : JsonSerializer.Deserialize<ChannelOperationResult>(entry.ResultJson);
                if (plan is null || plan.SchemaVersion > 2)
                {
                    classification = RecoveryClassification.UnsupportedPlanSchema;
                    summary = "The interrupted operation uses an unsupported persisted-plan schema.";
                }
                else
                {
                    var assessment = await ReconcilePlanAsync(
                            plan,
                            persistedResult,
                            cancellationToken)
                        .ConfigureAwait(false);
                    classification = assessment.Classification;
                    summary = assessment.SafeSummary;
                    reconciledSteps = assessment.ReconciledSteps;
                }
            }
            catch (Exception exception) when (
                exception is JsonException or NotSupportedException)
            {
                classification = RecoveryClassification.UnsupportedPlanSchema;
                summary = "The interrupted operation plan is corrupt or unsupported.";
            }

            var recoveryState = classification == RecoveryClassification.CompletedAfterReconciliation
                ? ChannelOperationState.Completed
                : ChannelOperationState.ReconciliationRequired;
            var reconciliationStatus = classification switch
            {
                RecoveryClassification.CompletedAfterReconciliation =>
                    OperationReconciliationStatus.ConfirmedApplied,
                RecoveryClassification.NotStarted =>
                    OperationReconciliationStatus.ConfirmedNotApplied,
                RecoveryClassification.UnableToInspect =>
                    OperationReconciliationStatus.TimedOut,
                _ => OperationReconciliationStatus.ManualReviewRequired
            };
            var now = DateTimeOffset.UtcNow;
            var recoveredResult = plan is null
                ? null
                : new ChannelOperationResult(
                    entry.OperationId,
                    entry.CorrelationId,
                    recoveryState,
                    entry.StartedAt ?? entry.CreatedAt,
                    now,
                    reconciledSteps,
                    reconciledSteps.Count(step => step.Succeeded),
                    reconciledSteps.Count(step => !step.Succeeded && !step.WasCancelled),
                    0,
                    recoveryState == ChannelOperationState.Completed
                        ? null
                        : RecoveryFailure(summary),
                    new OperationReconciliationResult(
                        reconciliationStatus,
                        summary,
                        reconciledSteps
                            .Where(step => step.Succeeded && step.ResultResourceId is not null)
                            .Select(step => step.ResultResourceId!.Value)
                            .ToImmutableArray(),
                        now),
                    entry.BackupIdentifier,
                    plan.CompensationCapability,
                    "Automatic compensation was not attempted during startup recovery.");
            var updated = entry with
            {
                State = recoveryState,
                FinishedAt = now,
                CompletedCount = recoveredResult?.CompletedCount ?? entry.CompletedCount,
                FailedCount = recoveredResult?.FailedCount ?? entry.FailedCount,
                SafeErrorCodes = recoveryState == ChannelOperationState.Completed
                    ? null
                    : "STARTUP_RECOVERY_REQUIRED",
                DurationMilliseconds = Math.Max(
                    entry.DurationMilliseconds,
                    (long)(now - (entry.StartedAt ?? entry.CreatedAt)).TotalMilliseconds),
                ResultJson = recoveredResult is null
                    ? entry.ResultJson
                    : JsonSerializer.Serialize(recoveredResult)
            };
            await history.UpdateAsync(updated, cancellationToken).ConfigureAwait(false);
            await queries.AddTransitionAsync(
                    new OperationStateTransition(
                        0,
                        entry.OperationId,
                        recoveryState,
                        now,
                        "STARTUP_INTERRUPTED",
                        summary),
                    cancellationToken)
                .ConfigureAwait(false);
            assessments.Add(
                new RecoveryAssessment(
                    entry.OperationId,
                    entry.CorrelationId,
                    classification,
                    summary,
                    reconciledSteps,
                    recoveryState != ChannelOperationState.Completed,
                    plan is not null && recoveryState != ChannelOperationState.Completed));
        }

        return assessments.ToImmutable();
    }

    public Task RecordDecisionAsync(
        ManualReconciliationDecision decision,
        CancellationToken cancellationToken) =>
        decisions.AddAsync(decision, cancellationToken);

    private async Task<RecoveryAssessment> ReconcilePlanAsync(
        OperationPlan plan,
        ChannelOperationResult? persistedResult,
        CancellationToken cancellationToken)
    {
        var knownResults = persistedResult?.StepResults
            .ToDictionary(result => result.StepId)
            ?? new Dictionary<Guid, OperationStepResult>();
        var reconciled = ImmutableArray.CreateBuilder<OperationStepResult>();
        var snapshot = explorer.GetSnapshot(plan.BotProfileId);
        var canInspect = snapshot.State == ExplorerCacheState.Ready
            && snapshot.Servers.Any(server =>
                server.Id == plan.ServerId
                && server.Availability == ServerAvailability.Available);
        if (!canInspect)
        {
            reconciled.AddRange(knownResults.Values.OrderBy(result => result.Order));
            return new RecoveryAssessment(
                plan.OperationId,
                plan.CorrelationId,
                RecoveryClassification.UnableToInspect,
                "The target server is not currently available to the selected bot. No Discord mutation was resumed.",
                reconciled.ToImmutable(),
                true,
                true);
        }

        var ambiguous = false;
        foreach (var step in plan.Steps.OrderBy(step => step.Order))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (knownResults.TryGetValue(step.StepId, out var known) && known.Succeeded)
            {
                reconciled.Add(known);
                continue;
            }

            OperationReconciliationResult outcome;
            try
            {
                outcome = await reconciliation
                    .ReconcileAsync(plan, step, UncertainOutcome(), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException
                || !cancellationToken.IsCancellationRequested)
            {
                _ = exception;
                ambiguous = true;
                reconciled.Add(RecoveryStep(step, false, null, "RECOVERY_INSPECTION_FAILED"));
                continue;
            }

            switch (outcome.Status)
            {
                case OperationReconciliationStatus.ConfirmedApplied:
                    reconciled.Add(
                        RecoveryStep(
                            step,
                            true,
                            outcome.MatchingResourceIds.Length == 1
                                ? outcome.MatchingResourceIds[0]
                                : null,
                            null));
                    break;
                case OperationReconciliationStatus.ConfirmedNotApplied:
                    reconciled.Add(RecoveryStep(step, false, null, "RECOVERY_CONFIRMED_NOT_APPLIED"));
                    break;
                default:
                    ambiguous = true;
                    reconciled.Add(RecoveryStep(step, false, null, "RECOVERY_AMBIGUOUS"));
                    break;
            }
        }

        var applied = reconciled.Count(result => result.Succeeded);
        var classification = ambiguous
            ? RecoveryClassification.ManualReviewRequired
            : applied == plan.Steps.Length
                ? RecoveryClassification.CompletedAfterReconciliation
                : applied == 0
                    ? RecoveryClassification.NotStarted
                    : RecoveryClassification.PartiallyCompleted;
        var summary = classification switch
        {
            RecoveryClassification.CompletedAfterReconciliation =>
                "Every planned step was confirmed from current Discord state. No mutation was replayed.",
            RecoveryClassification.PartiallyCompleted =>
                "Only part of the plan was confirmed from current Discord state. No mutation was replayed.",
            RecoveryClassification.NotStarted =>
                "No planned step was found in current Discord state. The operation was not resumed.",
            _ =>
                "One or more step outcomes remain ambiguous. Manual review is required and no mutation was replayed."
        };
        return new RecoveryAssessment(
            plan.OperationId,
            plan.CorrelationId,
            classification,
            summary,
            reconciled.ToImmutable(),
            classification != RecoveryClassification.CompletedAfterReconciliation,
            classification != RecoveryClassification.CompletedAfterReconciliation);
    }

    private static OperationStepResult RecoveryStep(
        OperationStep step,
        bool succeeded,
        ulong? resourceId,
        string? safeCode)
    {
        var now = DateTimeOffset.UtcNow;
        return new OperationStepResult(
            step.StepId,
            step.Order,
            step.Description,
            succeeded,
            false,
            resourceId,
            now,
            now,
            0,
            safeCode is null
                ? null
                : new OperationFailure(
                    OperationFailureKind.UncertainOutcome,
                    safeCode,
                    "Startup recovery inspected this step without replaying it.",
                    null,
                    false,
                    OperationOutcomeCertainty.KnownFailed),
            false,
            false);
    }

    private static ChannelWriteOutcome UncertainOutcome() =>
        new(
            false,
            null,
            RecoveryFailure("The process ended before this step reached a durable terminal result."),
            OperationOutcomeCertainty.Uncertain);

    private static OperationFailure RecoveryFailure(string message) =>
        new(
            OperationFailureKind.UncertainOutcome,
            "STARTUP_RECOVERY_REQUIRED",
            message,
            null,
            false,
            OperationOutcomeCertainty.Uncertain);
}

public sealed class OperationExportService(
    IOperationHistoryQueryRepository history,
    IBackupCatalogService backups) : IOperationExportService
{
    private static readonly ImmutableArray<string> Excluded =
    [
        "Tokens and protected credentials",
        "Authorization headers and raw Discord payloads",
        "Messages, DMs, voice content, and member directories",
        "Sensitive exception messages, stack traces, and Windows user paths"
    ];

    public async Task<int> ExportHistoryJsonAsync(
        Stream destination,
        OperationHistoryQuery query,
        CancellationToken cancellationToken)
    {
        var rows = await ReadHistoryAsync(query, cancellationToken).ConfigureAwait(false);
        var export = new SafeOperationExport(
            1,
            DateTimeOffset.UtcNow,
            Excluded,
            rows,
            []);
        await JsonSerializer.SerializeAsync(
                destination,
                export,
                OperationalJson.Indented,
                cancellationToken)
            .ConfigureAwait(false);
        return rows.Length;
    }

    public async Task<int> ExportHistoryCsvAsync(
        Stream destination,
        OperationHistoryQuery query,
        CancellationToken cancellationToken)
    {
        var rows = await ReadHistoryAsync(query, cancellationToken).ConfigureAwait(false);
        await using var writer = new StreamWriter(
            destination,
            new UTF8Encoding(false),
            4096,
            leaveOpen: true);
        await writer.WriteLineAsync(
            "OperationId,CorrelationId,Title,OperationType,BotProfileId,ServerId,ServerName,Risk,State,CreatedAt,StartedAt,FinishedAt,DurationMilliseconds,AffectedResources,Completed,Failed,Cancelled,BackupIdentifier,ReconciliationStatus,SafeErrorCodes,AuditReason")
            .ConfigureAwait(false);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = new object?[]
            {
                row.OperationId,
                row.CorrelationId,
                row.Title,
                row.OperationType,
                row.BotProfileId,
                row.ServerId,
                row.ServerName,
                row.Risk,
                row.State,
                row.CreatedAt,
                row.StartedAt,
                row.FinishedAt,
                row.DurationMilliseconds,
                row.AffectedResourceCount,
                row.CompletedCount,
                row.FailedCount,
                row.CancelledCount,
                row.BackupIdentifier,
                row.ReconciliationStatus,
                row.SafeErrorCodes,
                row.AuditReason
            };
            await writer.WriteLineAsync(string.Join(",", values.Select(Csv))).ConfigureAwait(false);
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        return rows.Length;
    }

    public async Task<int> ExportBackupMetadataJsonAsync(
        Stream destination,
        BackupQuery query,
        CancellationToken cancellationToken)
    {
        var rows = ImmutableArray.CreateBuilder<SafeBackupExportRow>();
        var pageNumber = 1;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await backups.QueryAsync(
                    query with { PageNumber = pageNumber, PageSize = 100 },
                    cancellationToken)
                .ConfigureAwait(false);
            rows.AddRange(
                page.Items.Select(item =>
                    new SafeBackupExportRow(
                        item.BackupIdentifier,
                        item.CorrelationId,
                        item.OperationId,
                        item.BotProfileId,
                        item.ServerId,
                        item.ServerName,
                        item.CreatedAt,
                        item.SourceOperationType,
                        item.CategoryCount,
                        item.ChannelCount,
                        item.PermissionOverwriteCount,
                        item.SchemaVersion,
                        item.IsPinned,
                        item.SizeBytes)));
            if (pageNumber >= page.TotalPages)
            {
                break;
            }

            pageNumber++;
        }

        var export = new SafeOperationExport(1, DateTimeOffset.UtcNow, Excluded, [], rows.ToImmutable());
        await JsonSerializer.SerializeAsync(
                destination,
                export,
                OperationalJson.Indented,
                cancellationToken)
            .ConfigureAwait(false);
        return rows.Count;
    }

    private async Task<ImmutableArray<SafeOperationExportRow>> ReadHistoryAsync(
        OperationHistoryQuery query,
        CancellationToken cancellationToken)
    {
        var rows = ImmutableArray.CreateBuilder<SafeOperationExportRow>();
        var pageNumber = 1;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await history.QueryAsync(
                    query with { PageNumber = pageNumber, PageSize = 100 },
                    cancellationToken)
                .ConfigureAwait(false);
            rows.AddRange(
                page.Items.Select(entry =>
                    new SafeOperationExportRow(
                        entry.OperationId,
                        entry.CorrelationId,
                        entry.Title,
                        entry.OperationType,
                        entry.BotProfileId,
                        entry.ServerId,
                        entry.ServerName,
                        entry.RiskLevel,
                        entry.State,
                        entry.CreatedAt,
                        entry.StartedAt,
                        entry.FinishedAt,
                        entry.DurationMilliseconds,
                        entry.AffectedResourceCount,
                        entry.CompletedCount,
                        entry.FailedCount,
                        entry.CancelledCount,
                        entry.BackupIdentifier,
                        entry.ReconciliationStatus,
                        entry.SafeErrorCodes,
                        entry.AuditReason)));
            if (pageNumber >= page.TotalPages)
            {
                break;
            }

            pageNumber++;
        }

        return rows.ToImmutable();
    }

    private static string Csv(object? value)
    {
        var text = value switch
        {
            null => string.Empty,
            DateTimeOffset timestamp => timestamp.ToString("O"),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        };
        return $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}

internal static class OperationalJson
{
    internal static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true
    };
}
