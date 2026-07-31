using DiscordControlCenter.Application.Bots;
using DiscordControlCenter.Application.Explorer;
using DiscordControlCenter.Core.Bots;
using DiscordControlCenter.Core.Explorer;
using DiscordControlCenter.Core.Messaging;

namespace DiscordControlCenter.Application.Messaging;

public sealed class MessagePreflightService(
    IBotConnectionManager connectionManager,
    IBotExplorerService explorer,
    IPermissionResolutionService permissions) : IMessagePreflightService
{
    public MessagePreflightResult Validate(MessageOperationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var issues = new List<MessagePreflightIssue>();
        var snapshot = explorer.GetSnapshot(plan.BotProfileId);
        var connection = connectionManager.Snapshots.FirstOrDefault(item => item.BotProfileId == plan.BotProfileId);
        if (connection?.State != BotConnectionState.Connected)
        {
            issues.Add(new("BOT_DISCONNECTED", "The selected bot is not connected."));
        }

        var server = snapshot.Servers.FirstOrDefault(item => item.Id == plan.Destination.ServerId && item.Availability == ServerAvailability.Available);
        if (server is null)
        {
            issues.Add(new("SERVER_UNAVAILABLE", "The destination server is no longer available."));
            return Result(issues, false);
        }

        if (plan.SourceExplorerSequence >= 0 && snapshot.LastAcceptedSequence < plan.SourceExplorerSequence)
        {
            issues.Add(new("CACHE_SEQUENCE_REGRESSED", "The explorer cache is older than the approved preview."));
        }

        if (plan.Destination.Kind == MessageDestinationKind.IndividualDirectMessage)
        {
            ValidateMemberDestination(server, plan, issues);
            return Result(issues, false);
        }

        var channel = server.Channels.FirstOrDefault(item => item.Id == plan.Destination.ChannelId);
        if (channel is null)
        {
            issues.Add(new("CHANNEL_UNAVAILABLE", "The destination channel no longer exists."));
            return Result(issues, true);
        }

        if (channel.Kind is not (ChannelKind.Text or ChannelKind.Announcement or ChannelKind.Thread))
        {
            issues.Add(new("CHANNEL_DOES_NOT_SUPPORT_MESSAGES", "The selected channel does not support messages."));
            return Result(issues, true);
        }

        var resolution = permissions.ResolveChannel(plan.BotProfileId, snapshot.Version, server, channel);
        Require(resolution, PermissionBits.ViewChannel, "View Channel", issues);
        Require(resolution, PermissionBits.SendMessages, "Send Messages", issues);
        if (channel.Kind == ChannelKind.Thread)
        {
            Require(resolution, PermissionBits.SendMessagesInThreads, "Send Messages in Threads", issues);
        }

        if (plan.Content.Embed is not null)
        {
            Require(resolution, PermissionBits.EmbedLinks, "Embed Links", issues);
        }

        if (plan.Content.AllowedMentions.AllowEveryoneAndHere || plan.Content.AllowedMentions.AllowRoleMentions)
        {
            Require(resolution, PermissionBits.MentionEveryone, "Mention Everyone", issues);
        }

        foreach (var error in MessageLimits.Validate(plan.Content))
        {
            issues.Add(new("MESSAGE_VALIDATION", error));
        }

        return Result(issues, false);
    }

    private static void ValidateMemberDestination(ServerReadModel server, MessageOperationPlan plan, List<MessagePreflightIssue> issues)
    {
        var member = server.Members.Members.FirstOrDefault(item => item.Id == plan.Destination.RecipientUserId);
        if (member is null)
        {
            issues.Add(new("MEMBER_UNAVAILABLE", "The selected direct-message recipient is no longer available in the current server data."));
        }
    }

    private static void Require(PermissionResolution resolution, PermissionBits permission, string label, List<MessagePreflightIssue> issues)
    {
        var result = resolution.Permissions.FirstOrDefault(item => item.Permission == permission);
        if (result is null || result.Status == PermissionStatus.Unknown)
        {
            issues.Add(new("PERMISSION_UNKNOWN", $"The bot's {label} permission is incomplete. Delivery is blocked."));
        }
        else if (result.Status is not (PermissionStatus.Allowed or PermissionStatus.AllowedThroughAdministrator))
        {
            issues.Add(new("MISSING_PERMISSION", $"The bot does not have {label} for the destination."));
        }
    }

    private static MessagePreflightResult Result(List<MessagePreflightIssue> issues, bool stale) =>
        new(issues.Count == 0, stale, issues, DateTimeOffset.UtcNow);
}
