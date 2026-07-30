using System.Collections.Immutable;
using Discord;
using Discord.WebSocket;
using DiscordControlCenter.Core.Explorer;

namespace DiscordControlCenter.Discord;

internal static class DiscordExplorerTranslator
{
    public static ServerReadModel TranslateServer(SocketGuild guild, DateTimeOffset refreshedAt)
    {
        var currentUser = guild.CurrentUser;
        var roles = guild.Roles
            .Select(role => new RoleReadModel(
                role.Id,
                role.Name,
                role.Position,
                MapPermissions(role.Permissions),
                role.Id == guild.Id))
            .OrderBy(role => role.Position)
            .ThenBy(role => role.Id)
            .ToImmutableArray();
        var botRoles = currentUser.Roles
            .Select(role => role.Id)
            .Order()
            .ToImmutableArray();
        var highestRole = currentUser.Roles
            .OrderByDescending(role => role.Position)
            .ThenByDescending(role => role.Id)
            .FirstOrDefault();
        var categories = guild.CategoryChannels.ToDictionary(category => category.Id);
        var channels = guild.Channels
            .Select(channel => TranslateChannel(channel, categories))
            .OrderBy(channel => channel.Kind == ChannelKind.Category ? 0 : 1)
            .ThenBy(channel => channel.Position)
            .ThenBy(channel => channel.Id)
            .ToImmutableArray();

        return new ServerReadModel(
            guild.Id,
            guild.Name,
            guild.IconUrl,
            guild.Description,
            guild.OwnerId,
            guild.CreatedAt,
            guild.MemberCount,
            channels.Count(channel => channel.Kind == ChannelKind.Category),
            channels.Count(channel => channel.Kind is ChannelKind.Text or ChannelKind.Announcement),
            channels.Count(channel => channel.Kind == ChannelKind.Voice),
            channels.Count(channel => channel.Kind is ChannelKind.Forum or ChannelKind.Media),
            channels.Count(channel => channel.Kind == ChannelKind.Stage),
            roles.Length,
            guild.Emotes.Count,
            guild.PremiumTier.ToString(),
            guild.PremiumSubscriptionCount,
            currentUser.Nickname,
            highestRole?.Name,
            highestRole?.Position,
            currentUser.Id,
            botRoles,
            roles,
            channels,
            guild.IsConnected ? ServerAvailability.Available : ServerAvailability.Unavailable,
            refreshedAt);
    }

    private static ChannelReadModel TranslateChannel(
        SocketGuildChannel channel,
        Dictionary<ulong, SocketCategoryChannel> categories)
    {
        var channelType = channel.GetChannelType();
        var kind = MapChannelKind(channelType);
        var categoryId = channel is INestedChannel nested ? nested.CategoryId : null;
        categories.TryGetValue(categoryId ?? 0, out var category);
        var overwrites = TranslateOverwrites(channel.PermissionOverwrites);
        ImmutableArray<PermissionOverwriteReadModel>? categoryOverwrites = category is null
            ? null
            : TranslateOverwrites(category.PermissionOverwrites);
        var synchronized = PermissionSynchronization.AreSynchronized(
            categoryId,
            overwrites,
            categoryOverwrites);

        string? topic = null;
        bool? isNsfw = null;
        int? slowModeSeconds = null;
        int? defaultAutoArchiveMinutes = null;
        int? bitrate = null;
        int? userLimit = null;
        string? regionOverride = null;
        int? connectedUserCount = null;
        var tags = ImmutableArray<string>.Empty;
        string? defaultReaction = null;
        string? defaultSortOrder = null;
        string? defaultLayout = null;

        if (channel is SocketForumChannel forum)
        {
            topic = forum.Topic;
            isNsfw = forum.IsNsfw;
            defaultAutoArchiveMinutes = (int)forum.DefaultAutoArchiveDuration;
            tags = forum.Tags
                .Select(tag => tag.Name)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();
            defaultReaction = forum.DefaultReactionEmoji?.ToString();
            defaultSortOrder = forum.DefaultSortOrder?.ToString();
            defaultLayout = forum.DefaultLayout.ToString();
        }
        else if (channel is SocketTextChannel text)
        {
            topic = text.Topic;
            isNsfw = text.IsNsfw;
            slowModeSeconds = TryGet(() => text.SlowModeInterval);
            defaultAutoArchiveMinutes = (int)text.DefaultArchiveDuration;
        }

        if (channel is SocketVoiceChannel voice)
        {
            bitrate = voice.Bitrate;
            userLimit = voice.UserLimit;
            regionOverride = voice.RTCRegion;
            connectedUserCount = voice.ConnectedUsers.Count;
        }

        return new ChannelReadModel(
            channel.Id,
            channel.Name,
            kind,
            channelType?.ToString() ?? channel.GetType().Name,
            channel.Position,
            channel.CreatedAt,
            categoryId,
            category?.Name,
            synchronized,
            overwrites,
            topic,
            isNsfw,
            slowModeSeconds,
            defaultAutoArchiveMinutes,
            bitrate,
            userLimit,
            regionOverride,
            connectedUserCount,
            tags,
            defaultReaction,
            defaultSortOrder,
            defaultLayout);
    }

