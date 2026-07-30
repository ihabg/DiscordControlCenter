using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using DiscordControlCenter.Core.Explorer;

namespace DiscordControlCenter.Application.Explorer;

public sealed class PermissionResolutionService : IPermissionResolutionService
{
    private const string GeneralGroup = "General";
    private const string TextGroup = "Text";
    private const string VoiceGroup = "Voice";
    private const string ModerationGroup = "Moderation";
    private const string ServerGroup = "Server";
    private static readonly PermissionBits AllPermissions = Enum
        .GetValues<PermissionBits>()
        .Aggregate(PermissionBits.None, (current, permission) => current | permission);
    private static readonly Definition[] Definitions =
    [
        new(GeneralGroup, "View Channel", PermissionBits.ViewChannel, Scope.AllChannels),
        new(GeneralGroup, "Manage Channel", PermissionBits.ManageChannels, Scope.AllChannels),
        new(GeneralGroup, "Manage Server", PermissionBits.ManageServer, Scope.Always),
        new(GeneralGroup, "Manage Roles", PermissionBits.ManageRoles, Scope.Always),
        new(GeneralGroup, "Manage Webhooks", PermissionBits.ManageWebhooks, Scope.Text),
        new(GeneralGroup, "View Audit Log", PermissionBits.ViewAuditLog, Scope.Always),
        new(GeneralGroup, "Create Invites", PermissionBits.CreateInvites, Scope.AllChannels),
        new(GeneralGroup, "Manage Events", PermissionBits.ManageEvents, Scope.Always),
        new(GeneralGroup, "Manage Expressions", PermissionBits.ManageExpressions, Scope.Always),
        new(TextGroup, "Send Messages", PermissionBits.SendMessages, Scope.Text),
        new(TextGroup, "Send Messages in Threads", PermissionBits.SendMessagesInThreads, Scope.Text),
        new(TextGroup, "Create Public Threads", PermissionBits.CreatePublicThreads, Scope.Text),
        new(TextGroup, "Create Private Threads", PermissionBits.CreatePrivateThreads, Scope.Text),
        new(TextGroup, "Embed Links", PermissionBits.EmbedLinks, Scope.Text),
        new(TextGroup, "Attach Files", PermissionBits.AttachFiles, Scope.Text),
        new(TextGroup, "Add Reactions", PermissionBits.AddReactions, Scope.Text),
        new(TextGroup, "Use External Emojis", PermissionBits.UseExternalEmojis, Scope.Text),
        new(TextGroup, "Use External Stickers", PermissionBits.UseExternalStickers, Scope.Text),
        new(TextGroup, "Read Message History", PermissionBits.ReadMessageHistory, Scope.Text),
        new(TextGroup, "Mention Everyone", PermissionBits.MentionEveryone, Scope.Text),
        new(TextGroup, "Manage Messages", PermissionBits.ManageMessages, Scope.Text),
        new(TextGroup, "Manage Threads", PermissionBits.ManageThreads, Scope.Text),
        new(VoiceGroup, "Connect", PermissionBits.Connect, Scope.Voice),
        new(VoiceGroup, "Speak", PermissionBits.Speak, Scope.Voice),
        new(VoiceGroup, "Stream", PermissionBits.Stream, Scope.Voice),
        new(VoiceGroup, "Use Voice Activity", PermissionBits.UseVoiceActivity, Scope.Voice),
        new(VoiceGroup, "Priority Speaker", PermissionBits.PrioritySpeaker, Scope.Voice),
        new(VoiceGroup, "Mute Members", PermissionBits.MuteMembers, Scope.Voice),
        new(VoiceGroup, "Deafen Members", PermissionBits.DeafenMembers, Scope.Voice),
        new(VoiceGroup, "Move Members", PermissionBits.MoveMembers, Scope.Voice),
        new(VoiceGroup, "Request to Speak", PermissionBits.RequestToSpeak, Scope.Stage),
        new(VoiceGroup, "Use Soundboard", PermissionBits.UseSoundboard, Scope.Voice),
        new(VoiceGroup, "Use External Sounds", PermissionBits.UseExternalSounds, Scope.Voice),
        new(ModerationGroup, "Kick Members", PermissionBits.KickMembers, Scope.Always),
        new(ModerationGroup, "Ban Members", PermissionBits.BanMembers, Scope.Always),
        new(ModerationGroup, "Moderate Members", PermissionBits.ModerateMembers, Scope.Always),
        new(ModerationGroup, "Manage Nicknames", PermissionBits.ManageNicknames, Scope.Always),
        new(ModerationGroup, "Change Nickname", PermissionBits.ChangeNickname, Scope.Always)
    ];
    private static readonly Definition[] ServerDefinitions =
    [
        new(ServerGroup, "Administrator", PermissionBits.Administrator, Scope.Always),
        new(ServerGroup, "Manage Channels", PermissionBits.ManageChannels, Scope.Always),
        new(ServerGroup, "Manage Roles", PermissionBits.ManageRoles, Scope.Always),
        new(ServerGroup, "Manage Server", PermissionBits.ManageServer, Scope.Always),
        new(ServerGroup, "View Audit Log", PermissionBits.ViewAuditLog, Scope.Always),
        new(ServerGroup, "Manage Webhooks", PermissionBits.ManageWebhooks, Scope.Always),
        new(ServerGroup, "Kick Members", PermissionBits.KickMembers, Scope.Always),
        new(ServerGroup, "Ban Members", PermissionBits.BanMembers, Scope.Always),
        new(ServerGroup, "Moderate Members", PermissionBits.ModerateMembers, Scope.Always),
        new(ServerGroup, "Connect", PermissionBits.Connect, Scope.Always),
        new(ServerGroup, "Speak", PermissionBits.Speak, Scope.Always),
        new(ServerGroup, "Move Members", PermissionBits.MoveMembers, Scope.Always),
        new(ServerGroup, "Mute Members", PermissionBits.MuteMembers, Scope.Always),
        new(ServerGroup, "Deafen Members", PermissionBits.DeafenMembers, Scope.Always)
    ];
    private readonly ConcurrentDictionary<PermissionCacheKey, PermissionResolution> _cache = new();

