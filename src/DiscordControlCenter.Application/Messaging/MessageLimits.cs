using DiscordControlCenter.Core.Messaging;

namespace DiscordControlCenter.Application.Messaging;

public static class MessageLimits
{
    public const decimal NearLimitRatio = 0.90m;
    public const int MaximumMessageCharacters = 2_000;
    public const int MaximumEmbedTitleCharacters = 256;
    public const int MaximumEmbedDescriptionCharacters = 4_096;
    public const int MaximumEmbedFields = 25;
    public const int MaximumEmbedFieldNameCharacters = 256;
    public const int MaximumEmbedFieldValueCharacters = 1_024;
    public const int MaximumEmbedFooterCharacters = 2_048;
    public const int MaximumEmbedAuthorCharacters = 256;
    public const int MaximumEmbedCharacters = 6_000;

    public static IReadOnlyList<string> Validate(MessageContent content)
    {
        var errors = new List<string>();
        var body = content.Body ?? string.Empty;
        if (string.IsNullOrWhiteSpace(body) && content.Embed is null)
        {
            errors.Add("Enter message content or include an embed.");
        }

        if (body.Length > MaximumMessageCharacters)
        {
            errors.Add($"Message content cannot exceed {MaximumMessageCharacters} characters.");
        }

        if (content.Embed is { } embed)
        {
            ValidateEmbed(embed, errors);
        }

        errors.AddRange(ValidateMentionPolicy(content.AllowedMentions));

        return errors;
    }

    public static IReadOnlyList<string> ValidateMentionPolicy(AllowedMentionPolicy? policy)
    {
        if (policy is null)
        {
            return ["The saved mention policy is incomplete."];
        }

        var errors = new List<string>();
        if (policy.AllowedUserIds.Any(id => id == 0) || policy.AllowedRoleIds.Any(id => id == 0))
        {
            errors.Add("Saved mention targets must have valid Discord IDs.");
        }

        if (policy.AllowedUserIds.Distinct().Count() != policy.AllowedUserIds.Length
            || policy.AllowedRoleIds.Distinct().Count() != policy.AllowedRoleIds.Length)
        {
            errors.Add("Saved mention targets must not be duplicated.");
        }

        return errors;
    }

    public static ContentUsageResult GetUsage(MessageContent? content)
    {
        if (content is null)
        {
            return new ContentUsageResult(
                [NotApplicable("message.characters", "Plain message characters", "Immutable message content is unavailable.")],
                [NotApplicable("embed", "Embed", "This immutable occurrence has no embed data.")],
                ["Immutable message content is unavailable."]);
        }

        var plain = new[] { Usage("message.characters", "Plain message characters", content.Body?.Length ?? 0, MaximumMessageCharacters) };
        if (content.Embed is not { } embed)
        {
            return new ContentUsageResult(
                plain,
                [NotApplicable("embed", "Embed", "The saved occurrence does not contain an embed.")],
                Validate(content));
        }

        var rows = new List<ContentUsageRow>();
        AddIfPresent(rows, "embed.title", "Title", embed.Title, MaximumEmbedTitleCharacters);
        AddIfPresent(rows, "embed.description", "Description", embed.Description, MaximumEmbedDescriptionCharacters);
        AddIfPresent(rows, "embed.author", "Author name", embed.AuthorName, MaximumEmbedAuthorCharacters);
        AddIfPresent(rows, "embed.footer", "Footer text", embed.FooterText, MaximumEmbedFooterCharacters);
        for (var index = 0; index < embed.Fields.Length; index++)
        {
            var field = embed.Fields[index];
            rows.Add(Usage($"embed.field.{index}.name", $"Field {index + 1} name", field.Name?.Length ?? 0, MaximumEmbedFieldNameCharacters));
            rows.Add(Usage($"embed.field.{index}.value", $"Field {index + 1} value", field.Value?.Length ?? 0, MaximumEmbedFieldValueCharacters));
        }

        rows.Add(Usage("embed.fields", "Fields", embed.Fields.Length, MaximumEmbedFields));
        var total = (embed.Title?.Length ?? 0) + (embed.Description?.Length ?? 0)
            + (embed.FooterText?.Length ?? 0) + (embed.AuthorName?.Length ?? 0)
            + embed.Fields.Sum(field => (field.Name?.Length ?? 0) + (field.Value?.Length ?? 0));
        rows.Add(Usage("embed.total", "Total embed content", total, MaximumEmbedCharacters));
        return new ContentUsageResult(
            plain,
            rows,
            Validate(content));
    }

