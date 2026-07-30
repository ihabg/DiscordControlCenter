using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DiscordControlCenter.Application.Explorer;
using DiscordControlCenter.Core.Explorer;
using DiscordControlCenter.Core.Operations;

namespace DiscordControlCenter.Application.Operations;

public sealed class ChannelOperationPlanner(IBotExplorerService explorer) : IChannelOperationPlanner
{
    private const ulong AddReactionsRaw = 1UL << 6;
    private const ulong SendMessagesRaw = 1UL << 11;
    private const ulong ConnectRaw = 1UL << 20;
    private const ulong SpeakRaw = 1UL << 21;

    public ChannelPlanResult PlanCreate(CreateChannelsRequest request)
    {
        var context = GetContext(request.BotProfileId, request.ServerId);
        if (!context.IsSuccess)
        {
            return context.Failure!;
        }

        var server = context.Server!;
        var errors = ValidateCreationItems(server, request.Channels);
        if (errors.Count > 0)
        {
            return ChannelPlanResult.Failure(errors);
        }

        var steps = request.Channels
            .Select(
                (item, index) =>
                {
                    var basePosition = item.Position
                        ?? (item.Kind == ChannelKind.Category
                            ? NextCategoryPosition(server)
                            : NextPosition(server, item.ParentCategoryId));
                    var after = new ChannelOperationStateSnapshot(
                        null,
                        NormalizeName(item.Name),
                        item.Kind,
                        basePosition + index,
                        item.ParentCategoryId,
                        GetChannel(server, item.ParentCategoryId)?.Name,
                        item.Topic?.Trim(),
                        item.IsNsfw,
                        item.SlowModeSeconds,
                        null,
                        item.Bitrate,
                        item.UserLimit,
                        null,
                        item.SynchronizeWithParent
                            ? GetChannel(server, item.ParentCategoryId)?.PermissionOverwrites
                                .Select(overwrite => ToOverwrite(overwrite, server))
                                .ToImmutableArray()
                                ?? ImmutableArray<ChannelPermissionOverwriteSnapshot>.Empty
                            : ImmutableArray<ChannelPermissionOverwriteSnapshot>.Empty);
                    var kind = item.Kind switch
                    {
                        ChannelKind.Category => OperationStepKind.CreateCategory,
                        ChannelKind.Text => OperationStepKind.CreateTextChannel,
                        ChannelKind.Voice => OperationStepKind.CreateVoiceChannel,
                        _ => throw new InvalidOperationException("Unsupported creation type.")
                    };
                    return new OperationStep(
                        Guid.NewGuid(),
                        index + 1,
                        kind,
                        $"Create {item.Kind.ToString().ToLowerInvariant()} channel “{after.Name}”",
                        new OperationTarget(
                            0,
                            after.Name,
                            item.Kind == ChannelKind.Category
                                ? OperationTargetKind.Category
                                : OperationTargetKind.Channel,
                            item.ParentCategoryId,
                            Fingerprint(after)),
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
                            "The newly created channel can be deleted if a later step fails."));
                })
            .ToImmutableArray();
        var parentStates = request.Channels
            .Where(item => item.ParentCategoryId is not null)
            .Select(item => GetChannel(server, item.ParentCategoryId))
            .OfType<ChannelReadModel>()
            .DistinctBy(channel => channel.Id)
            .Select(channel => ToState(channel, server))
            .ToImmutableArray();
        var risk = steps.Length == 1 ? OperationRiskLevel.Low : OperationRiskLevel.Moderate;
        return ChannelPlanResult.Success(
            BuildPlan(
                request.BotProfileId,
                context.Snapshot!,
                server,
                request.Channels.Any(item => item.Kind == ChannelKind.Category)
                    ? ChannelOperationType.CreateCategory
                    : request.Channels.All(item => item.Kind == ChannelKind.Voice)
                        ? ChannelOperationType.CreateVoiceChannels
                        : ChannelOperationType.CreateTextChannels,
                steps.Length == 1 ? $"Create {steps[0].Target.DisplayName}" : $"Create {steps.Length} channels",
                parentStates,
                steps.Select(step => step.After!).ToImmutableArray(),
                request.Channels.Any(item => item.SynchronizeWithParent)
                    ? [PermissionBits.ManageChannels, PermissionBits.ManageRoles]
                    : [PermissionBits.ManageChannels],
                risk,
                steps,
                ExplicitConfirmation(),
                OperationCompensationCapability.BestEffort,
                request.AuditReason));
    }

    public ChannelPlanResult PlanEdit(EditChannelRequest request)
    {
        var context = GetContext(request.BotProfileId, request.ServerId);
        if (!context.IsSuccess)
        {
            return context.Failure!;
        }

        var server = context.Server!;
        var channel = GetChannel(server, request.ChannelId);
        if (channel is null)
        {
            return ChannelPlanResult.Failure("The selected channel no longer exists.");
        }

        if (!IsEditable(channel.Kind))
        {
            return ChannelPlanResult.Failure("This channel type is not supported for editing in Phase 4A.");
        }

        var before = ToState(channel, server);
        var after = before with
        {
            Name = request.Name.IsSpecified ? NormalizeName(request.Name.Value ?? string.Empty) : before.Name,
            ParentCategoryId = request.ParentCategoryId.IsSpecified
                ? request.ParentCategoryId.Value
                : before.ParentCategoryId,
            ParentCategoryName = request.ParentCategoryId.IsSpecified
                ? GetChannel(server, request.ParentCategoryId.Value)?.Name
                : before.ParentCategoryName,
            Position = request.Position.IsSpecified ? request.Position.Value : before.Position,
            Topic = request.Topic.IsSpecified ? NormalizeOptionalText(request.Topic.Value, 1024) : before.Topic,
            IsNsfw = request.IsNsfw.IsSpecified ? request.IsNsfw.Value : before.IsNsfw,
            SlowModeSeconds = request.SlowModeSeconds.IsSpecified
                ? request.SlowModeSeconds.Value
                : before.SlowModeSeconds,
            DefaultAutoArchiveMinutes = request.DefaultAutoArchiveMinutes.IsSpecified
                ? request.DefaultAutoArchiveMinutes.Value
                : before.DefaultAutoArchiveMinutes,
            Bitrate = request.Bitrate.IsSpecified ? request.Bitrate.Value : before.Bitrate,
            UserLimit = request.UserLimit.IsSpecified ? request.UserLimit.Value : before.UserLimit,
            RegionOverride = request.RegionOverride.IsSpecified
                ? NormalizeOptionalText(request.RegionOverride.Value, 64)
                : before.RegionOverride
        };
        var errors = ValidateState(server, after, channel.Id);
        if (errors.Count > 0)
        {
            return ChannelPlanResult.Failure(errors);
        }

        if (before == after)
        {
            return ChannelPlanResult.Failure("No supported property would change.");
        }

        var step = ModifyStep(1, channel, before, after);
        var onlyLowRisk = before.Name == after.Name
            && before.ParentCategoryId == after.ParentCategoryId
            && before.Position == after.Position
            && before.IsNsfw == after.IsNsfw
            && before.Bitrate == after.Bitrate
            && before.UserLimit == after.UserLimit
            && before.RegionOverride == after.RegionOverride;
        return ChannelPlanResult.Success(
            BuildPlan(
                request.BotProfileId,
                context.Snapshot!,
                server,
                ChannelOperationType.EditChannel,
                $"Edit {channel.Name}",
                [before],
                [after],
                [PermissionBits.ManageChannels],
                onlyLowRisk ? OperationRiskLevel.Low : OperationRiskLevel.Moderate,
                [step],
                ExplicitConfirmation(),
                OperationCompensationCapability.ExactWhenTargetUnchanged,
                request.AuditReason));
    }

    public ChannelPlanResult PlanBulkRename(BulkRenameRequest request)
    {
        var context = GetContext(request.BotProfileId, request.ServerId);
        if (!context.IsSuccess)
        {
            return context.Failure!;
        }

        var server = context.Server!;
        var channels = ResolveChannels(server, request.ChannelIds);
        if (channels.Length != request.ChannelIds.Distinct().Count())
        {
            return ChannelPlanResult.Failure("One or more selected channels no longer exist.");
        }

        if (channels.Length == 0)
        {
            return ChannelPlanResult.Failure("Select at least one channel to rename.");
        }

        var ordered = channels
            .OrderBy(channel => channel.CategoryId)
            .ThenBy(channel => channel.Position)
            .ThenBy(channel => channel.Id)
            .ToArray();
        var proposals = ordered
            .Select(
                (channel, index) =>
                {
                    var name = request.Mode switch
                    {
                        BulkRenameMode.ExactReplacement when ordered.Length == 1 => request.Value,
                        BulkRenameMode.Prefix => request.Value + channel.Name,
                        BulkRenameMode.Suffix => channel.Name + request.Value,
                        BulkRenameMode.FindAndReplace => channel.Name.Replace(
                            request.Value,
                            request.Replacement ?? string.Empty,
                            StringComparison.OrdinalIgnoreCase),
                        BulkRenameMode.SequentialNumbering =>
                            $"{request.Value}{(request.StartNumber + index).ToString(
                                request.ZeroPadding > 0
                                    ? new string('0', Math.Min(request.ZeroPadding, 8))
                                    : "0",
                                CultureInfo.InvariantCulture)}",
                        _ => string.Empty
                    };
                    return (Channel: channel, Name: NormalizeName(name));
                })
            .ToArray();
        var errors = proposals
            .Where(proposal => !IsValidName(proposal.Name))
            .Select(proposal => $"“{proposal.Name}” is not a valid channel name.")
            .ToList();
        if (request.Mode == BulkRenameMode.ExactReplacement && ordered.Length != 1)
        {
            errors.Add("Exact replacement can rename only one selected channel.");
        }

        if (proposals.Select(proposal => proposal.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != proposals.Length)
        {
            errors.Add("The rename would produce duplicate final names.");
        }

        if (errors.Count > 0)
        {
            return ChannelPlanResult.Failure(errors);
        }

        var beforeStates = proposals.Select(proposal => ToState(proposal.Channel, server)).ToArray();
        var afterStates = beforeStates
            .Select((state, index) => state with { Name = proposals[index].Name })
            .ToArray();
        if (beforeStates.SequenceEqual(afterStates))
        {
            return ChannelPlanResult.Failure("The requested rename would not change any channel.");
        }

        var steps = proposals
            .Select(
                (proposal, index) => ModifyStep(
                    index + 1,
                    proposal.Channel,
                    beforeStates[index],
                    afterStates[index]))
            .ToImmutableArray();
        return ChannelPlanResult.Success(
            BuildPlan(
                request.BotProfileId,
                context.Snapshot!,
                server,
                ChannelOperationType.BulkRename,
                $"Rename {steps.Length} channel{Plural(steps.Length)}",
                beforeStates.ToImmutableArray(),
                afterStates.ToImmutableArray(),
                [PermissionBits.ManageChannels],
                OperationRiskLevel.Moderate,
                steps,
                ExplicitConfirmation(),
                OperationCompensationCapability.ExactWhenTargetUnchanged,
                request.AuditReason));
    }

    public ChannelPlanResult PlanMove(MoveChannelsRequest request)
    {
        var context = GetContext(request.BotProfileId, request.ServerId);
        if (!context.IsSuccess)
        {
            return context.Failure!;
        }

        var server = context.Server!;
        var channels = ResolveChannels(server, request.ChannelIds)
            .OrderBy(channel => channel.Position)
            .ThenBy(channel => channel.Id)
            .ToArray();
        if (channels.Length == 0 || channels.Length != request.ChannelIds.Distinct().Count())
        {
            return ChannelPlanResult.Failure("Select existing channels to move.");
        }

        if (channels.Any(channel => channel.Kind == ChannelKind.Category))
        {
            return ChannelPlanResult.Failure("Categories cannot be moved inside another category.");
        }

        var category = GetChannel(server, request.DestinationCategoryId);
        if (request.DestinationCategoryId is not null && category?.Kind != ChannelKind.Category)
        {
            return ChannelPlanResult.Failure("The destination category no longer exists.");
        }

        var anchor = GetChannel(server, request.AnchorChannelId);
        if (request.Placement != MovePlacement.PreserveRelativeOrder
            && (anchor is null
                || anchor.Kind == ChannelKind.Category
                || anchor.CategoryId != request.DestinationCategoryId
                || channels.Any(channel => channel.Id == anchor.Id)))
        {
            return ChannelPlanResult.Failure(
                "Choose an unselected ordinary anchor in the destination category.");
        }

        var startingPosition = request.Placement switch
        {
            MovePlacement.BeforeChannel => Math.Max(0, anchor!.Position),
            MovePlacement.AfterChannel => anchor!.Position + 1,
            _ => server.Channels
                .Where(channel => channel.CategoryId == request.DestinationCategoryId
                    && channel.Kind != ChannelKind.Category)
                .Select(channel => channel.Position)
                .DefaultIfEmpty(-1)
                .Max() + 1
        };
        var beforeStates = channels.Select(channel => ToState(channel, server)).ToArray();
        var afterStates = beforeStates
            .Select(
                (state, index) => state with
                {
                    ParentCategoryId = request.DestinationCategoryId,
                    ParentCategoryName = category?.Name,
                    Position = startingPosition + index
                })
            .ToArray();
        if (beforeStates.SequenceEqual(afterStates))
        {
            return ChannelPlanResult.Failure("The requested move would not change parent or order.");
        }

        var isPureReorder = beforeStates.All(state =>
            state.ParentCategoryId == request.DestinationCategoryId);
        ImmutableArray<OperationStep> steps;
        if (isPureReorder)
        {
            var first = channels[0];
            steps =
            [
                new OperationStep(
                    Guid.NewGuid(),
                    1,
                    OperationStepKind.ReorderChannel,
                    $"Reorder {channels.Length} channel{Plural(channels.Length)}",
                    ToTarget(first, beforeStates[0]),
                    beforeStates[0],
                    afterStates[0],
                    null,
                    false,
                    new OperationCompensation(
                        OperationCompensationCapability.ExactWhenTargetUnchanged,
                        OperationStepKind.ReorderChannel,
                        null,
                        null,
                        null,
                        "Restore the exact captured channel positions."))
                {
                    BatchBeforeStates = beforeStates.ToImmutableArray(),
                    BatchAfterStates = afterStates.ToImmutableArray()
                }
            ];
        }
        else
        {
            steps = channels
                .Select(
                    (channel, index) => ModifyStep(
                        index + 1,
                        channel,
                        beforeStates[index],
                        afterStates[index],
                        OperationStepKind.MoveChannel))
                .ToImmutableArray();
        }
        return ChannelPlanResult.Success(
            BuildPlan(
                request.BotProfileId,
                context.Snapshot!,
                server,
                request.DestinationCategoryId == channels.First().CategoryId
                    ? ChannelOperationType.ReorderChannels
                    : ChannelOperationType.MoveChannels,
                $"Move or reorder {steps.Length} channel{Plural(steps.Length)}",
                beforeStates.ToImmutableArray(),
                afterStates.ToImmutableArray(),
                [PermissionBits.ManageChannels],
                OperationRiskLevel.Moderate,
                steps,
                ExplicitConfirmation(),
                OperationCompensationCapability.ExactWhenTargetUnchanged,
                request.AuditReason));
    }

    public ChannelPlanResult PlanClone(CloneChannelRequest request)
    {
        var context = GetContext(request.BotProfileId, request.ServerId);
        if (!context.IsSuccess)
        {
            return context.Failure!;
        }

        var server = context.Server!;
        var source = GetChannel(server, request.ChannelId);
        if (source is null)
        {
            return ChannelPlanResult.Failure("The source channel no longer exists.");
        }

        if (source.Kind is not (ChannelKind.Text or ChannelKind.Voice))
        {
            return ChannelPlanResult.Failure("Only ordinary text and voice channels can be cloned in Phase 4A.");
        }

        if (!IsValidName(request.NewName))
        {
            return ChannelPlanResult.Failure("Enter a valid clone name between 1 and 100 characters.");
        }

        if (request.ParentCategoryId is not null
            && GetChannel(server, request.ParentCategoryId)?.Kind != ChannelKind.Category)
        {
            return ChannelPlanResult.Failure("The selected clone destination category is unavailable.");
        }

        if (server.Channels.Any(channel =>
                channel.Kind == source.Kind
                && channel.CategoryId == request.ParentCategoryId
                && string.Equals(
                    channel.Name,
                    NormalizeName(request.NewName),
                    StringComparison.OrdinalIgnoreCase)))
        {
            return ChannelPlanResult.Failure(
                "A matching channel already exists in the clone destination.");
        }

        var sourceState = ToState(source, server);
        var after = sourceState with
        {
            Id = null,
            Name = NormalizeName(request.NewName),
            ParentCategoryId = request.ParentCategoryId,
            ParentCategoryName = GetChannel(server, request.ParentCategoryId)?.Name,
            Position = NextPosition(server, request.ParentCategoryId),
            PermissionOverwrites = request.CopyPermissionOverwrites
                ? sourceState.PermissionOverwrites
                : ImmutableArray<ChannelPermissionOverwriteSnapshot>.Empty
        };
        var step = CreateStep(1, after, $"Clone {source.Name} as “{after.Name}”");
        return ChannelPlanResult.Success(
            BuildPlan(
                request.BotProfileId,
                context.Snapshot!,
                server,
                ChannelOperationType.CloneChannel,
                $"Clone {source.Name}",
                [sourceState],
                [after],
                request.CopyPermissionOverwrites
                    ? [PermissionBits.ManageChannels, PermissionBits.ManageRoles]
                    : [PermissionBits.ManageChannels],
                request.CopyPermissionOverwrites
                    ? OperationRiskLevel.Moderate
                    : OperationRiskLevel.Low,
                [step],
                ExplicitConfirmation(),
                OperationCompensationCapability.BestEffort,
                request.AuditReason));
    }

    public ChannelPlanResult PlanCloneCategory(CloneCategoryRequest request)
    {
        var context = GetContext(request.BotProfileId, request.ServerId);
        if (!context.IsSuccess)
        {
            return context.Failure!;
        }

        var server = context.Server!;
        var sourceCategory = GetChannel(server, request.CategoryId);
        if (sourceCategory?.Kind != ChannelKind.Category)
        {
            return ChannelPlanResult.Failure("The source category no longer exists.");
        }

        if (!IsValidName(request.NewCategoryName))
        {
            return ChannelPlanResult.Failure("Enter a valid category name between 1 and 100 characters.");
        }

        if (server.Channels.Any(channel =>
                channel.Kind == ChannelKind.Category
                && string.Equals(
                    channel.Name,
                    NormalizeName(request.NewCategoryName),
                    StringComparison.OrdinalIgnoreCase)))
        {
            return ChannelPlanResult.Failure("A matching category already exists.");
        }

        var children = request.ChildChannelIds.Length == 0
            ? []
            : ResolveChannels(server, request.ChildChannelIds)
                .Where(channel => channel.CategoryId == sourceCategory.Id)
                .OrderBy(channel => channel.Position)
                .ThenBy(channel => channel.Id)
                .ToArray();
        if (children.Length != request.ChildChannelIds.Distinct().Count())
        {
            return ChannelPlanResult.Failure("Every selected child must still belong to the source category.");
        }

        var unsupported = children.Where(channel => channel.Kind is not (ChannelKind.Text or ChannelKind.Voice)).ToArray();
        if (unsupported.Length > 0)
        {
            return ChannelPlanResult.Failure(
                unsupported.Select(channel => $"“{channel.Name}” has unsupported type {channel.TypeName}."));
        }

        var sourceCategoryState = ToState(sourceCategory, server);
        var newCategory = sourceCategoryState with
        {
            Id = null,
            Name = NormalizeName(request.NewCategoryName),
            Position = NextCategoryPosition(server),
            PermissionOverwrites = request.CopyCategoryOverwrites
                ? sourceCategoryState.PermissionOverwrites
                : ImmutableArray<ChannelPermissionOverwriteSnapshot>.Empty
        };
        var categoryStep = CreateStep(1, newCategory, $"Create category “{newCategory.Name}”");
        var steps = new List<OperationStep> { categoryStep };
        var proposed = new List<ChannelOperationStateSnapshot> { newCategory };
        for (var index = 0; index < children.Length; index++)
        {
            var childState = ToState(children[index], server);
            var clone = childState with
            {
                Id = null,
                ParentCategoryId = null,
                ParentCategoryName = newCategory.Name,
                Position = index,
                PermissionOverwrites = request.SynchronizeChildren
                    ? newCategory.PermissionOverwrites
                    : request.CopyChildOverwrites
                        ? childState.PermissionOverwrites
                        : ImmutableArray<ChannelPermissionOverwriteSnapshot>.Empty
            };
            proposed.Add(clone);
            steps.Add(
                CreateStep(index + 2, clone, $"Clone child “{childState.Name}”") with
                {
                    ParentResultStepId = categoryStep.StepId
                });
        }

        var risk = children.Length >= 10 || request.CopyChildOverwrites
            ? OperationRiskLevel.High
            : OperationRiskLevel.Moderate;
        return ChannelPlanResult.Success(
            BuildPlan(
                request.BotProfileId,
                context.Snapshot!,
                server,
                ChannelOperationType.CloneCategoryStructure,
                $"Clone category {sourceCategory.Name} with {children.Length} child channel{Plural(children.Length)}",
                [sourceCategoryState, .. children.Select(channel => ToState(channel, server))],
                proposed.ToImmutableArray(),
                request.CopyCategoryOverwrites
                || request.CopyChildOverwrites
                || request.SynchronizeChildren
                    ? [PermissionBits.ManageChannels, PermissionBits.ManageRoles]
                    : [PermissionBits.ManageChannels],
                risk,
                steps.ToImmutableArray(),
                ExplicitConfirmation(),
                OperationCompensationCapability.BestEffort,
                request.AuditReason));
    }

    public ChannelPlanResult PlanLock(ChannelLockRequest request)
    {
        var context = GetContext(request.BotProfileId, request.ServerId);
        if (!context.IsSuccess)
        {
            return context.Failure!;
        }

        var server = context.Server!;
        var role = server.Roles.FirstOrDefault(item => item.Id == request.TargetRoleId);
        if (role is null)
        {
            return ChannelPlanResult.Failure("The selected overwrite role is unavailable.");
        }

        var channels = ResolveChannels(server, request.ChannelIds);
        if (channels.Length == 0 || channels.Length != request.ChannelIds.Distinct().Count())
        {
            return ChannelPlanResult.Failure("Select existing channels to lock or unlock.");
        }

        if (channels.Any(channel => channel.Kind is not (ChannelKind.Text or ChannelKind.Voice)))
        {
            return ChannelPlanResult.Failure("Lock presets support only ordinary text and voice channels.");
        }

        var steps = new List<OperationStep>();
        var beforeStates = new List<ChannelOperationStateSnapshot>();
        var afterStates = new List<ChannelOperationStateSnapshot>();
        foreach (var channel in channels.OrderBy(channel => channel.Position).ThenBy(channel => channel.Id))
        {
            var beforeState = ToState(channel, server);
            var existing = beforeState.PermissionOverwrites.FirstOrDefault(item =>
                item.TargetId == role.Id && item.TargetType == PermissionTargetKind.Role);
            var selectedMask = channel.Kind == ChannelKind.Text
                ? SendMessagesRaw | (request.IncludeSecondaryPermission ? AddReactionsRaw : 0)
                : ConnectRaw | (request.IncludeSecondaryPermission ? SpeakRaw : 0);
            var beforeOverwrite = existing
                ?? new ChannelPermissionOverwriteSnapshot(
                    role.Id,
                    PermissionTargetKind.Role,
                    role.Name,
                    0,
                    0);
            var afterOverwrite = request.IsUnlock
                ? beforeOverwrite with { DeniedRaw = beforeOverwrite.DeniedRaw & ~selectedMask }
                : beforeOverwrite with
                {
                    AllowedRaw = beforeOverwrite.AllowedRaw & ~selectedMask,
                    DeniedRaw = beforeOverwrite.DeniedRaw | selectedMask
                };
            if (beforeOverwrite == afterOverwrite)
            {
                continue;
            }

            var change = BuildOverwriteChange(existing, afterOverwrite, selectedMask);
            var afterOverwrites = beforeState.PermissionOverwrites
                .Where(item => !(item.TargetId == role.Id && item.TargetType == PermissionTargetKind.Role))
                .Append(afterOverwrite)
                .ToImmutableArray();
            var afterState = beforeState with { PermissionOverwrites = afterOverwrites };
            beforeStates.Add(beforeState);
            afterStates.Add(afterState);
            steps.Add(
                new OperationStep(
                    Guid.NewGuid(),
                    steps.Count + 1,
                    OperationStepKind.SetPermissionOverwrite,
                    $"{(request.IsUnlock ? "Unlock" : "Lock")} {channel.Name} for {role.Name}",
                    ToTarget(channel, beforeState),
                    beforeState,
                    afterState,
                    change,
                    false,
                    new OperationCompensation(
                        OperationCompensationCapability.ExactWhenTargetUnchanged,
                        existing is null
                            ? OperationStepKind.DeletePermissionOverwrite
                            : OperationStepKind.SetPermissionOverwrite,
                        channel.Id,
                        null,
                        existing,
                        "Restore the exact overwrite captured before this operation.")));
        }

        if (steps.Count == 0)
        {
            return ChannelPlanResult.Failure("The selected lock bits are already in the requested state.");
        }

        return ChannelPlanResult.Success(
            BuildPlan(
                request.BotProfileId,
                context.Snapshot!,
                server,
                request.IsUnlock ? ChannelOperationType.UnlockChannels : ChannelOperationType.LockChannels,
                $"{(request.IsUnlock ? "Unlock" : "Lock")} {steps.Count} channel{Plural(steps.Count)}",
                beforeStates.ToImmutableArray(),
                afterStates.ToImmutableArray(),
                [PermissionBits.ManageChannels, PermissionBits.ManageRoles],
                OperationRiskLevel.Moderate,
                steps.ToImmutableArray(),
                ExplicitConfirmation(),
                OperationCompensationCapability.ExactWhenTargetUnchanged,
                request.AuditReason));
    }

    public ChannelPlanResult PlanSynchronizePermissions(SynchronizePermissionsRequest request)
    {
        var context = GetContext(request.BotProfileId, request.ServerId);
        if (!context.IsSuccess)
        {
            return context.Failure!;
        }

        var server = context.Server!;
        var channels = ResolveChannels(server, request.ChannelIds);
        if (channels.Length == 0 || channels.Length != request.ChannelIds.Distinct().Count())
        {
            return ChannelPlanResult.Failure("Select existing child channels to synchronize.");
        }

        var steps = new List<OperationStep>();
        var beforeStates = new List<ChannelOperationStateSnapshot>();
        var afterStates = new List<ChannelOperationStateSnapshot>();
        foreach (var channel in channels.OrderBy(channel => channel.Position).ThenBy(channel => channel.Id))
        {
            if (channel.CategoryId is not ulong categoryId
                || GetChannel(server, categoryId) is not { Kind: ChannelKind.Category } category)
            {
                return ChannelPlanResult.Failure($"“{channel.Name}” has no available parent category.");
            }

            var beforeState = ToState(channel, server);
            var desired = ToState(category, server).PermissionOverwrites;
            if (OverwritesEqual(beforeState.PermissionOverwrites, desired))
            {
                continue;
            }

            beforeStates.Add(beforeState);
            afterStates.Add(beforeState with { PermissionOverwrites = desired });
            var existingByTarget = beforeState.PermissionOverwrites.ToDictionary(
                item => (item.TargetType, item.TargetId));
            var desiredByTarget = desired.ToDictionary(item => (item.TargetType, item.TargetId));
            foreach (var target in existingByTarget.Keys.Union(desiredByTarget.Keys))
            {
                existingByTarget.TryGetValue(target, out var beforeOverwrite);
                desiredByTarget.TryGetValue(target, out var afterOverwrite);
                if (beforeOverwrite == afterOverwrite)
                {
                    continue;
                }

                var change = BuildOverwriteChange(beforeOverwrite, afterOverwrite, ulong.MaxValue);
                var kind = afterOverwrite is null
                    ? OperationStepKind.DeletePermissionOverwrite
                    : OperationStepKind.SetPermissionOverwrite;
                steps.Add(
                    new OperationStep(
                        Guid.NewGuid(),
                        steps.Count + 1,
                        kind,
                        $"Synchronize {change.TargetDisplayName} overwrite on {channel.Name}",
                        ToTarget(channel, beforeState),
                        beforeState,
                        beforeState with { PermissionOverwrites = desired },
                        change,
                        false,
                        new OperationCompensation(
                            OperationCompensationCapability.ExactWhenTargetUnchanged,
                            beforeOverwrite is null
                                ? OperationStepKind.DeletePermissionOverwrite
                                : OperationStepKind.SetPermissionOverwrite,
                            channel.Id,
                            null,
                            beforeOverwrite,
                            "Restore the exact pre-synchronization overwrite.")));
            }
        }

        if (steps.Count == 0)
        {
            return ChannelPlanResult.Failure("The selected channels are already synchronized.");
        }

        return ChannelPlanResult.Success(
            BuildPlan(
                request.BotProfileId,
                context.Snapshot!,
                server,
                ChannelOperationType.SynchronizePermissions,
                $"Synchronize permissions on {beforeStates.Count} channel{Plural(beforeStates.Count)}",
                beforeStates.ToImmutableArray(),
                afterStates.ToImmutableArray(),
                [PermissionBits.ManageChannels, PermissionBits.ManageRoles],
                steps.Count > 10 ? OperationRiskLevel.High : OperationRiskLevel.Moderate,
                steps.ToImmutableArray(),
                ExplicitConfirmation(),
                OperationCompensationCapability.ExactWhenTargetUnchanged,
                request.AuditReason));
    }

    public ChannelPlanResult PlanDelete(DeleteChannelsRequest request)
    {
        var context = GetContext(request.BotProfileId, request.ServerId);
        if (!context.IsSuccess)
        {
            return context.Failure!;
        }

        var server = context.Server!;
        var selected = ResolveChannels(server, request.ChannelIds);
        if (selected.Length == 0 || selected.Length != request.ChannelIds.Distinct().Count())
        {
            return ChannelPlanResult.Failure("Select existing channels to delete.");
        }

        if (selected.Length > 1 && selected.Any(channel => channel.Kind == ChannelKind.Category))
        {
            return ChannelPlanResult.Failure(
                "Plan category deletion separately so child-channel semantics remain explicit.");
        }

        var expanded = new List<ChannelReadModel>();
        var affectedOnly = new List<ChannelReadModel>();
        var operationType = ChannelOperationType.DeleteChannels;
        if (selected is [{ Kind: ChannelKind.Category } category])
        {
            var children = server.Channels
                .Where(channel => channel.CategoryId == category.Id)
                .OrderBy(channel => channel.Position)
                .ThenBy(channel => channel.Id)
                .ToArray();
            if (request.DeleteCategoryOnly)
            {
                operationType = ChannelOperationType.DeleteCategoryOnly;
                expanded.Add(category);
                affectedOnly.AddRange(children);
            }
            else
            {
                operationType = ChannelOperationType.DeleteCategoryWithChildren;
                var requestedChildren = request.IncludeAllChildren
                    ? children
                    : children.Where(channel => request.ChildChannelIds.Contains(channel.Id)).ToArray();
                if (!request.IncludeAllChildren
                    && requestedChildren.Length != request.ChildChannelIds.Distinct().Count())
                {
                    return ChannelPlanResult.Failure(
                        "Every selected child must still belong to the category.");
                }

                if (requestedChildren.Length == 0)
                {
                    return ChannelPlanResult.Failure(
                        "Select child channels or choose all children, or use category-only deletion.");
                }

                expanded.AddRange(requestedChildren);
                expanded.Add(category);
            }
        }
        else
        {
            expanded.AddRange(selected);
        }

        var unsupported = expanded.Where(channel => !IsDeletable(channel.Kind)).ToArray();
        if (unsupported.Length > 0)
        {
            return ChannelPlanResult.Failure(
                unsupported.Select(channel =>
                    $"“{channel.Name}” ({channel.TypeName}) is protected or unsupported for deletion."));
        }

        var beforeStates = expanded
            .Concat(affectedOnly)
            .DistinctBy(channel => channel.Id)
            .Select(channel => ToState(channel, server))
            .ToImmutableArray();
        var steps = expanded
            .Select(
                (channel, index) =>
                {
                    var before = ToState(channel, server);
                    return new OperationStep(
                        Guid.NewGuid(),
                        index + 1,
                        OperationStepKind.DeleteChannel,
                        $"Delete {channel.TypeName.ToLowerInvariant()} “{channel.Name}”",
                        ToTarget(channel, before),
                        before,
                        null,
                        null,
                        true,
                        null);
                })
            .ToImmutableArray();
        var deleteCount = steps.Length;
        var confirmation = operationType == ChannelOperationType.DeleteCategoryWithChildren
            ? new OperationConfirmationRequirement(
                OperationConfirmationKind.TypedTextAndServerName,
                "Deletion is irreversible. Type the exact channel count and server name.",
                $"DELETE {deleteCount} CHANNELS / {server.Name}")
            : new OperationConfirmationRequirement(
                OperationConfirmationKind.TypedText,
                "Deletion is irreversible. Type the exact calculated channel count.",
                $"DELETE {deleteCount} CHANNELS");
        var risk = deleteCount > 1 || operationType == ChannelOperationType.DeleteCategoryWithChildren
            ? OperationRiskLevel.Irreversible
            : OperationRiskLevel.High;
        var proposedAfter = operationType == ChannelOperationType.DeleteCategoryOnly
            ? affectedOnly
                .Select(channel => ToState(channel, server) with
                {
                    ParentCategoryId = null,
                    ParentCategoryName = null
                })
                .ToImmutableArray()
            : ImmutableArray<ChannelOperationStateSnapshot>.Empty;
        return ChannelPlanResult.Success(
            BuildPlan(
                request.BotProfileId,
                context.Snapshot!,
                server,
                operationType,
                operationType switch
                {
                    ChannelOperationType.DeleteCategoryOnly =>
                        $"Delete category {expanded[0].Name} only",
                    ChannelOperationType.DeleteCategoryWithChildren =>
                        $"Delete category and {deleteCount - 1} child channel{Plural(deleteCount - 1)}",
                    _ => $"Delete {deleteCount} channel{Plural(deleteCount)}"
                },
                beforeStates,
                proposedAfter,
                [PermissionBits.ManageChannels],
                risk,
                steps,
                confirmation,
                OperationCompensationCapability.None,
                request.AuditReason));
    }

    public OperationPreview BuildPreview(OperationPlan plan, string botDisplayName)
    {
        var changes = plan.Steps
            .SelectMany(BuildPreviewPropertyChanges)
            .ToImmutableArray();
        var overwrites = plan.Steps
            .Select(step => step.PermissionOverwriteChange)
            .OfType<PermissionOverwriteChange>()
            .ToImmutableArray();
        ImmutableArray<string> consequences = plan.OperationType switch
        {
            ChannelOperationType.DeleteCategoryOnly =>
            [
                "The category is deleted.",
                "Its child channels are not deleted and become uncategorized.",
                "The deleted category ID and associations cannot be restored."
            ],
            ChannelOperationType.DeleteCategoryWithChildren or ChannelOperationType.DeleteChannels =>
            [
                "Deleted channels cannot be restored with the same IDs.",
                "Messages, threads, links, webhooks, integrations, and history are not recoverable.",
                "A local structure backup is not an undo operation."
            ],
            ChannelOperationType.CloneChannel or ChannelOperationType.CloneCategoryStructure =>
            [
                "Only modeled structure and selected overwrites are copied.",
                "Messages, threads, webhooks, invites, pins, history, and connected users are not cloned."
            ],
            _ =>
            [
                "Only the exact changes listed in this preview are queued.",
                "Cancellation cannot undo a Discord request that already succeeded."
            ]
        };
        return new OperationPreview(
            plan.OperationId,
            plan.CorrelationId,
            plan.Title,
            botDisplayName,
            plan.ServerNameSnapshot,
            plan.RiskLevel,
            plan.ExactTargetIds.Length > 0
                ? plan.ExactTargetIds.Length
                : plan.ProposedAfterState.Length,
            plan.EstimatedRequestCount,
            plan.RequiredBotPermissions.Select(permission => permission.ToString()).ToImmutableArray(),
            changes,
            overwrites,
            consequences,
            plan.ConfirmationRequirement,
            plan.AuditReason);
    }

    private ContextResult GetContext(Guid botProfileId, ulong serverId)
    {
        var snapshot = explorer.GetSnapshot(botProfileId);
        var server = snapshot.Servers.FirstOrDefault(item =>
            item.Id == serverId && item.Availability == ServerAvailability.Available);
        return server is null
            ? new ContextResult(
                snapshot,
                null,
                ChannelPlanResult.Failure("The selected server is unavailable in the current bot cache."))
            : new ContextResult(snapshot, server, null);
    }

    private static OperationPlan BuildPlan(
        Guid botProfileId,
        BotExplorerSnapshot snapshot,
        ServerReadModel server,
        ChannelOperationType operationType,
        string title,
        ImmutableArray<ChannelOperationStateSnapshot> before,
        ImmutableArray<ChannelOperationStateSnapshot> after,
        ImmutableArray<PermissionBits> requiredPermissions,
        OperationRiskLevel risk,
        ImmutableArray<OperationStep> steps,
        OperationConfirmationRequirement confirmation,
        OperationCompensationCapability compensation,
        string? auditReason)
    {
        var operationId = Guid.NewGuid();
        return new OperationPlan(
            operationId,
            Guid.NewGuid(),
            botProfileId,
            server.Id,
            server.Name,
            snapshot.LastAcceptedSequence,
            DateTimeOffset.UtcNow,
            operationType,
            title,
            before.Where(state => state.Id is not null).Select(state => state.Id!.Value).Distinct().ToImmutableArray(),
            before,
            after,
            requiredPermissions.Distinct().ToImmutableArray(),
            BuildPreconditions(before, requiredPermissions),
            risk,
            steps,
            steps.Length,
            confirmation,
            compensation,
            AuditReasonSanitizer.Sanitize(auditReason));
    }

    private static IEnumerable<PropertyChange> BuildPreviewPropertyChanges(
        OperationStep step)
    {
        if (step.Kind == OperationStepKind.ReorderChannel
            && step.BatchBeforeStates.Length == step.BatchAfterStates.Length)
        {
            return step.BatchBeforeStates
                .Zip(
                    step.BatchAfterStates,
                    (before, after) => step with
                    {
                        Target = step.Target with
                        {
                            Id = before.Id ?? 0,
                            DisplayName = before.Name
                        },
                        Before = before,
                        After = after
                    })
                .SelectMany(Prefix);
        }

        return Prefix(step);

        static IEnumerable<PropertyChange> Prefix(OperationStep current) =>
            BuildPropertyChanges(current)
                .Select(change => change with
                {
                    PropertyName = $"{current.Target.DisplayName}: {change.PropertyName}"
                });
    }

    private static ImmutableArray<OperationPrecondition> BuildPreconditions(
        ImmutableArray<ChannelOperationStateSnapshot> before,
        ImmutableArray<PermissionBits> requiredPermissions) =>
    [
        new(OperationPreconditionKind.BotConnected, "The selected bot remains connected.", true, null),
        new(OperationPreconditionKind.ServerAvailable, "The selected server remains available.", true, null),
        .. before.Where(state => state.Id is not null).Select(
            state => new OperationPrecondition(
                OperationPreconditionKind.TargetFingerprintMatches,
                $"“{state.Name}” remains unchanged since preview.",
                true,
                null)),
        .. requiredPermissions.Select(
            permission => new OperationPrecondition(
                OperationPreconditionKind.RequiredPermission,
                $"The bot still has {permission}.",
                true,
                null))
    ];

    private static OperationStep ModifyStep(
        int order,
        ChannelReadModel channel,
        ChannelOperationStateSnapshot before,
        ChannelOperationStateSnapshot after,
        OperationStepKind kind = OperationStepKind.ModifyChannel) =>
        new(
            Guid.NewGuid(),
            order,
            kind,
            $"Change {channel.Name}",
            ToTarget(channel, before),
            before,
            after,
            null,
            false,
            new OperationCompensation(
                OperationCompensationCapability.ExactWhenTargetUnchanged,
                OperationStepKind.ModifyChannel,
                channel.Id,
                before,
                null,
                "Restore the exact supported properties captured before execution."));

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
            _ => throw new InvalidOperationException("Unsupported clone channel type.")
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
                Fingerprint(after)),
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
                "Delete the newly created resource if a later step fails."));
    }

    internal static ChannelOperationStateSnapshot ToState(
        ChannelReadModel channel,
        ServerReadModel server) =>
        new(
            channel.Id,
            channel.Name,
            channel.Kind,
            channel.Position,
            channel.CategoryId,
            channel.CategoryName,
            channel.Topic,
            channel.IsNsfw,
            channel.SlowModeSeconds,
            channel.DefaultAutoArchiveMinutes,
            channel.Bitrate,
            channel.UserLimit,
            channel.RegionOverride,
            channel.PermissionOverwrites
                .Select(overwrite => ToOverwrite(overwrite, server))
                .OrderBy(overwrite => overwrite.TargetType)
                .ThenBy(overwrite => overwrite.TargetId)
                .ToImmutableArray())
        {
            AvailableTags = channel.AvailableTags,
            DefaultReaction = channel.DefaultReaction,
            DefaultSortOrder = channel.DefaultSortOrder,
            DefaultLayout = channel.DefaultLayout
        };

    internal static string Fingerprint(ChannelOperationStateSnapshot state)
    {
        var value = string.Join(
            '\u001f',
            state.Id,
            state.Name,
            state.Kind,
            state.Position,
            state.ParentCategoryId,
            state.Topic,
            state.IsNsfw,
            state.SlowModeSeconds,
            state.DefaultAutoArchiveMinutes,
            state.Bitrate,
            state.UserLimit,
            state.RegionOverride,
            string.Join(',', state.AvailableTags),
            state.DefaultReaction,
            state.DefaultSortOrder,
            state.DefaultLayout,
            string.Join(
                ';',
                state.PermissionOverwrites
                    .OrderBy(overwrite => overwrite.TargetType)
                    .ThenBy(overwrite => overwrite.TargetId)
                    .Select(overwrite =>
                        $"{overwrite.TargetType}:{overwrite.TargetId}:{overwrite.AllowedRaw}:{overwrite.DeniedRaw}")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static ChannelPermissionOverwriteSnapshot ToOverwrite(
        PermissionOverwriteReadModel overwrite,
        ServerReadModel server)
    {
        var displayName = overwrite.TargetType == PermissionTargetKind.Role
            ? server.Roles.FirstOrDefault(role => role.Id == overwrite.TargetId)?.Name
            : server.Members.Members.FirstOrDefault(member => member.Id == overwrite.TargetId)?.DisplayName;
        return new ChannelPermissionOverwriteSnapshot(
            overwrite.TargetId,
            overwrite.TargetType,
            displayName ?? overwrite.TargetId.ToString(CultureInfo.InvariantCulture),
            overwrite.AllowedRaw,
            overwrite.DeniedRaw);
    }

    private static OperationTarget ToTarget(
        ChannelReadModel channel,
        ChannelOperationStateSnapshot state) =>
        new(
            channel.Id,
            channel.Name,
            channel.Kind == ChannelKind.Category
                ? OperationTargetKind.Category
                : OperationTargetKind.Channel,
            channel.CategoryId,
            Fingerprint(state));

    private static PermissionOverwriteChange BuildOverwriteChange(
        ChannelPermissionOverwriteSnapshot? before,
        ChannelPermissionOverwriteSnapshot? after,
        ulong selectedMask)
    {
        var source = after ?? before
            ?? throw new InvalidOperationException("An overwrite diff requires at least one side.");
        var beforeAllowed = before?.AllowedRaw ?? 0;
        var afterAllowed = after?.AllowedRaw ?? 0;
        var beforeDenied = before?.DeniedRaw ?? 0;
        var afterDenied = after?.DeniedRaw ?? 0;
        return new PermissionOverwriteChange(
            source.TargetId,
            source.TargetType,
            source.TargetDisplayName,
            before,
            after,
            DescribeRawChanges(beforeAllowed, afterAllowed, selectedMask, "allow"),
            DescribeRawChanges(beforeDenied, afterDenied, selectedMask, "deny"));
    }

    private static ImmutableArray<string> DescribeRawChanges(
        ulong before,
        ulong after,
        ulong selectedMask,
        string valueKind)
    {
        var changed = (before ^ after) & selectedMask;
        if (changed == 0)
        {
            return ImmutableArray<string>.Empty;
        }

        var descriptions = new List<string>();
        AddPermissionDescription(AddReactionsRaw, "Add Reactions");
        AddPermissionDescription(SendMessagesRaw, "Send Messages");
        AddPermissionDescription(ConnectRaw, "Connect");
        AddPermissionDescription(SpeakRaw, "Speak");
        var knownMask = AddReactionsRaw | SendMessagesRaw | ConnectRaw | SpeakRaw;
        if ((changed & ~knownMask) != 0)
        {
            descriptions.Add($"{valueKind} raw bits 0x{(changed & ~knownMask):X}");
        }

        return descriptions.ToImmutableArray();

        void AddPermissionDescription(ulong bit, string name)
        {
            if ((changed & bit) != 0)
            {
                descriptions.Add($"{name}: {(((after & bit) != 0) ? valueKind : $"remove {valueKind}")}");
            }
        }
    }

    private static IEnumerable<PropertyChange> BuildPropertyChanges(OperationStep step)
    {
        if (step.Before is null && step.After is not null)
        {
            yield return new PropertyChange("Resource", null, $"{step.After.Kind} “{step.After.Name}”");
            yield break;
        }

        if (step.Before is not null && step.After is null)
        {
            yield return new PropertyChange("Resource", $"{step.Before.Kind} “{step.Before.Name}”", "Deleted");
            yield break;
        }

        if (step.Before is null || step.After is null)
        {
            yield break;
        }

        foreach (var change in CompareStates(step.Before, step.After))
        {
            yield return change;
        }
    }

    private static IEnumerable<PropertyChange> CompareStates(
        ChannelOperationStateSnapshot before,
        ChannelOperationStateSnapshot after)
    {
        if (before.Name != after.Name)
        {
            yield return new("Name", before.Name, after.Name);
        }

        if (before.ParentCategoryId != after.ParentCategoryId)
        {
            yield return new("Parent category", before.ParentCategoryName ?? "Uncategorized", after.ParentCategoryName ?? "Uncategorized");
        }

        if (before.Position != after.Position)
        {
            yield return new("Position", before.Position.ToString(CultureInfo.InvariantCulture), after.Position.ToString(CultureInfo.InvariantCulture));
        }

        if (before.Topic != after.Topic)
        {
            yield return new("Topic", before.Topic ?? "None", after.Topic ?? "None");
        }

        if (before.IsNsfw != after.IsNsfw)
        {
            yield return new("NSFW", Format(before.IsNsfw), Format(after.IsNsfw));
        }

        if (before.SlowModeSeconds != after.SlowModeSeconds)
        {
            yield return new("Slow mode", Format(before.SlowModeSeconds), Format(after.SlowModeSeconds));
        }

        if (before.DefaultAutoArchiveMinutes != after.DefaultAutoArchiveMinutes)
        {
            yield return new("Auto archive", Format(before.DefaultAutoArchiveMinutes), Format(after.DefaultAutoArchiveMinutes));
        }

        if (before.Bitrate != after.Bitrate)
        {
            yield return new("Bitrate", Format(before.Bitrate), Format(after.Bitrate));
        }

        if (before.UserLimit != after.UserLimit)
        {
            yield return new("User limit", Format(before.UserLimit), Format(after.UserLimit));
        }

        if (before.RegionOverride != after.RegionOverride)
        {
            yield return new("Region", before.RegionOverride ?? "Automatic", after.RegionOverride ?? "Automatic");
        }
    }

    private static List<string> ValidateCreationItems(
        ServerReadModel server,
        ImmutableArray<ChannelCreationItem> items)
    {
        var errors = new List<string>();
        if (items.Length is < 1 or > 50)
        {
            errors.Add("Create between 1 and 50 channels per plan.");
            return errors;
        }

        if (items.Select(item => NormalizeName(item.Name)).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != items.Length)
        {
            errors.Add("Requested channel names must be unique within the plan.");
        }

        foreach (var item in items)
        {
            if (!IsValidName(item.Name))
            {
                errors.Add($"“{item.Name}” is not a valid channel name.");
            }

            if (item.Kind is not (ChannelKind.Category or ChannelKind.Text or ChannelKind.Voice))
            {
                errors.Add($"Channel type {item.Kind} is not supported for creation.");
            }

            if (item.Kind == ChannelKind.Category && item.ParentCategoryId is not null)
            {
                errors.Add("A category cannot have a parent category.");
            }

            if (item.ParentCategoryId is not null
                && GetChannel(server, item.ParentCategoryId)?.Kind != ChannelKind.Category)
            {
                errors.Add($"The parent category for “{item.Name}” is unavailable.");
            }

            if (server.Channels.Any(channel =>
                    channel.Kind == item.Kind
                    && channel.CategoryId == item.ParentCategoryId
                    && string.Equals(
                        channel.Name,
                        NormalizeName(item.Name),
                        StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"A matching channel named “{item.Name}” already exists.");
            }

            if (item.Kind != ChannelKind.Text
                && (!string.IsNullOrWhiteSpace(item.Topic)
                    || item.IsNsfw is not null
                    || item.SlowModeSeconds is not null))
            {
                errors.Add($"Text-only properties were supplied for “{item.Name}”.");
            }

            if (item.Kind != ChannelKind.Voice
                && (item.Bitrate is not null || item.UserLimit is not null))
            {
                errors.Add($"Voice-only properties were supplied for “{item.Name}”.");
            }

            if (item.SlowModeSeconds is < 0 or > 21600)
            {
                errors.Add($"Slow mode for “{item.Name}” must be between 0 and 21,600 seconds.");
            }

            if (item.Bitrate is < 8000 or > 384000)
            {
                errors.Add($"Bitrate for “{item.Name}” must be between 8,000 and 384,000.");
            }

            if (item.UserLimit is < 0 or > 99)
            {
                errors.Add($"User limit for “{item.Name}” must be between 0 and 99.");
            }

            if (item.Position is < 0 or > 499)
            {
                errors.Add($"Position for “{item.Name}” must be between 0 and 499.");
            }
        }

        if (server.Channels.Length + items.Length > 500)
        {
            errors.Add("The plan could exceed Discord's server channel limit.");
        }

        if (items.Select((item, index) => item.Position is int position
                && position + index > 499)
            .Any(isOutOfRange => isOutOfRange))
        {
            errors.Add("One or more calculated channel positions would exceed 499.");
        }

        return errors;
    }

    private static List<string> ValidateState(
        ServerReadModel server,
        ChannelOperationStateSnapshot state,
        ulong currentChannelId)
    {
        var errors = new List<string>();
        if (!IsValidName(state.Name))
        {
            errors.Add("Channel names must contain 1 to 100 non-control characters.");
        }

        if (state.ParentCategoryId is not null
            && GetChannel(server, state.ParentCategoryId)?.Kind != ChannelKind.Category)
        {
            errors.Add("The selected parent category is unavailable.");
        }

        if (state.ParentCategoryId == currentChannelId)
        {
            errors.Add("A channel cannot be its own parent.");
        }

        if (state.SlowModeSeconds is < 0 or > 21600)
        {
            errors.Add("Slow mode must be between 0 and 21,600 seconds.");
        }

        if (state.Bitrate is < 8000 or > 384000)
        {
            errors.Add("Voice bitrate must be between 8,000 and 384,000.");
        }

        if (state.UserLimit is < 0 or > 99)
        {
            errors.Add("Voice user limit must be between 0 and 99.");
        }

        if (state.Position is < 0 or > 499)
        {
            errors.Add("Channel position must be between 0 and 499.");
        }

        if (state.DefaultAutoArchiveMinutes is not null
            && state.DefaultAutoArchiveMinutes is not (60 or 1440 or 4320 or 10080))
        {
            errors.Add("Default auto-archive must be 60, 1,440, 4,320, or 10,080 minutes.");
        }

        if (state.Kind != ChannelKind.Text
            && (state.Topic is not null
                || state.IsNsfw is not null
                || state.SlowModeSeconds is not null
                || state.DefaultAutoArchiveMinutes is not null))
        {
            errors.Add("Text-only properties cannot be applied to this channel type.");
        }

        if (state.Kind != ChannelKind.Voice
            && (state.Bitrate is not null
                || state.UserLimit is not null
                || state.RegionOverride is not null))
        {
            errors.Add("Voice-only properties cannot be applied to this channel type.");
        }

        return errors;
    }

    private static ChannelReadModel[] ResolveChannels(
        ServerReadModel server,
        IEnumerable<ulong> ids)
    {
        var distinct = ids.Distinct().ToHashSet();
        return server.Channels.Where(channel => distinct.Contains(channel.Id)).ToArray();
    }

    private static ChannelReadModel? GetChannel(ServerReadModel server, ulong? id) =>
        id is ulong channelId
            ? server.Channels.FirstOrDefault(channel => channel.Id == channelId)
            : null;

    private static int NextPosition(ServerReadModel server, ulong? categoryId) =>
        server.Channels
            .Where(channel => channel.CategoryId == categoryId && channel.Kind != ChannelKind.Category)
            .Select(channel => channel.Position)
            .DefaultIfEmpty(-1)
            .Max() + 1;

    private static int NextCategoryPosition(ServerReadModel server) =>
        server.Channels
            .Where(channel => channel.Kind == ChannelKind.Category)
            .Select(channel => channel.Position)
            .DefaultIfEmpty(-1)
            .Max() + 1;

    private static bool IsEditable(ChannelKind kind) =>
        kind is ChannelKind.Category or ChannelKind.Text or ChannelKind.Voice;

    private static bool IsDeletable(ChannelKind kind) =>
        kind is ChannelKind.Category or ChannelKind.Text or ChannelKind.Voice;

    private static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Trim().Length <= 100
        && !name.Any(char.IsControl);

    private static string NormalizeName(string name) => name.Trim();

    private static string? NormalizeOptionalText(string? value, int maximumLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length <= maximumLength
            ? trimmed
            : trimmed[..maximumLength];
    }

    private static bool OverwritesEqual(
        ImmutableArray<ChannelPermissionOverwriteSnapshot> first,
        ImmutableArray<ChannelPermissionOverwriteSnapshot> second) =>
        first.OrderBy(item => item.TargetType).ThenBy(item => item.TargetId)
            .Select(item => (item.TargetType, item.TargetId, item.AllowedRaw, item.DeniedRaw))
            .SequenceEqual(
                second.OrderBy(item => item.TargetType).ThenBy(item => item.TargetId)
                    .Select(item => (item.TargetType, item.TargetId, item.AllowedRaw, item.DeniedRaw)));

    private static OperationConfirmationRequirement ExplicitConfirmation() =>
        new(
            OperationConfirmationKind.Explicit,
            "Review the exact preview and explicitly confirm execution.",
            null);

    private static string Format<T>(T? value)
        where T : struct =>
        value?.ToString() ?? "Unavailable";

    private static string Plural(int count) => count == 1 ? string.Empty : "s";

    private sealed record ContextResult(
        BotExplorerSnapshot Snapshot,
        ServerReadModel? Server,
        ChannelPlanResult? Failure)
    {
        public bool IsSuccess => Server is not null && Failure is null;
    }
}

public static class AuditReasonSanitizer
{
    private const int MaximumLength = 480;

    public static string? Sanitize(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return null;
        }

        var sanitized = string.Join(
            ' ',
            reason
                .Where(character => !char.IsControl(character))
                .ToArray()
                .AsSpan()
                .ToString()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return sanitized.Length <= MaximumLength
            ? sanitized
            : sanitized[..MaximumLength];
    }
}
