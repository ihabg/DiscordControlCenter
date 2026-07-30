using System.Collections.Immutable;
using DiscordControlCenter.Core.Explorer;

namespace DiscordControlCenter.Application.Explorer;

public sealed class BotExplorerCache(Guid botProfileId)
{
    private const int MaximumMembersPerServer = 100_000;
    private readonly object _gate = new();
    private ImmutableDictionary<ulong, ServerReadModel> _servers =
        ImmutableDictionary<ulong, ServerReadModel>.Empty;
    private BotExplorerSnapshot _snapshot = BotExplorerSnapshot.Disconnected(botProfileId);
    private long _lastSequence = -1;

    public BotExplorerSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public BotExplorerSnapshot MarkLoading()
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                Version = _snapshot.Version + 1,
                State = ExplorerCacheState.Loading,
                ErrorMessage = null,
                IsRefreshPending = true
            };
            return _snapshot;
        }
    }

    public BotExplorerSnapshot MarkDisconnected()
    {
        lock (_gate)
        {
            _servers = ImmutableDictionary<ulong, ServerReadModel>.Empty;
            _snapshot = BotExplorerSnapshot.Disconnected(botProfileId, _snapshot.Version + 1);
            return _snapshot;
        }
    }

    public BotExplorerSnapshot MarkFaulted(string error)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                Version = _snapshot.Version + 1,
                State = ExplorerCacheState.Faulted,
                ErrorMessage = error,
                IsRefreshPending = false
            };
            return _snapshot;
        }
    }

    public BotExplorerSnapshot CancelLoading(ExplorerCacheState previousState)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                Version = _snapshot.Version + 1,
                State = previousState,
                ErrorMessage = null,
                IsRefreshPending = false
            };
            return _snapshot;
        }
    }

    public BotExplorerSnapshot Apply(ExplorerCacheUpdate update)
    {
        if (update.BotProfileId != botProfileId)
        {
            throw new ArgumentException("The update belongs to another bot runtime.", nameof(update));
        }

        lock (_gate)
        {
            if (update.Sequence <= _lastSequence)
            {
                return _snapshot;
            }

            _lastSequence = update.Sequence;
            switch (update.Kind)
            {
                case ExplorerCacheUpdateKind.Reset:
                    _servers = update.Servers.ToImmutableDictionary(server => server.Id);
                    break;
                case ExplorerCacheUpdateKind.ServerUpserted when update.Server is not null:
                    _servers = _servers.SetItem(
                        update.Server.Id,
                        MergeServer(_servers.GetValueOrDefault(update.Server.Id), update.Server));
                    break;
                case ExplorerCacheUpdateKind.ServerRemoved when update.ServerId is ulong serverId:
                    _servers = _servers.Remove(serverId);
                    break;
                case ExplorerCacheUpdateKind.MembersLoading:
                case ExplorerCacheUpdateKind.MembersBatchUpserted:
                case ExplorerCacheUpdateKind.MemberUpserted:
                case ExplorerCacheUpdateKind.MemberRemoved:
                case ExplorerCacheUpdateKind.MembersStateChanged:
                    ApplyMemberChange(update);
                    break;
                case ExplorerCacheUpdateKind.VoiceStatesChanged:
                    ApplyVoiceChanges(update.VoiceChanges);
                    break;
                case ExplorerCacheUpdateKind.Cleared:
                    _servers = ImmutableDictionary<ulong, ServerReadModel>.Empty;
                    break;
                case ExplorerCacheUpdateKind.Faulted:
                    break;
            }

            var state = update.Kind switch
            {
                ExplorerCacheUpdateKind.Cleared => ExplorerCacheState.Disconnected,
                ExplorerCacheUpdateKind.Faulted => ExplorerCacheState.Faulted,
                _ => ExplorerCacheState.Ready
            };
            _snapshot = new BotExplorerSnapshot(
                botProfileId,
                _snapshot.Version + 1,
                state,
                _servers.Values
                    .OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(server => server.Id)
                    .ToImmutableArray(),
                update.Kind == ExplorerCacheUpdateKind.Cleared ? null : update.OccurredAt,
                update.ErrorMessage)
            {
                LastAcceptedSequence = _lastSequence,
                LastSuccessfulRefreshAt = update.Kind == ExplorerCacheUpdateKind.Reset
                    ? update.OccurredAt
                    : _snapshot.LastSuccessfulRefreshAt,
                IsRefreshPending = false
            };
            return _snapshot;
        }
    }

    private void ApplyMemberChange(ExplorerCacheUpdate update)
    {
        var change = update.MemberChange;
        if (change is null || !_servers.TryGetValue(change.ServerId, out var server))
        {
            return;
        }

        var existing = server.Members;
        var members = existing.Members.ToDictionary(member => member.Id);
        switch (update.Kind)
        {
            case ExplorerCacheUpdateKind.MembersBatchUpserted:
            case ExplorerCacheUpdateKind.MemberUpserted:
                foreach (var member in change.Members)
                {
                    members[member.Id] = member;
                }

                break;
            case ExplorerCacheUpdateKind.MemberRemoved when change.MemberId is ulong memberId:
                members.Remove(memberId);
                break;
            case ExplorerCacheUpdateKind.MembersStateChanged
                when change.Completeness == DataCompleteness.Complete
                    && !change.Members.IsDefault:
                members = change.Members.ToDictionary(member => member.Id);
                break;
        }

        var completeness = (update.Kind is ExplorerCacheUpdateKind.MemberUpserted
                or ExplorerCacheUpdateKind.MemberRemoved
                or ExplorerCacheUpdateKind.MembersBatchUpserted)
            && existing.Completeness == DataCompleteness.Complete
                ? DataCompleteness.Complete
                : change.Completeness;
        var bounded = members.Count > MaximumMembersPerServer;
        var memberArray = members.Values
            .OrderBy(member => member.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(member => member.Id)
            .Take(MaximumMembersPerServer)
            .ToImmutableArray();
        if (bounded)
        {
            completeness = DataCompleteness.Partial;
        }

        var memberSnapshot = new MemberCollectionReadModel(
            completeness,
            change.FullAccessEnabled,
            memberArray,
            change.ExpectedMemberCount ?? existing.ExpectedMemberCount,
            change.RefreshedAt ?? existing.LastRefreshedAt,
            bounded
                ? $"Member caching is limited to {MaximumMembersPerServer:N0} entries per server."
                : change.ErrorMessage);
        var updatedServer = server with { Members = memberSnapshot };
        _servers = _servers.SetItem(
            server.Id,
            update.Kind == ExplorerCacheUpdateKind.MembersBatchUpserted
                && completeness == DataCompleteness.Loading
                    ? updatedServer
                    : RecalculateRoleCounts(updatedServer));
    }

    private void ApplyVoiceChanges(ImmutableArray<VoiceStateCacheChange> changes)
    {
        foreach (var serverChanges in changes.GroupBy(change => change.ServerId))
        {
            if (!_servers.TryGetValue(serverChanges.Key, out var server))
            {
                continue;
            }

            var members = server.Members.Members.ToDictionary(member => member.Id);
            var channels = server.Channels.ToDictionary(channel => channel.Id);
            foreach (var change in serverChanges)
            {
                foreach (var channel in channels.Values.Where(
                             channel => channel.Kind is ChannelKind.Voice or ChannelKind.Stage))
                {
                    var voiceMembers = channel.VoiceMembers
                        .Where(member => member.UserId != change.UserId)
                        .ToImmutableArray();
                    channels[channel.Id] = channel with
                    {
                        VoiceMembers = voiceMembers,
                        ConnectedUserCount = voiceMembers.Length
                    };
                }

                if (change.VoiceState is not null
                    && channels.TryGetValue(change.VoiceState.ChannelId, out var destination))
                {
                    var destinationMembers = destination.VoiceMembers
                        .Where(member => member.UserId != change.UserId)
                        .Append(change.VoiceState)
                        .OrderBy(member => member.DisplayName, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(member => member.UserId)
                        .ToImmutableArray();
                    channels[destination.Id] = destination with
                    {
                        VoiceMembers = destinationMembers,
                        ConnectedUserCount = destinationMembers.Length
                    };
                }

                if (members.TryGetValue(change.UserId, out var existingMember))
                {
                    members[change.UserId] = existingMember with { VoiceState = change.VoiceState };
                }
                else if (change.Member is not null)
                {
                    members[change.UserId] = change.Member with { VoiceState = change.VoiceState };
                }

                if (!server.Members.FullAccessEnabled
                    && change.VoiceState is null
                    && change.UserId != server.BotUserId)
                {
                    members.Remove(change.UserId);
                }
            }

            var memberSnapshot = server.Members with
            {
                Members = members.Values
                    .OrderBy(member => member.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(member => member.Id)
                    .ToImmutableArray()
            };
            _servers = _servers.SetItem(
                server.Id,
                RecalculateRoleCounts(
                    server with
                    {
                        Members = memberSnapshot,
                        Channels = channels.Values
                            .OrderBy(channel => channel.Kind == ChannelKind.Category ? 0 : 1)
                            .ThenBy(channel => channel.Position)
                            .ThenBy(channel => channel.Id)
                            .ToImmutableArray()
                    }));
        }
    }

    private static ServerReadModel MergeServer(
        ServerReadModel? existing,
        ServerReadModel incoming)
    {
        if (existing is null)
        {
            return RecalculateRoleCounts(incoming);
        }

        var members = Rank(incoming.Members.Completeness) >= Rank(existing.Members.Completeness)
            ? incoming.Members
            : existing.Members;
        members = RefreshMemberHierarchy(members, incoming.Roles);
        return RecalculateRoleCounts(incoming with { Members = members });
    }

    private static int Rank(DataCompleteness completeness) =>
        completeness switch
        {
            DataCompleteness.Complete => 5,
            DataCompleteness.Partial => 4,
            DataCompleteness.Loading => 3,
            DataCompleteness.Limited => 2,
            DataCompleteness.Cancelled => 1,
            DataCompleteness.Failed => 1,
            _ => 0
        };

    private static ServerReadModel RecalculateRoleCounts(ServerReadModel server)
    {
        var exact = server.Members.Completeness == DataCompleteness.Complete;
        var members = server.Members.Members;
        var roles = server.Roles
            .Select(role => role with
            {
                MemberCount = exact || members.Length > 0
                    ? members.Count(member =>
                        role.IsEveryone || member.RoleIds.Contains(role.Id))
                    : null,
                MemberCountCompleteness = exact
                    ? DataCompleteness.Complete
                    : members.Length > 0
                        ? DataCompleteness.Partial
                        : DataCompleteness.Unavailable
            })
            .ToImmutableArray();
        return server with { Roles = roles };
    }

    private static MemberCollectionReadModel RefreshMemberHierarchy(
        MemberCollectionReadModel members,
        ImmutableArray<RoleReadModel> roles)
    {
        var rolesById = roles.ToDictionary(role => role.Id);
        return members with
        {
            Members = members.Members
                .Select(
                    member =>
                    {
                        var highest = member.RoleIds
                            .Where(rolesById.ContainsKey)
                            .Select(roleId => rolesById[roleId])
                            .OrderByDescending(role => role.Position)
                            .ThenByDescending(role => role.Id)
                            .FirstOrDefault();
                        return member with
                        {
                            HighestRoleName = highest?.Name,
                            HighestRolePosition = highest?.Position
                        };
                    })
                .ToImmutableArray()
        };
    }
}