    public int CachedEntryCount => _cache.Count;

    public PermissionResolution ResolveServer(
        Guid botProfileId,
        long snapshotVersion,
        ServerReadModel server) =>
        _cache.GetOrAdd(
            new PermissionCacheKey(botProfileId, server.Id, 0, snapshotVersion, SubjectKind.Bot, server.BotUserId),
            _ => BuildResolution(
                server,
                null,
                server.BotRoleIds,
                server.BotUserId,
                rolesComplete: true,
                "Bot member overwrite",
                serverOnly: true));

    public PermissionResolution ResolveChannel(
        Guid botProfileId,
        long snapshotVersion,
        ServerReadModel server,
        ChannelReadModel channel) =>
        _cache.GetOrAdd(
            new PermissionCacheKey(
                botProfileId,
                server.Id,
                channel.Id,
                snapshotVersion,
                SubjectKind.Bot,
                server.BotUserId),
            _ => BuildResolution(
                server,
                channel,
                server.BotRoleIds,
                server.BotUserId,
                rolesComplete: true,
                "Bot member overwrite",
                serverOnly: false));

    public PermissionResolution ResolveMember(
        Guid botProfileId,
        long snapshotVersion,
        ServerReadModel server,
        MemberReadModel member,
        ChannelReadModel? channel) =>
        _cache.GetOrAdd(
            new PermissionCacheKey(
                botProfileId,
                server.Id,
                channel?.Id ?? 0,
                snapshotVersion,
                SubjectKind.Member,
                member.Id),
            _ => BuildResolution(
                server,
                channel,
                member.RoleIds,
                member.Id,
                member.RolesAreComplete,
                "Member-specific overwrite",
                serverOnly: false));

    public PermissionResolution ResolveRole(
        Guid botProfileId,
        long snapshotVersion,
        ServerReadModel server,
        RoleReadModel role,
        ChannelReadModel? channel) =>
        _cache.GetOrAdd(
            new PermissionCacheKey(
                botProfileId,
                server.Id,
                channel?.Id ?? 0,
                snapshotVersion,
                SubjectKind.Role,
                role.Id),
            _ => BuildResolution(
                server,
                channel,
                role.IsEveryone ? [] : [role.Id],
                null,
                rolesComplete: true,
                null,
                serverOnly: false));

    public PermissionComparison Compare(
        PermissionResolution first,
        PermissionResolution second)
    {
        var secondByPermission = second.Permissions.ToDictionary(item => item.Permission);
        var items = first.Permissions
            .Where(item => secondByPermission.ContainsKey(item.Permission))
            .Select(
                item =>
                {
                    var other = secondByPermission[item.Permission];
                    return new PermissionComparisonItem(
                        item.Group,
                        item.Name,
                        item.Permission,
                        item.Status,
                        other.Status,
                        Compare(item.Status, other.Status));
                })
            .ToArray();
        return new PermissionComparison(new ReadOnlyCollection<PermissionComparisonItem>(items));
    }