    private static ImmutableArray<PermissionOverwriteReadModel> TranslateOverwrites(
        IEnumerable<Overwrite> overwrites) =>
        overwrites
            .Select(overwrite => new PermissionOverwriteReadModel(
                overwrite.TargetId,
                overwrite.TargetType == PermissionTarget.Role
                    ? PermissionTargetKind.Role
                    : PermissionTargetKind.User,
                overwrite.Permissions.AllowValue,
                overwrite.Permissions.DenyValue,
                MapPermissions(new GuildPermissions(overwrite.Permissions.AllowValue)),
                MapPermissions(new GuildPermissions(overwrite.Permissions.DenyValue))))
            .OrderBy(overwrite => overwrite.TargetId)
            .ThenBy(overwrite => overwrite.TargetType)
            .ToImmutableArray();

    private static ChannelKind MapChannelKind(ChannelType? channelType) =>
        channelType switch
        {
            ChannelType.Category => ChannelKind.Category,
            ChannelType.Text => ChannelKind.Text,
            ChannelType.News => ChannelKind.Announcement,
            ChannelType.Voice => ChannelKind.Voice,
            ChannelType.Stage => ChannelKind.Stage,
            ChannelType.Forum => ChannelKind.Forum,
            ChannelType.Media => ChannelKind.Media,
            ChannelType.NewsThread or ChannelType.PublicThread or ChannelType.PrivateThread =>
                ChannelKind.Thread,
            _ => ChannelKind.Other
        };

    private static PermissionBits MapPermissions(GuildPermissions permissions)
    {
        var result = PermissionBits.None;
        Add(permissions.Administrator, PermissionBits.Administrator);
        Add(permissions.ManageChannels, PermissionBits.ManageChannels);
        Add(permissions.ManageRoles, PermissionBits.ManageRoles);
        Add(permissions.ManageGuild, PermissionBits.ManageServer);
        Add(permissions.ViewAuditLog, PermissionBits.ViewAuditLog);
        Add(permissions.ManageWebhooks, PermissionBits.ManageWebhooks);
        Add(permissions.KickMembers, PermissionBits.KickMembers);
        Add(permissions.BanMembers, PermissionBits.BanMembers);
        Add(permissions.ModerateMembers, PermissionBits.ModerateMembers);
        Add(permissions.ViewChannel, PermissionBits.ViewChannel);
        Add(permissions.SendMessages, PermissionBits.SendMessages);
        Add(permissions.SendMessagesInThreads, PermissionBits.SendMessagesInThreads);
        Add(permissions.CreatePublicThreads, PermissionBits.CreatePublicThreads);
        Add(permissions.CreatePrivateThreads, PermissionBits.CreatePrivateThreads);
        Add(permissions.EmbedLinks, PermissionBits.EmbedLinks);
        Add(permissions.AttachFiles, PermissionBits.AttachFiles);
        Add(permissions.AddReactions, PermissionBits.AddReactions);
        Add(permissions.ManageMessages, PermissionBits.ManageMessages);
        Add(permissions.ReadMessageHistory, PermissionBits.ReadMessageHistory);
        Add(permissions.MentionEveryone, PermissionBits.MentionEveryone);
        Add(permissions.Connect, PermissionBits.Connect);
        Add(permissions.Speak, PermissionBits.Speak);
        Add(permissions.Stream, PermissionBits.Stream);
        Add(permissions.UseVAD, PermissionBits.UseVoiceActivity);
        Add(permissions.PrioritySpeaker, PermissionBits.PrioritySpeaker);
        Add(permissions.MuteMembers, PermissionBits.MuteMembers);
        Add(permissions.DeafenMembers, PermissionBits.DeafenMembers);
        Add(permissions.MoveMembers, PermissionBits.MoveMembers);
        Add(permissions.RequestToSpeak, PermissionBits.RequestToSpeak);
        return result;

        void Add(bool enabled, PermissionBits permission)
        {
            if (enabled)
            {
                result |= permission;
            }
        }
    }

    private static int? TryGet(Func<int> accessor)
    {
        try
        {
            return accessor();
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
