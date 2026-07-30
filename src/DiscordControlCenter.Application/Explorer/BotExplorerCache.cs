using System.Collections.Immutable;
using DiscordControlCenter.Core.Explorer;

namespace DiscordControlCenter.Application.Explorer;

public sealed class BotExplorerCache(Guid botProfileId)
{
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
                ErrorMessage = null
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
                ErrorMessage = error
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
                ErrorMessage = null
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
                    _servers = _servers.SetItem(update.Server.Id, update.Server);
                    break;
                case ExplorerCacheUpdateKind.ServerRemoved when update.ServerId is ulong serverId:
                    _servers = _servers.Remove(serverId);
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
                update.ErrorMessage);
            return _snapshot;
        }
    }
}
