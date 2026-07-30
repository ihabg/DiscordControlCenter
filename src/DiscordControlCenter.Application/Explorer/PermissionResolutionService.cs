using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using DiscordControlCenter.Core.Explorer;

namespace DiscordControlCenter.Application.Explorer;

public sealed class PermissionResolutionService : IPermissionResolutionService
{
    private const string GeneralGroup = "General";
    private const string TextGroup = "Text";
    private const string VoiceGroup = "Voice";
    private const string ServerGroup = "Server";
    private static readonly PermissionBits AllPermissions = Enum
        .GetValues<PermissionBits>()
        .Aggregate(PermissionBits.None, (current, permission) => current | permission);
    private readonly ConcurrentDictionary<PermissionCacheKey, PermissionResolution> _cache = new();

    public int CachedEntryCount => _cache.Count;

    public PermissionResolution ResolveServer(
        Guid botProfileId,
        long snapshotVersion,
        ServerReadModel server) =>
        _cache.GetOrAdd(
            new PermissionCacheKey(botProfileId, server.Id, 0, snapshotVersion),
            _ => BuildServerResolution(server));

    public PermissionResolution ResolveChannel(
        Guid botProfileId,
        long snapshotVersion,
        ServerReadModel server,
        ChannelReadModel channel) =>
        _cache.GetOrAdd(
            new PermissionCacheKey(botProfileId, server.Id, channel.Id, snapshotVersion),
            _ => BuildChannelResolution(server, channel));

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

    private static PermissionResolution BuildServerResolution(ServerReadModel server)
    {
        var calculation = CalculateBase(server);
        var definitions = new[]
        {
            ("Administrator", PermissionBits.Administrator),
            ("Manage Channels", PermissionBits.ManageChannels),
            ("Manage Roles", PermissionBits.ManageRoles),
            ("Manage Server", PermissionBits.ManageServer),
            ("View Audit Log", PermissionBits.ViewAuditLog),
            ("Manage Webhooks", PermissionBits.ManageWebhooks),
            ("Kick Members", PermissionBits.KickMembers),
            ("Ban Members", PermissionBits.BanMembers),
            ("Moderate Members", PermissionBits.ModerateMembers),
            ("Connect", PermissionBits.Connect),
            ("Speak", PermissionBits.Speak),
            ("Move Members", PermissionBits.MoveMembers),
            ("Mute Members", PermissionBits.MuteMembers),
            ("Deafen Members", PermissionBits.DeafenMembers)
        };
        var permissions = definitions
            .Select(definition => Resolve(
                ServerGroup,
                definition.Item1,
                definition.Item2,
                calculation,
                applicable: true))
            .ToArray();
        return new PermissionResolution(
            calculation.Effective,
            new ReadOnlyCollection<PermissionResult>(permissions));
    }

    private static PermissionResolution BuildChannelResolution(
        ServerReadModel server,
        ChannelReadModel channel)
    {
        var calculation = CalculateBase(server);
        if (!calculation.Administrator)
        {
            calculation = ApplyChannelOverwrites(server, channel, calculation);
        }

        var isText = channel.Kind is ChannelKind.Text
            or ChannelKind.Announcement
            or ChannelKind.Forum
            or ChannelKind.Media
            or ChannelKind.Thread;
        var isVoice = channel.Kind is ChannelKind.Voice or ChannelKind.Stage;
        var definitions = new[]
        {
            new Definition(GeneralGroup, "View Channel", PermissionBits.ViewChannel, true),
            new Definition(GeneralGroup, "Manage Channel", PermissionBits.ManageChannels, true),
            new Definition(GeneralGroup, "Manage Roles", PermissionBits.ManageRoles, true),
            new Definition(GeneralGroup, "Manage Webhooks", PermissionBits.ManageWebhooks, isText),
            new Definition(GeneralGroup, "View Audit Log", PermissionBits.ViewAuditLog, true),
            new Definition(TextGroup, "Send Messages", PermissionBits.SendMessages, isText),
            new Definition(TextGroup, "Send Messages in Threads", PermissionBits.SendMessagesInThreads, isText),
            new Definition(TextGroup, "Create Public Threads", PermissionBits.CreatePublicThreads, isText),
            new Definition(TextGroup, "Create Private Threads", PermissionBits.CreatePrivateThreads, isText),
            new Definition(TextGroup, "Embed Links", PermissionBits.EmbedLinks, isText),
            new Definition(TextGroup, "Attach Files", PermissionBits.AttachFiles, isText),
            new Definition(TextGroup, "Add Reactions", PermissionBits.AddReactions, isText),
            new Definition(TextGroup, "Manage Messages", PermissionBits.ManageMessages, isText),
            new Definition(TextGroup, "Read Message History", PermissionBits.ReadMessageHistory, isText),
            new Definition(TextGroup, "Mention Everyone", PermissionBits.MentionEveryone, isText),
            new Definition(VoiceGroup, "Connect", PermissionBits.Connect, isVoice),
            new Definition(VoiceGroup, "Speak", PermissionBits.Speak, isVoice),
            new Definition(VoiceGroup, "Stream", PermissionBits.Stream, isVoice),
            new Definition(VoiceGroup, "Use Voice Activity", PermissionBits.UseVoiceActivity, isVoice),
            new Definition(VoiceGroup, "Priority Speaker", PermissionBits.PrioritySpeaker, isVoice),
            new Definition(VoiceGroup, "Mute Members", PermissionBits.MuteMembers, isVoice),
            new Definition(VoiceGroup, "Deafen Members", PermissionBits.DeafenMembers, isVoice),
            new Definition(VoiceGroup, "Move Members", PermissionBits.MoveMembers, isVoice),
            new Definition(VoiceGroup, "Request to Speak", PermissionBits.RequestToSpeak, channel.Kind == ChannelKind.Stage)
        };
        var permissions = definitions
            .Select(definition => Resolve(
                definition.Group,
                definition.Name,
                definition.Permission,
                calculation,
                definition.Applicable))
            .ToArray();
        return new PermissionResolution(
            calculation.Effective,
            new ReadOnlyCollection<PermissionResult>(permissions));
    }

