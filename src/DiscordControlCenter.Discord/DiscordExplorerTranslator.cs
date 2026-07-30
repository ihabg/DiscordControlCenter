using System.Collections.Immutable;
using Discord;
using Discord.WebSocket;
using DiscordControlCenter.Core.Explorer;

namespace DiscordControlCenter.Discord;

internal static class DiscordExplorerTranslator
{
    public static ServerReadModel TranslateServer(
        SocketGuild guild,
        DateTimeOffset refreshedAt,
        bool fullMemberAccessEnabled)
    {
        var currentUser = guild.CurrentUser;
        var roles = guild.Roles
            .Select(role => TranslateRole(role))
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
        var visibleUsers = fullMemberAccessEnabled && guild.HasAllMembers
            ? guild.Users.Cast<IGuildUser>()
            : guild.Users
                .Where(user =>
                    user.Id == currentUser.Id
                    || user.VoiceChannel is not null)
                .Cast<IGuildUser>();
        var members = visibleUsers
            .Select(user => TranslateMember(user, guild))
            .DistinctBy(member => member.Id)
            .OrderBy(member => member.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(member => member.Id)
            .ToImmutableArray();
        var memberSnapshot = new MemberCollectionReadModel(
            fullMemberAccessEnabled
                ? guild.HasAllMembers
                    ? DataCompleteness.Complete
                    : DataCompleteness.Partial
                : DataCompleteness.Limited,
            fullMemberAccessEnabled,
            members,
            guild.MemberCount,
            refreshedAt,
            null);
        roles = ApplyMemberCounts(roles, memberSnapshot);

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
            refreshedAt)
        {
            Members = memberSnapshot
        };
    }

    public static MemberReadModel TranslateMember(IGuildUser user, SocketGuild guild)
    {
        var roles = guild.Roles
            .Where(role => user.RoleIds.Contains(role.Id))
            .OrderByDescending(role => role.Position)
            .ThenByDescending(role => role.Id)
            .ToArray();
        var highestRole = roles.FirstOrDefault();
        var voiceState = user.VoiceChannel is null
            ? null
            : TranslateVoiceState(user);
        return new MemberReadModel(
            user.Id,
            user.Username,
            user.GlobalName,
            user.Nickname,
            user.DisplayName,
            user.GetDisplayAvatarUrl(ImageFormat.Auto, 128),
            user.IsBot,
            user.CreatedAt,
            user.JoinedAt,
            user.RoleIds
                .Where(roleId => roleId != guild.Id)
                .Order()
                .ToImmutableArray(),
            highestRole?.Name,
            highestRole?.Position,
            user.PremiumSince,
            user.IsPending,
            user.TimedOutUntil,
            voiceState,
            RolesAreComplete: true);
    }

    public static VoiceStateReadModel TranslateVoiceState(IGuildUser user)
    {
        var channel = user.VoiceChannel
            ?? throw new InvalidOperationException("The user is not connected to a guild voice channel.");
        return new VoiceStateReadModel(
            user.Id,
            user.DisplayName,
            user.IsBot,
            channel.Id,
            channel.Name,
            user.IsSelfMuted,
            user.IsSelfDeafened,
            user.IsMuted,
            user.IsDeafened,
            user.IsStreaming,
            user.IsVideoing,
            user.IsSuppressed,
            user.RequestToSpeakTimestamp);
    }

    public static VoiceStateReadModel? TranslateVoiceState(
        SocketGuildUser user,
        SocketVoiceState state)
    {
        var channel = state.VoiceChannel;
        return channel is null
            ? null
            : new VoiceStateReadModel(
                user.Id,
                user.DisplayName,
                user.IsBot,
                channel.Id,
                channel.Name,
                state.IsSelfMuted,
                state.IsSelfDeafened,
                state.IsMuted,
                state.IsDeafened,
                state.IsStreaming,
                state.IsVideoing,
                state.IsSuppressed,
                state.RequestToSpeakTimestamp);
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
            defaultLayout)
        {
            VoiceMembers = channel is SocketVoiceChannel voiceChannel
                ? voiceChannel.ConnectedUsers
                    .Select(TranslateVoiceState)
                    .OrderBy(member => member.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(member => member.UserId)
                    .ToImmutableArray()
                : ImmutableArray<VoiceStateReadModel>.Empty
        };
    }