    private static void AddIfPresent(List<ContentUsageRow> rows, string id, string label, string? value, int maximum)
    {
        if (!string.IsNullOrEmpty(value))
        {
            rows.Add(Usage(id, label, value.Length, maximum));
        }
    }

    private static ContentUsageRow NotApplicable(string id, string label, string summary) =>
        new(id, label, 0, 0, 0, ContentUsageState.NotApplicable, false, summary, null);

    private static ContentUsageRow Usage(string id, string label, int used, int maximum)
    {
        var remaining = maximum - used;
        var state = used > maximum
            ? ContentUsageState.OverLimit
            : maximum > 0 && used >= decimal.Ceiling(maximum * NearLimitRatio)
                ? ContentUsageState.NearLimit
                : ContentUsageState.WithinLimit;
        var summary = state switch
        {
            ContentUsageState.OverLimit => $"{Math.Abs(remaining):N0} over limit",
            ContentUsageState.NearLimit => $"{remaining:N0} remaining; near the limit",
            _ => $"{remaining:N0} remaining"
        };
        return new ContentUsageRow(id, label, used, maximum, remaining, state, state == ContentUsageState.OverLimit, summary, state == ContentUsageState.NearLimit ? "Near the shared Discord limit." : null);
    }

    private static void ValidateEmbed(EmbedDraft embed, List<string> errors)
    {
        ValidateLength(embed.Title, MaximumEmbedTitleCharacters, "Embed title", errors);
        ValidateLength(embed.Description, MaximumEmbedDescriptionCharacters, "Embed description", errors);
        ValidateLength(embed.FooterText, MaximumEmbedFooterCharacters, "Embed footer", errors);
        ValidateLength(embed.AuthorName, MaximumEmbedAuthorCharacters, "Embed author", errors);
        foreach (var url in new[] { embed.Url, embed.AuthorUrl, embed.AuthorIconUrl, embed.ThumbnailUrl, embed.ImageUrl, embed.FooterIconUrl })
        {
            if (url is null)
            {
                continue;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                || parsed.Scheme is not ("https" or "http"))
            {
                errors.Add("Embed URLs must be valid http or https URLs.");
                break;
            }
        }

        if (embed.Fields.Length > MaximumEmbedFields)
        {
            errors.Add($"An embed cannot contain more than {MaximumEmbedFields} fields.");
        }

        var total = (embed.Title?.Length ?? 0) + (embed.Description?.Length ?? 0)
            + (embed.FooterText?.Length ?? 0) + (embed.AuthorName?.Length ?? 0);
        foreach (var field in embed.Fields)
        {
            ValidateLength(field.Name, MaximumEmbedFieldNameCharacters, "Embed field name", errors);
            ValidateLength(field.Value, MaximumEmbedFieldValueCharacters, "Embed field value", errors);
            total += field.Name?.Length ?? 0;
            total += field.Value?.Length ?? 0;
        }

        if (total > MaximumEmbedCharacters)
        {
            errors.Add($"The combined embed content cannot exceed {MaximumEmbedCharacters} characters.");
        }
    }

    private static void ValidateLength(string? value, int limit, string label, List<string> errors)
    {
        if (value?.Length > limit)
        {
            errors.Add($"{label} cannot exceed {limit} characters.");
        }
    }
}
