using DiscordControlCenter.Application.Bots;
using DiscordControlCenter.Application.Explorer;
using DiscordControlCenter.Core.Bots;
using DiscordControlCenter.Core.Explorer;
using DiscordControlCenter.Core.Messaging;

namespace DiscordControlCenter.Application.Messaging;

public sealed record AutomationPreflightIssue(string SafeCode, string Message);

public sealed record AutomationPreflightResult(bool IsAllowed, IReadOnlyList<AutomationPreflightIssue> Issues, DateTimeOffset CheckedAt);

public interface IAutomationRulePreflightService
{
    AutomationPreflightResult Validate(AutomationRule rule);
}

public sealed class AutomationRulePreflightService(
    IBotConnectionManager connectionManager,
    IBotExplorerService explorer,
    IPermissionResolutionService permissions,
    IRoleHierarchySafetyService hierarchySafety) : IAutomationRulePreflightService
{
    public AutomationPreflightResult Validate(AutomationRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var issues = new List<AutomationPreflightIssue>();
        var snapshot = explorer.GetSnapshot(rule.BotProfileId);
        var connection = connectionManager.Snapshots.FirstOrDefault(item => item.BotProfileId == rule.BotProfileId);
        if (connection?.State != BotConnectionState.Connected)
        {
            issues.Add(new("BOT_DISCONNECTED", "Connect the selected bot before enabling join automation."));
        }

        if (connection?.FullMemberAccessEnabled != true)
        {
            issues.Add(new("GUILD_MEMBERS_LOCAL_DISABLED", "Enable Server Members Intent locally for this bot before enabling join automation."));
        }

        if (!rule.DeveloperPortalGuildMembersIntentAcknowledged)
        {
            issues.Add(new("GUILD_MEMBERS_PORTAL_UNACKNOWLEDGED", "Acknowledge that Server Members Intent must also be enabled in the Discord Developer Portal."));
        }

        var server = snapshot.Servers.FirstOrDefault(item => item.Id == rule.ServerId && item.Availability == ServerAvailability.Available);
        if (server is null)
        {
            issues.Add(new("SERVER_UNAVAILABLE", "The selected server is unavailable to this bot."));
            return Result(issues);
        }

        if (server.Members.Completeness is DataCompleteness.Unavailable or DataCompleteness.Failed or DataCompleteness.Limited)
        {
            issues.Add(new("MEMBER_DATA_INCOMPLETE", "Member data is not sufficiently complete for safe join automation."));
        }

        if (rule.Actions.Length == 0 || rule.Actions.Length > rule.RateLimitPolicy.MaximumActionCount)
        {
            issues.Add(new("ACTION_COUNT_INVALID", "The workflow action count exceeds the configured safe limit or is empty."));
        }

        foreach (var action in rule.Actions.OrderBy(action => action.Order))
        {
            if (action.MaximumRetryCount < 0 || action.MaximumRetryCount > rule.RateLimitPolicy.MaximumRetryCount)
            {
                issues.Add(new("RETRY_LIMIT_INVALID", "An action retry limit exceeds the rule's safe maximum."));
            }

            if (action.Kind == AutomationActionKind.Wait && (action.WaitDuration is null || action.WaitDuration <= TimeSpan.Zero || action.WaitDuration > rule.RateLimitPolicy.MaximumWaitDuration))
            {
                issues.Add(new("WAIT_DURATION_INVALID", "A workflow wait duration must be positive and within the configured safe limit."));
            }

            if (action.RoleAssignment is { } roleAction)
            {
                var role = server.Roles.FirstOrDefault(item => item.Id == roleAction.RoleId);
                if (role is null)
                {
                    issues.Add(new("ROLE_UNAVAILABLE", $"Role {roleAction.RoleName} is no longer available."));
                }
                else
                {
                    var hierarchy = hierarchySafety.CanAssignRole(server, role);
                    if (hierarchy.Decision != SafetyDecision.Allowed)
                    {
                        issues.Add(new($"ROLE_{hierarchy.ReasonCode}", hierarchy.Explanation));
                    }
                }
            }

            if (action.WelcomeMessage is { Destination.ChannelId: ulong channelId })
            {
                var channel = server.Channels.FirstOrDefault(item => item.Id == channelId);
                if (channel is null || channel.Kind is not (ChannelKind.Text or ChannelKind.Announcement or ChannelKind.Thread))
                {
                    issues.Add(new("WELCOME_CHANNEL_UNAVAILABLE", "The welcome channel is unavailable or cannot receive messages."));
                }
                else
                {
                    Require(permissions.ResolveChannel(rule.BotProfileId, snapshot.Version, server, channel), PermissionBits.SendMessages, issues);
                }
            }
        }

        return Result(issues);
    }

    private static void Require(PermissionResolution resolution, PermissionBits permission, List<AutomationPreflightIssue> issues)
    {
        var result = resolution.Permissions.FirstOrDefault(item => item.Permission == permission);
        if (result is null || result.Status is PermissionStatus.Unknown or PermissionStatus.Denied)
        {
            issues.Add(new("WELCOME_SEND_MESSAGES_UNAVAILABLE", "The bot must have a confirmed Send Messages permission for the welcome channel."));
        }
    }

    private static AutomationPreflightResult Result(List<AutomationPreflightIssue> issues) => new(issues.Count == 0, issues, DateTimeOffset.UtcNow);
}