    private static RoleReadModel TranslateRole(SocketRole role)
    {
        var permissions = MapPermissions(role.Permissions);
        var tags = role.Tags;
        var tagParts = new List<string>();
        if (tags?.BotId is ulong botId)
        {
            tagParts.Add($"Bot managed ({botId})");
        }

        if (tags?.IntegrationId is ulong integrationId)
        {
            tagParts.Add($"Integration ({integrationId})");
        }

        if (tags?.IsPremiumSubscriberRole == true)
        {
            tagParts.Add("Booster role");
        }

        if (tags?.SubscriptionListingId is ulong subscriptionId)
        {
            tagParts.Add($"Subscription ({subscriptionId})");
        }

        return new RoleReadModel(
            role.Id,
            role.Name,
            role.Position,
            permissions,
            role.Id == role.Guild.Id)
        {
            ColorRaw = role.Colors.PrimaryColor.RawValue,
            IsHoisted = role.IsHoisted,
            IsMentionable = role.IsMentionable,
            IsManaged = role.IsManaged,
            IsBotManaged = tags?.BotId is not null,
            IconUrl = string.IsNullOrWhiteSpace(role.Icon)
                ? null
                : $"https://cdn.discordapp.com/role-icons/{role.Id}/{role.Icon}.png?size=128",
            UnicodeEmoji = role.Emoji?.ToString(),
            TagsSummary = tagParts.Count == 0 ? null : string.Join(", ", tagParts),
            PermissionRaw = role.Permissions.RawValue,
            PermissionNames = role.Permissions
                .ToList()
                .Select(permission => permission.ToString())
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray()
        };
    }

    private static ImmutableArray<RoleReadModel> ApplyMemberCounts(
        ImmutableArray<RoleReadModel> roles,
        MemberCollectionReadModel members)
    {
        var exact = members.Completeness == DataCompleteness.Complete;
        return roles
            .Select(role => role with
            {
                MemberCount = exact || members.Members.Length > 0
                    ? members.Members.Count(member =>
                        role.IsEveryone || member.RoleIds.Contains(role.Id))
                    : null,
                MemberCountCompleteness = exact
                    ? DataCompleteness.Complete
                    : members.Members.Length > 0
                        ? DataCompleteness.Partial
                        : DataCompleteness.Unavailable
            })
            .ToImmutableArray();
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
        Add(GuildPermission.Administrator, PermissionBits.Administrator);
        Add(GuildPermission.ManageChannels, PermissionBits.ManageChannels);
        Add(GuildPermission.ManageRoles, PermissionBits.ManageRoles);
        Add(GuildPermission.ManageGuild, PermissionBits.ManageServer);
        Add(GuildPermission.ViewAuditLog, PermissionBits.ViewAuditLog);
        Add(GuildPermission.ManageWebhooks, PermissionBits.ManageWebhooks);
        Add(GuildPermission.KickMembers, PermissionBits.KickMembers);
        Add(GuildPermission.BanMembers, PermissionBits.BanMembers);
        Add(GuildPermission.ModerateMembers, PermissionBits.ModerateMembers);
        Add(GuildPermission.ViewChannel, PermissionBits.ViewChannel);
        Add(GuildPermission.SendMessages, PermissionBits.SendMessages);
        Add(GuildPermission.SendMessagesInThreads, PermissionBits.SendMessagesInThreads);
        Add(GuildPermission.CreatePublicThreads, PermissionBits.CreatePublicThreads);
        Add(GuildPermission.CreatePrivateThreads, PermissionBits.CreatePrivateThreads);
        Add(GuildPermission.EmbedLinks, PermissionBits.EmbedLinks);
        Add(GuildPermission.AttachFiles, PermissionBits.AttachFiles);
        Add(GuildPermission.AddReactions, PermissionBits.AddReactions);
        Add(GuildPermission.ManageMessages, PermissionBits.ManageMessages);
        Add(GuildPermission.ReadMessageHistory, PermissionBits.ReadMessageHistory);
        Add(GuildPermission.MentionEveryone, PermissionBits.MentionEveryone);
        Add(GuildPermission.Connect, PermissionBits.Connect);
        Add(GuildPermission.Speak, PermissionBits.Speak);
        Add(GuildPermission.Stream, PermissionBits.Stream);
        Add(GuildPermission.UseVAD, PermissionBits.UseVoiceActivity);
        Add(GuildPermission.PrioritySpeaker, PermissionBits.PrioritySpeaker);
        Add(GuildPermission.MuteMembers, PermissionBits.MuteMembers);
        Add(GuildPermission.DeafenMembers, PermissionBits.DeafenMembers);
        Add(GuildPermission.MoveMembers, PermissionBits.MoveMembers);
        Add(GuildPermission.RequestToSpeak, PermissionBits.RequestToSpeak);
        Add(GuildPermission.CreateInstantInvite, PermissionBits.CreateInvites);
        Add(GuildPermission.ManageEvents, PermissionBits.ManageEvents);
        Add(GuildPermission.ManageEmojisAndStickers, PermissionBits.ManageExpressions);
        Add(GuildPermission.UseExternalEmojis, PermissionBits.UseExternalEmojis);
        Add(GuildPermission.UseExternalStickers, PermissionBits.UseExternalStickers);
        Add(GuildPermission.ManageThreads, PermissionBits.ManageThreads);
        Add(GuildPermission.UseSoundboard, PermissionBits.UseSoundboard);
        Add(GuildPermission.UseExternalSounds, PermissionBits.UseExternalSounds);
        Add(GuildPermission.ManageNicknames, PermissionBits.ManageNicknames);
        Add(GuildPermission.ChangeNickname, PermissionBits.ChangeNickname);
        return result;

        void Add(GuildPermission discordPermission, PermissionBits permission)
        {
            if (permissions.Has(discordPermission))
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