    public void Invalidate(Guid botProfileId, ulong? serverId = null)
    {
        foreach (var key in _cache.Keys)
        {
            if (key.BotProfileId == botProfileId
                && (serverId is null || key.ServerId == serverId.Value))
            {
                _cache.TryRemove(key, out _);
            }
        }
    }

    private static PermissionResolution BuildResolution(
        ServerReadModel server,
        ChannelReadModel? channel,
        IEnumerable<ulong> roleIds,
        ulong? memberId,
        bool rolesComplete,
        string? memberOverwriteSource,
        bool serverOnly)
    {
        var calculation = CalculateBase(server, roleIds, rolesComplete);
        if (channel is not null && !calculation.Administrator && calculation.IsComplete)
        {
            calculation = ApplyChannelOverwrites(
                server,
                channel,
                roleIds,
                memberId,
                memberOverwriteSource,
                calculation);
        }

        var definitions = serverOnly ? ServerDefinitions : Definitions;
        var permissions = definitions
            .Select(definition => Resolve(definition, channel, calculation))
            .ToArray();
        return new PermissionResolution(
            calculation.Effective,
            new ReadOnlyCollection<PermissionResult>(permissions));
    }

    private static Calculation CalculateBase(
        ServerReadModel server,
        IEnumerable<ulong> roleIds,
        bool rolesComplete)
    {
        var everyone = server.Roles.FirstOrDefault(role => role.IsEveryone);
        var effective = everyone?.Permissions ?? PermissionBits.None;
        var sources = new Dictionary<PermissionBits, string>();
        foreach (var permission in EnumerateFlags(effective))
        {
            sources[permission] = "Base @everyone permissions";
        }

        var roleIdSet = roleIds.ToHashSet();
        foreach (var role in server.Roles.Where(role => roleIdSet.Contains(role.Id)))
        {
            effective |= role.Permissions;
            foreach (var permission in EnumerateFlags(role.Permissions))
            {
                sources[permission] = "Aggregated role permissions";
            }
        }

        var administrator = effective.Has(PermissionBits.Administrator);
        if (administrator)
        {
            effective = AllPermissions;
            foreach (var permission in EnumerateFlags(AllPermissions))
            {
                sources[permission] = "Administrator";
            }
        }

        return new Calculation(effective, administrator, rolesComplete && everyone is not null, sources);
    }

    private static Calculation ApplyChannelOverwrites(
        ServerReadModel server,
        ChannelReadModel channel,
        IEnumerable<ulong> roleIds,
        ulong? memberId,
        string? memberOverwriteSource,
        Calculation calculation)
    {
        var effective = calculation.Effective;
        var sources = new Dictionary<PermissionBits, string>(calculation.Sources);
        var everyone = server.Roles.First(role => role.IsEveryone);
        var everyoneOverwrite = channel.PermissionOverwrites.FirstOrDefault(item =>
            item.TargetType == PermissionTargetKind.Role && item.TargetId == everyone.Id);
        if (everyoneOverwrite is not null)
        {
            effective = ApplyOverwrite(
                effective,
                everyoneOverwrite.Denied,
                everyoneOverwrite.Allowed,
                sources,
                channel.IsPermissionSynchronized == true
                    ? "Category @everyone overwrite"
                    : "@everyone channel overwrite");
        }

        var roleIdSet = roleIds.ToHashSet();
        var roleOverwrites = channel.PermissionOverwrites
            .Where(overwrite =>
                overwrite.TargetType == PermissionTargetKind.Role
                && overwrite.TargetId != everyone.Id
                && roleIdSet.Contains(overwrite.TargetId))
            .ToArray();
        if (roleOverwrites.Length > 0)
        {
            var denied = roleOverwrites.Aggregate(
                PermissionBits.None,
                (current, overwrite) => current | overwrite.Denied);
            var allowed = roleOverwrites.Aggregate(
                PermissionBits.None,
                (current, overwrite) => current | overwrite.Allowed);
            effective = ApplyOverwrite(
                effective,
                denied,
                allowed,
                sources,
                channel.IsPermissionSynchronized == true
                    ? "Category role overwrites"
                    : "Role overwrite");
        }

        if (memberId is ulong id)
        {
            var memberOverwrite = channel.PermissionOverwrites.FirstOrDefault(overwrite =>
                overwrite.TargetType == PermissionTargetKind.User
                && overwrite.TargetId == id);
            if (memberOverwrite is not null)
            {
                effective = ApplyOverwrite(
                    effective,
                    memberOverwrite.Denied,
                    memberOverwrite.Allowed,
                    sources,
                    channel.IsPermissionSynchronized == true
                        ? $"Category {memberOverwriteSource?.ToLowerInvariant()}"
                        : memberOverwriteSource ?? "Member-specific overwrite");
            }
        }

        return new Calculation(effective, false, true, sources);
    }

