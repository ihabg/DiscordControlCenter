using DiscordControlCenter.Core.Messaging;

namespace DiscordControlCenter.Application.Messaging;

public static class MessageLimits
{
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

        return errors;
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
