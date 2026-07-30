using System.Collections.Immutable;
using DiscordControlCenter.Application.Explorer;
using DiscordControlCenter.Core.Explorer;

namespace DiscordControlCenter.Application.Tests;

public sealed class BotExplorerCacheTests
{
    [Fact]
    public void CacheIsIsolatedBetweenBots()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var first = new BotExplorerCache(firstId);
        var second = new BotExplorerCache(secondId);

        first.Apply(ExplorerCacheUpdate.Reset(firstId, 1, [Server(1, "First")], Now));
        second.Apply(ExplorerCacheUpdate.Reset(secondId, 1, [Server(2, "Second")], Now));

        Assert.Equal("First", Assert.Single(first.Snapshot.Servers).Name);
        Assert.Equal("Second", Assert.Single(second.Snapshot.Servers).Name);
    }

    [Fact]
    public void ServerCreateUpdateAndRemovalEventsAreIncremental()
    {
        var botId = Guid.NewGuid();
        var cache = new BotExplorerCache(botId);

        cache.Apply(ExplorerCacheUpdate.Upsert(botId, 1, Server(1, "Created"), Now));
        Assert.Equal("Created", Assert.Single(cache.Snapshot.Servers).Name);

        cache.Apply(ExplorerCacheUpdate.Upsert(botId, 2, Server(1, "Updated"), Now));
        Assert.Equal("Updated", Assert.Single(cache.Snapshot.Servers).Name);

        cache.Apply(ExplorerCacheUpdate.Remove(botId, 3, 1, Now));
        Assert.Empty(cache.Snapshot.Servers);
    }

    [Fact]
    public void ChannelCreateUpdateAndDeleteEventsReplaceOnlyAffectedServer()
    {
        var botId = Guid.NewGuid();
        var cache = new BotExplorerCache(botId);
        var other = Server(2, "Other");
        cache.Apply(ExplorerCacheUpdate.Reset(botId, 1, [Server(1, "Primary"), other], Now));

        cache.Apply(
            ExplorerCacheUpdate.Upsert(
                botId,
                2,
                Server(1, "Primary") with { Channels = [Channel(10, "created")] },
                Now));
        Assert.Equal("created", Assert.Single(cache.Snapshot.Servers.Single(item => item.Id == 1).Channels).Name);

        cache.Apply(
            ExplorerCacheUpdate.Upsert(
                botId,
                3,
                Server(1, "Primary") with { Channels = [Channel(10, "renamed")] },
                Now));
        Assert.Equal("renamed", Assert.Single(cache.Snapshot.Servers.Single(item => item.Id == 1).Channels).Name);

        cache.Apply(
            ExplorerCacheUpdate.Upsert(
                botId,
                4,
                Server(1, "Primary") with { Channels = ImmutableArray<ChannelReadModel>.Empty },
                Now));
        Assert.Empty(cache.Snapshot.Servers.Single(item => item.Id == 1).Channels);
        Assert.Equal(other, cache.Snapshot.Servers.Single(item => item.Id == 2));
    }

    [Fact]
    public void CacheClearsAfterDisconnect()
    {
        var botId = Guid.NewGuid();
        var cache = new BotExplorerCache(botId);
        cache.Apply(ExplorerCacheUpdate.Reset(botId, 1, [Server(1, "Server")], Now));

        var snapshot = cache.MarkDisconnected();

        Assert.Equal(ExplorerCacheState.Disconnected, snapshot.State);
        Assert.Empty(snapshot.Servers);
    }

    [Fact]
    public void OlderAsyncResetCannotOverwriteNewerGatewayUpdate()
    {
        var botId = Guid.NewGuid();
        var cache = new BotExplorerCache(botId);
        cache.Apply(ExplorerCacheUpdate.Upsert(botId, 10, Server(1, "Newest"), Now));

        var snapshot = cache.Apply(
            ExplorerCacheUpdate.Reset(botId, 9, [Server(1, "Stale")], Now));

        Assert.Equal("Newest", Assert.Single(snapshot.Servers).Name);
    }

    private static DateTimeOffset Now => new(2026, 7, 30, 0, 0, 0, TimeSpan.Zero);

    private static ServerReadModel Server(ulong id, string name) =>
        new(
            id,
            name,
            null,
            null,
            1,
            Now,
            null,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            "None",
            null,
            null,
            null,
            null,
            99,
            ImmutableArray<ulong>.Empty,
            ImmutableArray<RoleReadModel>.Empty,
            ImmutableArray<ChannelReadModel>.Empty,
            ServerAvailability.Available,
            Now);

    private static ChannelReadModel Channel(ulong id, string name) =>
        new(
            id,
            name,
            ChannelKind.Text,
            "Text",
            1,
            Now,
            null,
            null,
            null,
            ImmutableArray<PermissionOverwriteReadModel>.Empty,
            null,
            false,
            0,
            60,
            null,
            null,
            null,
            null,
            ImmutableArray<string>.Empty,
            null,
            null,
            null);
}
