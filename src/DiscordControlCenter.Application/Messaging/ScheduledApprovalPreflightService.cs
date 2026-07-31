using DiscordControlCenter.Application.Bots;
using DiscordControlCenter.Application.Explorer;
using DiscordControlCenter.Core.Bots;
using DiscordControlCenter.Core.Explorer;
using DiscordControlCenter.Core.Messaging;

namespace DiscordControlCenter.Application.Messaging;

/// <summary>
/// Converts immutable approval data and the current read-only explorer state into a safe,
/// stable UI preflight. It deliberately does not send anything and does not replace the
/// executor's just-before-send preflight.
/// </summary>
public sealed class ScheduledApprovalPreflightService(
    IBotProfileRepository profiles,
    IBotConnectionManager connections,
    IBotExplorerService explorer,
    IPermissionResolutionService permissions) : IScheduledApprovalPreflightService
{
    public async Task<ScheduledApprovalPreflightResult> EvaluateAsync(
        ScheduledMessageApproval approval,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approval);
        cancellationToken.ThrowIfCancellationRequested();

        var content = approval.ImmutableContent;
        var usage = GetUsage(content);
        var checks = new List<ScheduledApprovalPreflightCheck>(14)
        {
            SnapshotCheck(approval),
            PlainMessageCheck(content, usage),
            EmbedCheck(content, usage),
            MentionPolicyCheck(content)
        };

        BotProfile? profile = null;
        var profileAvailable = false;
        try
        {
            profile = await profiles.GetAsync(approval.Snapshot.BotProfileId, cancellationToken).ConfigureAwait(false);
            profileAvailable = profile is not null;
            checks.Add(profile is null
                ? Check(ScheduledApprovalPreflightCheckId.BotProfileExists, "Bot profile exists", ScheduledApprovalPreflightState.Blocked, true, "The saved bot profile no longer exists.", "Choose a current bot profile before approving.", "BOT_PROFILE_MISSING")
                : Check(ScheduledApprovalPreflightCheckId.BotProfileExists, "Bot profile exists", ScheduledApprovalPreflightState.Allowed, true, "The saved bot profile is available.", null, null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            checks.Add(Check(ScheduledApprovalPreflightCheckId.BotProfileExists, "Bot profile exists", ScheduledApprovalPreflightState.Unavailable, true, "The saved bot profile could not be checked.", "Refresh current Discord status and try again.", "BOT_PROFILE_CHECK_UNAVAILABLE"));
        }

        if (!profileAvailable)
        {
            AddUnavailableLiveChecks(checks, "Bot profile availability must be resolved first.", content);
            return Result(checks, usage);
        }

        var connection = connections.Snapshots.FirstOrDefault(item => item.BotProfileId == approval.Snapshot.BotProfileId);
        var connected = profile!.IsEnabled && connection?.State == BotConnectionState.Connected;
        checks.Add(connected
            ? Check(ScheduledApprovalPreflightCheckId.BotConnected, "Bot connected", ScheduledApprovalPreflightState.Allowed, true, "The selected bot is connected.", null, null)
            : Check(ScheduledApprovalPreflightCheckId.BotConnected, "Bot connected", ScheduledApprovalPreflightState.Unavailable, true, "The selected bot is not connected.", "Connect the selected bot before sending.", "BOT_DISCONNECTED"));
        if (!connected)
        {
            AddUnavailableAfterConnection(checks, "Connect the selected bot before current destination status can be checked.", content);
            return Result(checks, usage);
        }

        BotExplorerSnapshot snapshot;
        ServerReadModel? server;
        try
        {
            snapshot = explorer.GetSnapshot(approval.Snapshot.BotProfileId);
            server = snapshot.Servers.FirstOrDefault(item => item.Id == approval.Snapshot.Destination.ServerId && item.Availability == ServerAvailability.Available);
        }
        catch
        {
            checks.Add(Check(ScheduledApprovalPreflightCheckId.ServerAccessible, "Server accessible", ScheduledApprovalPreflightState.Unavailable, true, "The destination server could not be checked.", "Refresh current Discord status and try again.", "SERVER_CHECK_UNAVAILABLE"));
            AddUnavailableAfterServer(checks, "The destination server must be available first.", content);
            return Result(checks, usage);
        }

        if (server is null)
        {
            checks.Add(Check(ScheduledApprovalPreflightCheckId.ServerAccessible, "Server accessible", ScheduledApprovalPreflightState.Blocked, true, "The destination server is unavailable.", "Reconnect the bot or choose an accessible destination.", "SERVER_UNAVAILABLE"));
            AddUnavailableAfterServer(checks, "The destination server must be available first.", content);
            return Result(checks, usage);
        }

        checks.Add(Check(ScheduledApprovalPreflightCheckId.ServerAccessible, "Server accessible", ScheduledApprovalPreflightState.Allowed, true, "The destination server is currently accessible.", null, null));
        var channel = approval.Snapshot.Destination.ChannelId is ulong channelId
            ? server.Channels.FirstOrDefault(item => item.Id == channelId)
            : null;
        if (channel is null)
        {
            checks.Add(Check(ScheduledApprovalPreflightCheckId.ChannelExists, "Channel exists", ScheduledApprovalPreflightState.Blocked, true, "The destination channel no longer exists.", "Choose an existing destination channel before approving.", "CHANNEL_MISSING"));
            AddUnavailableAfterChannel(checks, "The destination channel must exist first.", content);
            return Result(checks, usage);
        }

        checks.Add(Check(ScheduledApprovalPreflightCheckId.ChannelExists, "Channel exists", ScheduledApprovalPreflightState.Allowed, true, "The destination channel exists.", null, null));
        var supportsMessages = channel.Kind is ChannelKind.Text or ChannelKind.Announcement or ChannelKind.Thread;
        checks.Add(supportsMessages
            ? Check(ScheduledApprovalPreflightCheckId.ChannelSupportsMessageSending, "Channel supports message sending", ScheduledApprovalPreflightState.Allowed, true, "The destination channel supports messages.", null, null)
            : Check(ScheduledApprovalPreflightCheckId.ChannelSupportsMessageSending, "Channel supports message sending", ScheduledApprovalPreflightState.Blocked, true, "The destination channel does not support message sending.", "Choose a text, announcement, or thread channel.", "CHANNEL_UNSUPPORTED"));
        if (!supportsMessages)
        {
            AddUnavailablePermissions(checks, "The destination channel does not support messages.", content);
            return Result(checks, usage);
        }

        PermissionResolution? resolution;
        try
        {
            resolution = permissions.ResolveChannel(approval.Snapshot.BotProfileId, snapshot.Version, server, channel);
        }
        catch
        {
            AddUnavailablePermissions(checks, "Current channel permissions could not be resolved.", content);
            return Result(checks, usage);
        }

        checks.Add(PermissionCheck(ScheduledApprovalPreflightCheckId.ViewChannel, "View Channel", PermissionBits.ViewChannel, true, resolution));
        checks.Add(PermissionCheck(ScheduledApprovalPreflightCheckId.SendMessages, "Send Messages", PermissionBits.SendMessages, true, resolution));
        checks.Add(content?.Embed is null
            ? NotRequired(ScheduledApprovalPreflightCheckId.EmbedLinks, "Embed Links", "The saved occurrence does not contain an embed.")
            : PermissionCheck(ScheduledApprovalPreflightCheckId.EmbedLinks, "Embed Links", PermissionBits.EmbedLinks, true, resolution));
        checks.Add(NotRequired(ScheduledApprovalPreflightCheckId.AttachFiles, "Attach Files", "The saved occurrence does not contain attachments."));
        var broad = content?.AllowedMentions.AllowEveryoneAndHere == true || content?.AllowedMentions.AllowRoleMentions == true;
        checks.Add(!broad
            ? NotRequired(ScheduledApprovalPreflightCheckId.MentionEveryone, "Mention Everyone", "The saved mention policy does not permit broad mentions.")
            : PermissionCheck(ScheduledApprovalPreflightCheckId.MentionEveryone, "Mention Everyone", PermissionBits.MentionEveryone, true, resolution));
        return Result(checks, usage);
    }

    public ContentUsageResult GetUsage(MessageContent? content) => MessageLimits.GetUsage(content);

    public IReadOnlyList<MentionPolicyUsageRow> GetMentionPolicyUsage(MessageContent? content)
    {
        var policy = content?.AllowedMentions;
        if (policy is null)
        {
            return [new("mentions.unavailable", "Mention policy", false, "Immutable mention policy is unavailable.")];
        }

        return
        [
            Mention("mentions.everyone", "Everyone mention", policy.AllowEveryoneAndHere),
            Mention("mentions.here", "Here mention", policy.AllowEveryoneAndHere),
            Mention("mentions.roles", "Role mention parsing", policy.AllowRoleMentions),
            Mention("mentions.users", "User mention parsing", policy.AllowedUserIds.Length > 0),
            Mention("mentions.replied-user", "Replied-user mention", false)
        ];
    }

    private static MentionPolicyUsageRow Mention(string id, string label, bool allowed) =>
        new(id, label, allowed, allowed ? "Allowed by the saved immutable mention policy." : "Blocked by the saved immutable mention policy.");

    private static ScheduledApprovalPreflightCheck SnapshotCheck(ScheduledMessageApproval approval) =>
        approval.Compatibility is SnapshotCompatibility.Supported or SnapshotCompatibility.SupportedLegacy
            ? Check(ScheduledApprovalPreflightCheckId.SnapshotCompatibility, "Snapshot compatibility", ScheduledApprovalPreflightState.Allowed, true, "This immutable occurrence is supported by this application version.", null, null)
            : Check(ScheduledApprovalPreflightCheckId.SnapshotCompatibility, "Snapshot compatibility", ScheduledApprovalPreflightState.Blocked, true, "This immutable occurrence is unsupported or incomplete.", "Use a compatible occurrence snapshot before approving.", "SNAPSHOT_INCOMPATIBLE");

    private static ScheduledApprovalPreflightCheck PlainMessageCheck(MessageContent? content, ContentUsageResult usage)
    {
        var invalid = content is null || usage.PlainMessageRows.Any(row => row.BlocksApproval)
            || (content is not null && string.IsNullOrWhiteSpace(content.Body) && content.Embed is null);
        return invalid
            ? Check(ScheduledApprovalPreflightCheckId.PlainMessageLimits, "Plain-message limits", ScheduledApprovalPreflightState.Blocked, true, "The saved message exceeds Discord limits or has no content.", "Reduce the immutable message content before approving.", "MESSAGE_LIMITS")
            : Check(ScheduledApprovalPreflightCheckId.PlainMessageLimits, "Plain-message limits", ScheduledApprovalPreflightState.Allowed, true, "The saved message is within Discord's plain-message limit.", null, null);
    }

    private static ScheduledApprovalPreflightCheck EmbedCheck(MessageContent? content, ContentUsageResult usage)
    {
        if (content?.Embed is null)
        {
            return NotRequired(ScheduledApprovalPreflightCheckId.EmbedLimits, "Embed limits", "The saved occurrence does not contain an embed.");
        }

        var invalid = usage.EmbedRows.Any(row => row.BlocksApproval) || MessageLimits.Validate(content).Any(error => error.Contains("Embed", StringComparison.OrdinalIgnoreCase));
        return invalid
            ? Check(ScheduledApprovalPreflightCheckId.EmbedLimits, "Embed limits", ScheduledApprovalPreflightState.Blocked, true, "The saved embed exceeds Discord limits or has invalid values.", "Correct the immutable embed before approving.", "EMBED_LIMITS")
            : Check(ScheduledApprovalPreflightCheckId.EmbedLimits, "Embed limits", ScheduledApprovalPreflightState.Allowed, true, "The saved embed is within Discord's limits.", null, null);
    }

    private static ScheduledApprovalPreflightCheck MentionPolicyCheck(MessageContent? content)
    {
        var invalid = MessageLimits.ValidateMentionPolicy(content?.AllowedMentions).Count > 0;
        return invalid
            ? Check(ScheduledApprovalPreflightCheckId.AllowedMentionPolicy, "Allowed-mention policy validity", ScheduledApprovalPreflightState.Blocked, true, "The saved mention policy is invalid or incomplete.", "Use a valid immutable mention policy before approving.", "MENTION_POLICY_INVALID")
            : Check(ScheduledApprovalPreflightCheckId.AllowedMentionPolicy, "Allowed-mention policy validity", ScheduledApprovalPreflightState.Allowed, true, "The saved mention policy is valid.", null, null);
    }

    private static ScheduledApprovalPreflightCheck PermissionCheck(ScheduledApprovalPreflightCheckId id, string label, PermissionBits bit, bool required, PermissionResolution resolution)
    {
        var result = resolution.Permissions.FirstOrDefault(item => item.Permission == bit);
        return result?.Status switch
        {
            PermissionStatus.Allowed or PermissionStatus.AllowedThroughAdministrator => Check(id, label, ScheduledApprovalPreflightState.Allowed, required, $"The bot currently has {label}.", null, null),
            PermissionStatus.Unknown or null => Check(id, label, ScheduledApprovalPreflightState.Unknown, required, $"The bot's {label} permission could not be resolved.", "Refresh current Discord status and try again.", "PERMISSION_UNKNOWN"),
            _ => Check(id, label, ScheduledApprovalPreflightState.Blocked, required, $"The bot is missing {label}.", $"Grant {label} before approving.", "MISSING_PERMISSION")
        };
    }

    private static void AddUnavailableLiveChecks(List<ScheduledApprovalPreflightCheck> checks, string explanation, MessageContent? content)
    {
        checks.Add(Check(ScheduledApprovalPreflightCheckId.BotConnected, "Bot connected", ScheduledApprovalPreflightState.Unavailable, true, explanation, null, "DEPENDENCY_UNAVAILABLE"));
        AddUnavailableAfterConnection(checks, explanation, content);
    }

    private static void AddUnavailableAfterConnection(List<ScheduledApprovalPreflightCheck> checks, string explanation, MessageContent? content)
    {
        checks.Add(Check(ScheduledApprovalPreflightCheckId.ServerAccessible, "Server accessible", ScheduledApprovalPreflightState.Unavailable, true, explanation, null, "DEPENDENCY_UNAVAILABLE"));
        AddUnavailableAfterServer(checks, explanation, content);
    }

    private static void AddUnavailableAfterServer(List<ScheduledApprovalPreflightCheck> checks, string explanation, MessageContent? content)
    {
        checks.Add(Check(ScheduledApprovalPreflightCheckId.ChannelExists, "Channel exists", ScheduledApprovalPreflightState.Unavailable, true, explanation, null, "DEPENDENCY_UNAVAILABLE"));
        AddUnavailableAfterChannel(checks, explanation, content);
    }

    private static void AddUnavailableAfterChannel(List<ScheduledApprovalPreflightCheck> checks, string explanation, MessageContent? content)
    {
        checks.Add(Check(ScheduledApprovalPreflightCheckId.ChannelSupportsMessageSending, "Channel supports message sending", ScheduledApprovalPreflightState.Unavailable, true, explanation, null, "DEPENDENCY_UNAVAILABLE"));
        AddUnavailablePermissions(checks, explanation, content);
    }

    private static void AddUnavailablePermissions(List<ScheduledApprovalPreflightCheck> checks, string explanation, MessageContent? content)
    {
        checks.Add(Check(ScheduledApprovalPreflightCheckId.ViewChannel, "View Channel", ScheduledApprovalPreflightState.Unavailable, true, explanation, null, "DEPENDENCY_UNAVAILABLE"));
        checks.Add(Check(ScheduledApprovalPreflightCheckId.SendMessages, "Send Messages", ScheduledApprovalPreflightState.Unavailable, true, explanation, null, "DEPENDENCY_UNAVAILABLE"));
        checks.Add(content?.Embed is null
            ? NotRequired(ScheduledApprovalPreflightCheckId.EmbedLinks, "Embed Links", "The saved occurrence does not contain an embed.")
            : Check(ScheduledApprovalPreflightCheckId.EmbedLinks, "Embed Links", ScheduledApprovalPreflightState.Unavailable, true, explanation, null, "DEPENDENCY_UNAVAILABLE"));
        checks.Add(NotRequired(ScheduledApprovalPreflightCheckId.AttachFiles, "Attach Files", "The saved occurrence does not contain attachments."));
        var broad = content?.AllowedMentions.AllowEveryoneAndHere == true || content?.AllowedMentions.AllowRoleMentions == true;
        checks.Add(!broad
            ? NotRequired(ScheduledApprovalPreflightCheckId.MentionEveryone, "Mention Everyone", "The saved mention policy does not permit broad mentions.")
            : Check(ScheduledApprovalPreflightCheckId.MentionEveryone, "Mention Everyone", ScheduledApprovalPreflightState.Unavailable, true, explanation, null, "DEPENDENCY_UNAVAILABLE"));
    }

    private static ScheduledApprovalPreflightCheck NotRequired(ScheduledApprovalPreflightCheckId id, string label, string explanation) =>
        Check(id, label, ScheduledApprovalPreflightState.NotRequired, false, explanation, null, null);

    private static ScheduledApprovalPreflightCheck Check(ScheduledApprovalPreflightCheckId id, string label, ScheduledApprovalPreflightState state, bool required, string explanation, string? remediation, string? category) =>
        new(id, label, state, required, required && state != ScheduledApprovalPreflightState.Allowed, explanation, remediation, category);

    private static ScheduledApprovalPreflightResult Result(IReadOnlyList<ScheduledApprovalPreflightCheck> checks, ContentUsageResult usage)
    {
        var blocking = checks.Where(check => check.BlocksApproval).Select(check => check.Explanation).ToArray();
        var state = checks.Any(check => check.BlocksApproval && check.State == ScheduledApprovalPreflightState.Blocked)
            ? ScheduledApprovalPreflightState.Blocked
            : checks.Any(check => check.BlocksApproval && check.State == ScheduledApprovalPreflightState.Unavailable)
                ? ScheduledApprovalPreflightState.Unavailable
                : checks.Any(check => check.BlocksApproval && check.State == ScheduledApprovalPreflightState.Unknown)
                    ? ScheduledApprovalPreflightState.Unknown
                    : ScheduledApprovalPreflightState.Allowed;
        var summary = state == ScheduledApprovalPreflightState.Allowed
            ? "Allowed — all required snapshot and current Discord checks passed."
            : blocking.FirstOrDefault() ?? "Current Discord status cannot be confirmed.";
        var warnings = usage.PlainMessageRows.Concat(usage.EmbedRows)
            .Select(row => row.Warning).Where(warning => warning is not null).Cast<string>()
            .Concat(usage.ValidationWarnings).Distinct(StringComparer.Ordinal).ToArray();
        return new ScheduledApprovalPreflightResult(
            state == ScheduledApprovalPreflightState.Allowed,
            state,
            summary,
            DateTimeOffset.UtcNow,
            checks,
            warnings,
            blocking);
    }
}