    private static PermissionBits ApplyOverwrite(
        PermissionBits effective,
        PermissionBits denied,
        PermissionBits allowed,
        Dictionary<PermissionBits, string> sources,
        string source)
    {
        foreach (var permission in EnumerateFlags(denied | allowed))
        {
            sources[permission] = source;
        }

        return (effective & ~denied) | allowed;
    }

    private static PermissionResult Resolve(
        Definition definition,
        ChannelReadModel? channel,
        Calculation calculation)
    {
        if (!IsApplicable(definition.Scope, channel))
        {
            return new PermissionResult(
                definition.Group,
                definition.Name,
                definition.Permission,
                PermissionStatus.NotApplicable,
                null);
        }

        if (!calculation.IsComplete)
        {
            return new PermissionResult(
                definition.Group,
                definition.Name,
                definition.Permission,
                PermissionStatus.Unknown,
                "Complete member-role data is unavailable");
        }

        var allowed = calculation.Effective.Has(definition.Permission);
        var status = calculation.Administrator
            && definition.Permission != PermissionBits.Administrator
                ? PermissionStatus.AllowedThroughAdministrator
                : allowed
                    ? PermissionStatus.Allowed
                    : PermissionStatus.Denied;
        calculation.Sources.TryGetValue(definition.Permission, out var source);
        return new PermissionResult(
            definition.Group,
            definition.Name,
            definition.Permission,
            status,
            source);
    }

    private static bool IsApplicable(Scope scope, ChannelReadModel? channel) =>
        scope switch
        {
            Scope.Always => true,
            Scope.AllChannels => channel is not null,
            Scope.Text => channel?.Kind is ChannelKind.Text
                or ChannelKind.Announcement
                or ChannelKind.Forum
                or ChannelKind.Media
                or ChannelKind.Thread,
            Scope.Voice => channel?.Kind is ChannelKind.Voice or ChannelKind.Stage,
            Scope.Stage => channel?.Kind == ChannelKind.Stage,
            _ => false
        };

    private static PermissionComparisonStatus Compare(
        PermissionStatus first,
        PermissionStatus second)
    {
        if (first == PermissionStatus.Unknown || second == PermissionStatus.Unknown)
        {
            return PermissionComparisonStatus.Unknown;
        }

        if (first == PermissionStatus.NotApplicable || second == PermissionStatus.NotApplicable)
        {
            return PermissionComparisonStatus.NotApplicable;
        }

        var firstAllowed = first is PermissionStatus.Allowed
            or PermissionStatus.AllowedThroughAdministrator;
        var secondAllowed = second is PermissionStatus.Allowed
            or PermissionStatus.AllowedThroughAdministrator;
        return (firstAllowed, secondAllowed) switch
        {
            (true, true) => PermissionComparisonStatus.BothAllowed,
            (true, false) => PermissionComparisonStatus.FirstOnly,
            (false, true) => PermissionComparisonStatus.SecondOnly,
            _ => PermissionComparisonStatus.BothDenied
        };
    }

    private static IEnumerable<PermissionBits> EnumerateFlags(PermissionBits permissions)
    {
        foreach (var permission in Enum.GetValues<PermissionBits>())
        {
            if (permission != PermissionBits.None && permissions.Has(permission))
            {
                yield return permission;
            }
        }
    }

    private readonly record struct PermissionCacheKey(
        Guid BotProfileId,
        ulong ServerId,
        ulong ChannelId,
        long SnapshotVersion,
        SubjectKind SubjectKind,
        ulong SubjectId);

    private sealed record Calculation(
        PermissionBits Effective,
        bool Administrator,
        bool IsComplete,
        Dictionary<PermissionBits, string> Sources);

    private sealed record Definition(
        string Group,
        string Name,
        PermissionBits Permission,
        Scope Scope);

    private enum Scope
    {
        Always,
        AllChannels,
        Text,
        Voice,
        Stage
    }

    private enum SubjectKind
    {
        Bot,
        Member,
        Role
    }
}
