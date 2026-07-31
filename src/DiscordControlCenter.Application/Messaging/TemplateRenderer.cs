using System.Collections.Immutable;
using System.Text.RegularExpressions;
using DiscordControlCenter.Core.Messaging;

namespace DiscordControlCenter.Application.Messaging;

public sealed partial class TemplateRenderer : ITemplateRenderer
{
    private static readonly IReadOnlyList<TemplateVariableDefinition> Variables =
    [
        new("member.displayName", "Selected member display name", true),
        new("member.username", "Selected member username", true),
        new("member.mention", "Selected member mention", false),
        new("server.name", "Server name", false),
        new("server.memberCount", "Visible server member count", false),
        new("channel.name", "Destination channel name", false),
        new("current.date", "Current local date", false),
        new("current.time", "Current local time", false)
    ];

    public IReadOnlyList<TemplateVariableDefinition> BuiltInVariables => Variables;

    public TemplateRenderResult Render(MessageTemplate messageTemplate, IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(messageTemplate);
        ArgumentNullException.ThrowIfNull(values);
        var unresolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var body = Replace(messageTemplate.Content.Body, values, unresolved, warnings);
        var embed = messageTemplate.Content.Embed is null ? null : RenderEmbed(messageTemplate.Content.Embed, values, unresolved, warnings);
        var content = new MessageContent(body, embed, messageTemplate.Content.AllowedMentions);
        var errors = MessageLimits.Validate(content).ToList();
        if (unresolved.Count > 0)
        {
            errors.Add("Resolve every template variable before delivery.");
        }

        return new TemplateRenderResult(
            errors.Count == 0,
            errors.Count == 0 ? content : null,
            errors.ToImmutableArray(),
            unresolved.Order().ToImmutableArray(),
            warnings.ToImmutableArray());
    }

    private static EmbedDraft RenderEmbed(
        EmbedDraft source,
        IReadOnlyDictionary<string, string?> values,
        HashSet<string> unresolved,
        List<string> warnings) =>
        source with
        {
            Title = Replace(source.Title, values, unresolved, warnings),
            Description = Replace(source.Description, values, unresolved, warnings),
            AuthorName = Replace(source.AuthorName, values, unresolved, warnings),
            FooterText = Replace(source.FooterText, values, unresolved, warnings),
            Fields = source.Fields.Select(field => field with
            {
                Name = Replace(field.Name, values, unresolved, warnings),
                Value = Replace(field.Value, values, unresolved, warnings)
            }).ToImmutableArray()
        };

    private static string Replace(
        string? source,
        IReadOnlyDictionary<string, string?> values,
        HashSet<string> unresolved,
        List<string> warnings)
    {
        if (string.IsNullOrEmpty(source))
        {
            return source ?? string.Empty;
        }

        return VariableRegex().Replace(
            source,
            match =>
            {
                var key = match.Groups[1].Value;
                if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                {
                    unresolved.Add(key);
                    return match.Value;
                }

                var definition = Variables.FirstOrDefault(item => string.Equals(item.Name, key, StringComparison.OrdinalIgnoreCase));
                if (definition?.IsMemberControlled == true)
                {
                    var sanitized = value.Replace("@", "@\u200B", StringComparison.Ordinal);
                    if (!string.Equals(value, sanitized, StringComparison.Ordinal))
                    {
                        warnings.Add($"Sanitized a mention-like value for {{{key}}}.");
                    }

                    return sanitized;
                }

                return value;
            });
    }

    [GeneratedRegex("\\{([a-zA-Z][a-zA-Z0-9.]*)\\}", RegexOptions.CultureInvariant)]
    private static partial Regex VariableRegex();
}
