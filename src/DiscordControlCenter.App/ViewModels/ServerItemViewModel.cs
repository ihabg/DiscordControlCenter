using System.Globalization;
using DiscordControlCenter.Core.Explorer;

namespace DiscordControlCenter.App.ViewModels;

public sealed class ServerItemViewModel(
    ServerReadModel model,
    PermissionResolution permissionResolution)
{
    public ServerReadModel Model { get; } = model;
    public ulong Id => Model.Id;
    public string Name => Model.Name;
    public string IdText => Model.Id.ToString(CultureInfo.InvariantCulture);
    public string? IconUrl => Model.IconUrl;
    public string Description => string.IsNullOrWhiteSpace(Model.Description)
        ? "No server description is available."
        : Model.Description;
    public string OwnerIdText => Model.OwnerId.ToString(CultureInfo.InvariantCulture);
    public string CreatedAtText => Model.CreatedAt.ToLocalTime().ToString("f", CultureInfo.CurrentCulture);
    public string MemberCountText => Model.ApproximateMemberCount?.ToString(CultureInfo.CurrentCulture)
        ?? "Unavailable";
    public string ChannelCountText => Model.Channels
        .Count(channel => channel.Kind != ChannelKind.Category)
        .ToString(CultureInfo.CurrentCulture);
    public string SummaryText =>
        $"{IdText}  ·  {MemberCountText} members  ·  {ChannelCountText} channels";
    public string RoleCountText => Model.RoleCount.ToString(CultureInfo.CurrentCulture);
    public string BotNicknameText => Model.BotNickname ?? "None";
    public string BotHighestRoleText => Model.BotHighestRole ?? "Unavailable";
    public string BotSummaryText => $"Bot: {BotNicknameText}  ·  {BotHighestRoleText}";
    public string BotRolePositionText => Model.BotRolePosition?.ToString(CultureInfo.CurrentCulture)
        ?? "Unavailable";
    public string AvailabilityText => Model.Availability == ServerAvailability.Available
        ? "Available"
        : "Temporarily unavailable";
    public string CategoryCountText => Model.CategoryCount.ToString(CultureInfo.CurrentCulture);
    public string TextChannelCountText => Model.TextChannelCount.ToString(CultureInfo.CurrentCulture);
    public string VoiceChannelCountText => Model.VoiceChannelCount.ToString(CultureInfo.CurrentCulture);
    public string ForumChannelCountText => Model.ForumChannelCount.ToString(CultureInfo.CurrentCulture);
    public string StageChannelCountText => Model.StageChannelCount.ToString(CultureInfo.CurrentCulture);
    public string EmojiCountText => Model.EmojiCount.ToString(CultureInfo.CurrentCulture);
    public string BoostTierText => Model.BoostTier;
    public string BoostCountText => Model.BoostCount?.ToString(CultureInfo.CurrentCulture)
        ?? "Unavailable";
    public string RefreshedAtText => Model.RefreshedAt.ToLocalTime().ToString("G", CultureInfo.CurrentCulture);
    public IReadOnlyList<PermissionResult> Permissions => permissionResolution.Permissions;
}

public sealed class ServerOptionViewModel(ServerReadModel model)
{
    public ulong Id => model.Id;
    public string Name => model.Name;
    public string? IconUrl => model.IconUrl;
    public string AvailabilityText => model.Availability == ServerAvailability.Available
        ? "Available"
        : "Unavailable";
}
