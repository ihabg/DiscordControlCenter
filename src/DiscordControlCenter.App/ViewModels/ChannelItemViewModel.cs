using System.Globalization;
using DiscordControlCenter.App.Mvvm;
using DiscordControlCenter.Core.Explorer;

namespace DiscordControlCenter.App.ViewModels;

public sealed class ChannelItemViewModel(
    ChannelReadModel model,
    PermissionResolution permissionResolution) : ObservableObject
{
    private bool _isSelected;

    public ChannelReadModel Model { get; } = model;
    public ulong Id => Model.Id;
    public string Name => Model.Name;
    public string IdText => Model.Id.ToString(CultureInfo.InvariantCulture);
    public string TypeName => Model.TypeName;
    public string TypeIndicator => Model.Kind switch
    {
        ChannelKind.Text => "#",
        ChannelKind.Announcement => "A",
        ChannelKind.Voice => "V",
        ChannelKind.Stage => "S",
        ChannelKind.Forum => "F",
        ChannelKind.Media => "M",
        ChannelKind.Thread => "T",
        ChannelKind.Category => "C",
        _ => "?"
    };
    public string PositionText => Model.Position.ToString(CultureInfo.CurrentCulture);
    public string CategoryText => Model.CategoryName ?? "Uncategorized";
    public string CreatedAtText => Model.CreatedAt.ToLocalTime().ToString("f", CultureInfo.CurrentCulture);
    public string PermissionSynchronizationText => Model.IsPermissionSynchronized switch
    {
        true => "Synchronized with category",
        false => "Custom channel overwrites",
        null => "No parent category"
    };
    public string TopicText => string.IsNullOrWhiteSpace(Model.Topic) ? "Unavailable" : Model.Topic;
    public string NsfwText => FormatBoolean(Model.IsNsfw);
    public string SlowModeText => Model.SlowModeSeconds is int seconds ? $"{seconds} seconds" : "Unavailable";
    public string AutoArchiveText => Model.DefaultAutoArchiveMinutes is int minutes
        ? $"{minutes} minutes"
        : "Unavailable";
    public string BitrateText => Model.Bitrate is int bitrate ? $"{bitrate / 1000d:0.#} kbps" : "Unavailable";
    public string UserLimitText => Model.UserLimit?.ToString(CultureInfo.CurrentCulture) ?? "Unavailable";
    public string RegionText => string.IsNullOrWhiteSpace(Model.RegionOverride) ? "Automatic" : Model.RegionOverride;
    public string ConnectedUsersText => Model.ConnectedUserCount?.ToString(CultureInfo.CurrentCulture)
        ?? "Unavailable";
    public string AvailableTagsText => Model.AvailableTags.Length == 0
        ? "None"
        : string.Join(", ", Model.AvailableTags);
    public string DefaultReactionText => Model.DefaultReaction ?? "None";
    public string DefaultSortOrderText => Model.DefaultSortOrder ?? "Unavailable";
    public string DefaultLayoutText => Model.DefaultLayout ?? "Unavailable";
    public IReadOnlyList<PermissionResult> Permissions => permissionResolution.Permissions;
    public string CanViewText => FormatPermission(PermissionBits.ViewChannel);
    public string CanManageText => FormatPermission(PermissionBits.ManageChannels);
    public bool ShowsTextDetails => Model.Kind is ChannelKind.Text
        or ChannelKind.Announcement
        or ChannelKind.Forum
        or ChannelKind.Media
        or ChannelKind.Thread;
    public bool ShowsVoiceDetails => Model.Kind is ChannelKind.Voice or ChannelKind.Stage;
    public bool ShowsForumDetails => Model.Kind is ChannelKind.Forum or ChannelKind.Media;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    private string FormatPermission(PermissionBits permission)
    {
        var result = Permissions.First(item => item.Permission == permission);
        return result.Status switch
        {
            PermissionStatus.Allowed => "Allowed",
            PermissionStatus.AllowedThroughAdministrator => "Allowed (Administrator)",
            PermissionStatus.NotApplicable => "Not applicable",
            PermissionStatus.Unknown => "Unknown",
            _ => "Denied"
        };
    }

    private static string FormatBoolean(bool? value) =>
        value switch
        {
            true => "Yes",
            false => "No",
            null => "Unavailable"
        };
}

public sealed class ChannelGroupViewModel(
    string name,
    ChannelItemViewModel? category,
    IReadOnlyList<ChannelItemViewModel> channels)
{
    public string Name { get; } = name;
    public ChannelItemViewModel? Category { get; } = category;
    public IReadOnlyList<ChannelItemViewModel> Channels { get; } = channels;
    public bool HasCategory => Category is not null;
}
