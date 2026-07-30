using System.Globalization;
using DiscordControlCenter.Core.Explorer;

namespace DiscordControlCenter.App.ViewModels;

public sealed class RoleItemViewModel(
    RoleReadModel model,
    HierarchyPreflightResult manage,
    HierarchyPreflightResult assign,
    bool isBotHighestRole)
{
    public RoleReadModel Model { get; } = model;
    public ulong Id => Model.Id;
    public string IdText => Id.ToString(CultureInfo.InvariantCulture);
    public string Name => Model.IsEveryone ? "@everyone" : Model.Name;
    public string PositionText => Model.Position.ToString(CultureInfo.CurrentCulture);
    public uint ColorRaw => Model.ColorRaw;
    public string ColorText => $"#{Model.ColorRaw:X6}";
    public string HoistedText => YesNo(Model.IsHoisted);
    public string MentionableText => YesNo(Model.IsMentionable);
    public string ManagedText => Model.IsManaged
        ? Model.IsBotManaged ? "Bot-managed" : "Managed externally"
        : "No";
    public string IconText => Model.UnicodeEmoji
        ?? (Model.IconUrl is null ? "None" : "Custom icon");
    public string? IconUrl => Model.IconUrl;
    public string TagsText => Model.TagsSummary ?? "None";
    public string CreatedAtText => Snowflake.DecodeTimestamp(Id)
        .ToLocalTime()
        .ToString("f", CultureInfo.CurrentCulture);
    public string MemberCountText => Model.MemberCount is int count
        ? Model.MemberCountCompleteness == DataCompleteness.Complete
            ? count.ToString(CultureInfo.CurrentCulture)
            : $"{count:N0} (partial)"
        : "Unavailable";
    public string PermissionCountText => (Model.PermissionNames.IsDefaultOrEmpty
            ? Enum.GetValues<PermissionBits>()
                .Count(permission =>
                    permission != PermissionBits.None && Model.Permissions.Has(permission))
            : Model.PermissionNames.Length)
        .ToString(CultureInfo.CurrentCulture);
    public string BotHierarchyText => isBotHighestRole
        ? "This is the selected bot's highest role."
        : manage.Decision == SafetyDecision.Allowed
            ? "This role is below the selected bot."
            : manage.Explanation;
    public string ManageabilityText => $"{manage.Decision}: {manage.Explanation}";
    public string AssignabilityText => $"{assign.Decision}: {assign.Explanation}";
    public string ReorderabilityText => ManageabilityText;
    public SafetyDecision Manageability => manage.Decision;
    public IReadOnlyList<string> Permissions => Model.PermissionNames.IsDefaultOrEmpty
        ? Enum.GetValues<PermissionBits>()
            .Where(permission =>
                permission != PermissionBits.None && Model.Permissions.Has(permission))
            .Select(permission => FormatPermissionName(permission.ToString()))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray()
        : Model.PermissionNames
            .Select(FormatPermissionName)
            .ToArray();

    private static string YesNo(bool value) => value ? "Yes" : "No";

    private static string FormatPermissionName(string name)
    {
        return string.Concat(
            name.Select((character, index) =>
                index > 0 && char.IsUpper(character)
                    ? $" {character}"
                    : character.ToString()));
    }
}
