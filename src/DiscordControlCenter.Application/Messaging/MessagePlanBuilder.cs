using System.Collections.Immutable;
using System.Text.RegularExpressions;
using DiscordControlCenter.Core.Messaging;

namespace DiscordControlCenter.Application.Messaging;

public sealed partial class MessagePlanBuilder : IMessagePlanBuilder
{
    public MessagePlanResult Build(MessageDraft draft, MessageOperationKind kind)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var errors = MessageLimits.Validate(draft.Content).ToList();
        if (draft.Destination.Kind == MessageDestinationKind.ServerChannel && draft.Destination.ChannelId is null)
        {
            errors.Add("Select a destination server channel.");
        }

        if (draft.Destination.Kind == MessageDestinationKind.IndividualDirectMessage && draft.Destination.RecipientUserId is null)
        {
            errors.Add("Select exactly one member for a direct message.");
        }

        if (draft.Attachments.Length > 0)
        {
            errors.Add("Attachment import is not enabled in Phase 5A yet.");
        }

        if (errors.Count != 0)
        {
            return MessagePlanResult.Failure(errors.ToArray());
        }

        var hasBroadMentionText = BroadMentionRegex().IsMatch(draft.Content.Body ?? string.Empty);
        var requiresStrong = hasBroadMentionText || draft.Content.AllowedMentions.HasBroadMentions;
        var prompt = kind == MessageOperationKind.IndividualDirectMessage
            ? $"Send one direct message to {draft.Destination.RecipientDisplayName ?? "the selected member"}?"
            : $"Send one message to #{draft.Destination.ChannelName ?? "the selected channel"}?";
        var requiredText = requiresStrong ? "CONFIRM MESSAGE DELIVERY" : null;
        var plan = new MessageOperationPlan(
            Guid.NewGuid(),
            Guid.NewGuid(),
            kind,
            draft.BotProfileId,
            draft.Destination,
            draft.Content,
            DateTimeOffset.UtcNow,
            -1,
            requiresStrong,
            prompt,
            requiredText,
            SanitizeAuditContext(draft.AuditContext))
        {
            TemplateId = draft.TemplateId
        };
        return MessagePlanResult.Success(plan);
    }

    public MessagePreview BuildPreview(MessageOperationPlan plan, string botDisplayName)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var warnings = new List<string>();
        if (BroadMentionRegex().IsMatch(plan.Content.Body ?? string.Empty))
        {
            warnings.Add("The message contains @everyone or @here text. It will not notify anyone unless broad mentions are explicitly enabled.");
        }

        if (plan.Content.AllowedMentions.AllowEveryoneAndHere)
        {
            warnings.Add("Broad mentions are enabled and can notify everyone in the destination.");
        }

        if (plan.Content.AllowedMentions.AllowRoleMentions)
        {
            warnings.Add("Role mentions are enabled. Confirm the selected role IDs before delivery.");
        }

        return new MessagePreview(
            string.IsNullOrWhiteSpace(botDisplayName) ? "Selected bot" : botDisplayName,
            plan,
            warnings,
            "Approximate application preview — not the Discord client.");
    }

    private static string? SanitizeAuditContext(string? context)
    {
        if (string.IsNullOrWhiteSpace(context))
        {
            return null;
        }

        var singleLine = Regex.Replace(context.Trim(), "\\s+", " ");
        return singleLine.Length <= 200 ? singleLine : singleLine[..200];
    }

    [GeneratedRegex("(?<!\\w)@(everyone|here)\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BroadMentionRegex();
}