    private static Calculation CalculateBase(ServerReadModel server)
    {
        var everyone = server.Roles.FirstOrDefault(role => role.IsEveryone);
        var effective = everyone?.Permissions ?? PermissionBits.None;
        var sources = new Dictionary<PermissionBits, string>();
        foreach (var permission in EnumerateFlags(effective))
        {
            sources[permission] = "Server role (@everyone)";
        }

        foreach (var role in server.Roles.Where(role => server.BotRoleIds.Contains(role.Id)))
        {
            effective |= role.Permissions;
            foreach (var permission in EnumerateFlags(role.Permissions))
            {
                sources[permission] = "Server role";
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

        return new Calculation(effective, administrator, sources);
    }

    private static Calculation ApplyChannelOverwrites(
        ServerReadModel server,
        ChannelReadModel channel,
        Calculation calculation)
    {
        var effective = calculation.Effective;
        var sources = new Dictionary<PermissionBits, string>(calculation.Sources);
        var everyone = server.Roles.FirstOrDefault(role => role.IsEveryone);
        if (everyone is not null)
        {
            var overwrite = channel.PermissionOverwrites.FirstOrDefault(item =>
                item.TargetType == PermissionTargetKind.Role && item.TargetId == everyone.Id);
            if (overwrite is not null)
            {
                effective = ApplyOverwrite(
                    effective,
                    overwrite.Denied,
                    overwrite.Allowed,
                    sources,
                    channel.IsPermissionSynchronized == true
                        ? "Category inheritance (@everyone overwrite)"
                        : "@everyone overwrite");
            }
        }

        var roleOverwrites = channel.PermissionOverwrites
            .Where(overwrite =>
                overwrite.TargetType == PermissionTargetKind.Role
                && overwrite.TargetId != everyone?.Id
                && server.BotRoleIds.Contains(overwrite.TargetId))
            .ToArray();
        var roleDenied = roleOverwrites.Aggregate(
            PermissionBits.None,
            (current, overwrite) => current | overwrite.Denied);
        var roleAllowed = roleOverwrites.Aggregate(
            PermissionBits.None,
            (current, overwrite) => current | overwrite.Allowed);
        if (roleOverwrites.Length > 0)
        {
            effective = ApplyOverwrite(
                effective,
                roleDenied,
                roleAllowed,
                sources,
                channel.IsPermissionSynchronized == true
                    ? "Category inheritance (role overwrite)"
                    : "Role overwrite");
        }

        var memberOverwrite = channel.PermissionOverwrites.FirstOrDefault(overwrite =>
            overwrite.TargetType == PermissionTargetKind.User
            && overwrite.TargetId == server.BotUserId);
        if (memberOverwrite is not null)
        {
            effective = ApplyOverwrite(
                effective,
                memberOverwrite.Denied,
                memberOverwrite.Allowed,
                sources,
                channel.IsPermissionSynchronized == true
                    ? "Category inheritance (bot member overwrite)"
                    : "Bot member overwrite");
        }

        return new Calculation(effective, false, sources);
    }

    private static PermissionBits ApplyOverwrite(
        PermissionBits effective,
        PermissionBits denied,
        PermissionBits allowed,
        Dictionary<PermissionBits, string> sources,
        string source)
    {
        var changed = denied | allowed;
        foreach (var permission in EnumerateFlags(changed))
        {
            sources[permission] = source;
        }

        return (effective & ~denied) | allowed;
    }

    private static PermissionResult Resolve(
        string group,
        string name,
        PermissionBits permission,
        Calculation calculation,
        bool applicable)
    {
        if (!applicable)
        {
            return new PermissionResult(
                group,
                name,
                permission,
                PermissionStatus.NotApplicable,
                null);
        }

        var allowed = calculation.Effective.Has(permission);
        var status = calculation.Administrator && permission != PermissionBits.Administrator
            ? PermissionStatus.AllowedThroughAdministrator
            : allowed
                ? PermissionStatus.Allowed
                : PermissionStatus.Denied;
        calculation.Sources.TryGetValue(permission, out var source);
        return new PermissionResult(group, name, permission, status, source);
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
        long SnapshotVersion);

    private sealed record Calculation(
        PermissionBits Effective,
        bool Administrator,
        Dictionary<PermissionBits, string> Sources);

    private sealed record Definition(
        string Group,
        string Name,
        PermissionBits Permission,
        bool Applicable);
}
