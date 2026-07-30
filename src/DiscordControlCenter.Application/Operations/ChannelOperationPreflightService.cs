using System.Collections.Immutable;
using DiscordControlCenter.Application.Bots;
using DiscordControlCenter.Application.Explorer;
using DiscordControlCenter.Core.Bots;
using DiscordControlCenter.Core.Explorer;
using DiscordControlCenter.Core.Operations;

namespace DiscordControlCenter.Application.Operations;

public sealed class ChannelOperationPreflightService(
    IBotConnectionManager connectionManager,
    IBotExplorerService explorer,
    IPermissionResolutionService permissions,
    IRoleHierarchySafetyService hierarchySafety,
    IVoiceChannelValidationService? voiceValidation = null) : IChannelOperationPreflightService
{
    private readonly IVoiceChannelValidationService _voiceValidation =
        voiceValidation ?? new VoiceChannelValidationService();

    public ChannelOperationPreflightResult Validate(OperationPlan plan)
    {
        var issues = new List<OperationPreflightIssue>();
        var evaluated = new List<OperationPrecondition>();
        var connection = connectionManager.Snapshots.FirstOrDefault(
            snapshot => snapshot.BotProfileId == plan.BotProfileId);
        var connected = connection?.State == BotConnectionState.Connected;
        evaluated.Add(
            Evaluate(
                OperationPreconditionKind.BotConnected,
                "The selected bot is connected.",
                connected,
                "BOT_DISCONNECTED"));
        if (!connected)
        {
            issues.Add(Issue("BOT_DISCONNECTED", "The selected bot is no longer connected."));
        }

        var snapshot = explorer.GetSnapshot(plan.BotProfileId);
        var sequenceIsCurrent = snapshot.LastAcceptedSequence >= plan.SourceExplorerSequence;
        evaluated.Add(
            Evaluate(
                OperationPreconditionKind.TargetFingerprintMatches,
                $"Explorer sequence {snapshot.LastAcceptedSequence} is not older than approved sequence {plan.SourceExplorerSequence}.",
                sequenceIsCurrent,
                "CACHE_SEQUENCE_REGRESSED"));
        if (!sequenceIsCurrent)
        {
            issues.Add(
                Issue(
                    "CACHE_SEQUENCE_REGRESSED",
                    "The explorer cache is older than the approved preview. Refresh and regenerate the plan.",
                    stale: true));
        }

        var server = snapshot.Servers.FirstOrDefault(item =>
            item.Id == plan.ServerId && item.Availability == ServerAvailability.Available);
        evaluated.Add(
            Evaluate(
                OperationPreconditionKind.ServerAvailable,
                "The selected server is available to this bot.",
                server is not null,
                "SERVER_UNAVAILABLE"));
        if (server is null)
        {
            issues.Add(Issue("SERVER_UNAVAILABLE", "The selected server is no longer available."));
            return Result(issues, evaluated);
        }

        if (!string.Equals(server.Name, plan.ServerNameSnapshot, StringComparison.Ordinal))
        {
            issues.Add(
                Issue(
                    "SERVER_NAME_CHANGED",
                    $"The server name changed from “{plan.ServerNameSnapshot}” to “{server.Name}”. Regenerate the preview.",
                    stale: true,
                    plan.ServerId));
        }

        foreach (var before in plan.ExactBeforeState.Where(state => state.Id is not null))
        {
            var current = server.Channels.FirstOrDefault(channel => channel.Id == before.Id);
            if (current is null)
            {
                evaluated.Add(
                    Evaluate(
                        OperationPreconditionKind.TargetExists,
                        $"“{before.Name}” still exists.",
                        false,
                        "TARGET_NOT_FOUND"));
                issues.Add(
                    Issue(
                        "TARGET_NOT_FOUND",
                        $"“{before.Name}” no longer exists.",
                        stale: true,
                        before.Id));
                continue;
            }

            var currentState = ChannelOperationPlanner.ToState(current, server);
            var expectedFingerprint = ChannelOperationPlanner.Fingerprint(before);
            var currentFingerprint = ChannelOperationPlanner.Fingerprint(currentState);
            var matches = string.Equals(
                expectedFingerprint,
                currentFingerprint,
                StringComparison.Ordinal);
            evaluated.Add(
                Evaluate(
                    OperationPreconditionKind.TargetFingerprintMatches,
                    $"“{before.Name}” is unchanged since preview.",
                    matches,
                    "TARGET_CHANGED"));
            if (!matches)
            {
                issues.Add(
                    Issue(
                        "TARGET_CHANGED",
                        DescribeChange(before, currentState),
                        stale: true,
                        before.Id));
            }
        }

        ValidateCreateConflicts(plan, server, issues, evaluated);
        ValidateVoiceCapabilities(plan, server, issues, evaluated);
        var createCount = plan.Steps.Count(step =>
            step.Kind is OperationStepKind.CreateCategory
                or OperationStepKind.CreateTextChannel
                or OperationStepKind.CreateVoiceChannel);
        if (createCount > 0 && server.Channels.Length + createCount > 500)
        {
            issues.Add(
                Issue(
                    "SERVER_CHANNEL_LIMIT",
                    "The current server channel count leaves insufficient room for this plan.",
                    stale: true));
        }

        ValidatePermissions(plan, snapshot, server, issues, evaluated);
        ValidateOverwriteHierarchy(plan, server, issues, evaluated);
        return Result(issues, evaluated);
    }

    private void ValidateVoiceCapabilities(
        OperationPlan plan,
        ServerReadModel server,
        List<OperationPreflightIssue> issues,
        List<OperationPrecondition> evaluated)
    {
        foreach (var state in plan.Steps
                     .Select(step => step.After)
                     .OfType<ChannelOperationStateSnapshot>()
                     .Where(state => state.Kind == ChannelKind.Voice))
        {
            var validation = _voiceValidation.Validate(
                server,
                state.Bitrate,
                state.UserLimit,
                state.RegionOverride);
            var allowed = validation.IsValid;
            evaluated.Add(
                Evaluate(
                    OperationPreconditionKind.SupportedChannelType,
                    $"Voice settings for “{state.Name}” remain supported by the current server model.",
                    allowed,
                    "VOICE_CAPABILITY_CHANGED"));
            if (!allowed)
            {
                issues.Add(
                    Issue(
                        "VOICE_CAPABILITY_CHANGED",
                        string.Join(" ", validation.Errors),
                        stale: true,
                        state.Id));
            }
        }
    }

    private void ValidatePermissions(
        OperationPlan plan,
        BotExplorerSnapshot snapshot,
        ServerReadModel server,
        List<OperationPreflightIssue> issues,
        List<OperationPrecondition> evaluated)
    {
        foreach (var required in plan.RequiredBotPermissions)
        {
            var resolutions = plan.Steps
                .Where(step => step.Target.Id != 0)
                .Select(step => server.Channels.FirstOrDefault(channel => channel.Id == step.Target.Id))
                .OfType<ChannelReadModel>()
                .Select(
                    channel => permissions.ResolveChannel(
                        plan.BotProfileId,
                        snapshot.Version,
                        server,
                        channel))
                .ToList();
            if (resolutions.Count == 0)
            {
                resolutions.Add(permissions.ResolveServer(plan.BotProfileId, snapshot.Version, server));
            }

            var results = resolutions
                .Select(resolution => resolution.Permissions.FirstOrDefault(item => item.Permission == required))
                .Where(result => result is not null)
                .ToArray();
            var unknown = results.Any(result => result!.Status == PermissionStatus.Unknown);
            var allowed = results.Length > 0
                && results.All(result => result!.Status is PermissionStatus.Allowed
                    or PermissionStatus.AllowedThroughAdministrator);
            evaluated.Add(
                Evaluate(
                    OperationPreconditionKind.RequiredPermission,
                    $"The bot has {required} for every target.",
                    allowed,
                    unknown ? "PERMISSION_UNKNOWN" : "MISSING_PERMISSION"));
            if (!allowed)
            {
                issues.Add(
                    Issue(
                        unknown ? "PERMISSION_UNKNOWN" : "MISSING_PERMISSION",
                        unknown
                            ? $"The bot's {required} result is incomplete. Execution is blocked."
                            : $"The bot no longer has {required} for every target."));
            }
        }
    }

    private void ValidateOverwriteHierarchy(
        OperationPlan plan,
        ServerReadModel server,
        List<OperationPreflightIssue> issues,
        List<OperationPrecondition> evaluated)
    {
        var roleIds = plan.Steps
            .SelectMany(step =>
                (step.PermissionOverwriteChange is { } change
                    ? new[] { (change.TargetId, change.TargetType) }
                    : [])
                .Concat(
                    (step.After?.PermissionOverwrites
                         ?? ImmutableArray<ChannelPermissionOverwriteSnapshot>.Empty)
                    .Select(overwrite => (overwrite.TargetId, overwrite.TargetType))))
            .Where(target => target.TargetType == PermissionTargetKind.Role)
            .Select(target => target.TargetId)
            .Distinct()
            .ToArray();
        foreach (var roleId in roleIds)
        {
            var role = server.Roles.FirstOrDefault(item => item.Id == roleId);
            if (role is null)
            {
                issues.Add(
                    Issue(
                        "OVERWRITE_ROLE_MISSING",
                        $"Overwrite role {roleId} is no longer visible.",
                        stale: true,
                        roleId));
                continue;
            }

            if (role.IsEveryone)
            {
                continue;
            }

            var hierarchy = hierarchySafety.CanManageRole(server, role);
            var allowed = hierarchy.Decision == SafetyDecision.Allowed;
            evaluated.Add(
                Evaluate(
                    OperationPreconditionKind.RequiredPermission,
                    $"The bot can manage overwrite role “{role.Name}”.",
                    allowed,
                    hierarchy.ReasonCode.ToString()));
            if (!allowed)
            {
                issues.Add(
                    Issue(
                        $"ROLE_{hierarchy.ReasonCode.ToString().ToUpperInvariant()}",
                        hierarchy.Explanation,
                        hierarchy.Decision == SafetyDecision.Unknown,
                        role.Id));
            }
        }
    }

    private static void ValidateCreateConflicts(
        OperationPlan plan,
        ServerReadModel server,
        List<OperationPreflightIssue> issues,
        List<OperationPrecondition> evaluated)
    {
        foreach (var step in plan.Steps.Where(step =>
                     step.ParentResultStepId is null
                     && step.Kind is OperationStepKind.CreateCategory
                         or OperationStepKind.CreateTextChannel
                         or OperationStepKind.CreateVoiceChannel))
        {
            if (step.After is not { } after)
            {
                continue;
            }

            var conflict = server.Channels.Any(channel =>
                channel.Kind == after.Kind
                && channel.CategoryId == after.ParentCategoryId
                && string.Equals(channel.Name, after.Name, StringComparison.OrdinalIgnoreCase));
            evaluated.Add(
                Evaluate(
                    OperationPreconditionKind.TargetFingerprintMatches,
                    $"No matching {after.Kind.ToString().ToLowerInvariant()} named “{after.Name}” has appeared.",
                    !conflict,
                    "CREATE_NAME_CONFLICT"));
            if (conflict)
            {
                issues.Add(
                    Issue(
                        "CREATE_NAME_CONFLICT",
                        $"A matching {after.Kind.ToString().ToLowerInvariant()} named “{after.Name}” now exists. Reconcile or regenerate the preview.",
                        stale: true));
            }
        }
    }

    private static string DescribeChange(
        ChannelOperationStateSnapshot before,
        ChannelOperationStateSnapshot current)
    {
        var changes = new List<string>();
        Add("name", before.Name, current.Name);
        Add("parent", before.ParentCategoryId, current.ParentCategoryId);
        Add("position", before.Position, current.Position);
        Add("topic", before.Topic, current.Topic);
        Add("NSFW", before.IsNsfw, current.IsNsfw);
        Add("slow mode", before.SlowModeSeconds, current.SlowModeSeconds);
        Add("bitrate", before.Bitrate, current.Bitrate);
        Add("user limit", before.UserLimit, current.UserLimit);
        if (!OverwritesMatch(before.PermissionOverwrites, current.PermissionOverwrites))
        {
            changes.Add("permission overwrites");
        }

        return changes.Count == 0
            ? $"“{before.Name}” changed after preview."
            : $"“{before.Name}” changed after preview: {string.Join(", ", changes)}.";

        void Add<T>(string name, T beforeValue, T currentValue)
        {
            if (!EqualityComparer<T>.Default.Equals(beforeValue, currentValue))
            {
                changes.Add(
                    $"{name} ({Format(beforeValue)} → {Format(currentValue)})");
            }
        }

        static string Format<T>(T value) =>
            value?.ToString() ?? "none";
    }

    private static bool OverwritesMatch(
        ImmutableArray<ChannelPermissionOverwriteSnapshot> first,
        ImmutableArray<ChannelPermissionOverwriteSnapshot> second) =>
        first.OrderBy(item => item.TargetType).ThenBy(item => item.TargetId)
            .Select(item => (item.TargetType, item.TargetId, item.AllowedRaw, item.DeniedRaw))
            .SequenceEqual(
                second.OrderBy(item => item.TargetType).ThenBy(item => item.TargetId)
                    .Select(item => (item.TargetType, item.TargetId, item.AllowedRaw, item.DeniedRaw)));

    private static OperationPrecondition Evaluate(
        OperationPreconditionKind kind,
        string description,
        bool satisfied,
        string failureCode) =>
        new(kind, description, satisfied, satisfied ? null : failureCode);

    private static OperationPreflightIssue Issue(
        string code,
        string message,
        bool stale = false,
        ulong? targetId = null) =>
        new(code, message, stale, targetId);

    private static ChannelOperationPreflightResult Result(
        IEnumerable<OperationPreflightIssue> issues,
        IEnumerable<OperationPrecondition> evaluated)
    {
        var issueArray = issues.ToImmutableArray();
        return new ChannelOperationPreflightResult(
            issueArray.Length == 0,
            issueArray.Any(issue => issue.IsStale),
            issueArray,
            evaluated.ToImmutableArray(),
            DateTimeOffset.UtcNow);
    }
}
