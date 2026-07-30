using System.Collections.Immutable;
using DiscordControlCenter.Application.Explorer;
using DiscordControlCenter.Core.Explorer;

namespace DiscordControlCenter.Application.Tests;

public sealed class MemberAndVoiceCacheTests
{
    private static readonly Guid BotId = Guid.Parse("23c8f46d-6a98-47b3-a617-77771fa29e0c");

    [Fact]
    public void MemberUpdatesAreIsolatedByBot()
    {
        var cache = ReadyCache(Server(1));
        var otherBot = Guid.NewGuid();

        Assert.Throws<ArgumentException>(
            () => cache.Apply(MemberUpdate(otherBot, 2, 1, Member(10, "Other"))));
        Assert.Empty(cache.Snapshot.Servers[0].Members.Members);
    }

    [Fact]
    public void MemberUpdatesAreIsolatedByServer()
    {
        var cache = ReadyCache(Server(1), Server(2));

        cache.Apply(MemberUpdate(BotId, 2, 2, Member(10, "Member")));

        Assert.Empty(cache.Snapshot.Servers.Single(server => server.Id == 1).Members.Members);
        Assert.Single(cache.Snapshot.Servers.Single(server => server.Id == 2).Members.Members);
    }

    [Fact]
    public void IncrementalMemberAddUpdateAndDuplicateSuppression()
    {
        var cache = ReadyCache(Server(1));

        cache.Apply(MemberUpdate(BotId, 2, 1, Member(10, "First")));
        cache.Apply(MemberUpdate(BotId, 3, 1, Member(10, "Updated")));

        var members = cache.Snapshot.Servers[0].Members.Members;
        Assert.Single(members);
        Assert.Equal("Updated", members[0].DisplayName);
    }

    [Fact]
    public void IncrementalMemberRemovalClearsTheTargetOnly()
    {
        var cache = ReadyCache(Server(1));
        cache.Apply(MemberUpdate(BotId, 2, 1, Member(10, "First"), Member(11, "Second")));

        cache.Apply(
            ExplorerCacheUpdate.Members(
                BotId,
                3,
                ExplorerCacheUpdateKind.MemberRemoved,
                new MemberCacheStateChange(
                    1,
                    10,
                    DataCompleteness.Partial,
                    true,
                    [],
                    1,
                    DateTimeOffset.UtcNow,
                    null),
                DateTimeOffset.UtcNow));

        Assert.Equal(11UL, Assert.Single(cache.Snapshot.Servers[0].Members.Members).Id);
    }

    [Fact]
    public void CompleteAndPartialStatesRemainExplicit()
    {
        var cache = ReadyCache(Server(1));
        cache.Apply(
            MemberState(
                2,
                DataCompleteness.Complete,
                [Member(10, "Complete")]));

        cache.Apply(MemberUpdate(BotId, 3, 1, Member(10, "Live update")));

        Assert.Equal(
            DataCompleteness.Complete,
            cache.Snapshot.Servers[0].Members.Completeness);
    }

    [Fact]
    public void StaleMemberGenerationIsRejected()
    {
        var cache = ReadyCache(Server(1));
        cache.Apply(MemberUpdate(BotId, 5, 1, Member(10, "Newest")));

        cache.Apply(MemberUpdate(BotId, 4, 1, Member(10, "Stale")));

        Assert.Equal("Newest", cache.Snapshot.Servers[0].Members.Members[0].DisplayName);
    }

    [Fact]
    public void DisconnectClearsMembersAndServers()
    {
        var cache = ReadyCache(Server(1));
        cache.Apply(MemberUpdate(BotId, 2, 1, Member(10, "Member")));

        cache.Apply(ExplorerCacheUpdate.Clear(BotId, 3, DateTimeOffset.UtcNow));

        Assert.Empty(cache.Snapshot.Servers);
        Assert.Equal(ExplorerCacheState.Disconnected, cache.Snapshot.State);
    }

    [Fact]
    public void VoiceMemberCanJoinMoveUpdateAndLeaveWithoutRebuildingServers()
    {
        var cache = ReadyCache(Server(1, VoiceChannel(100), VoiceChannel(200)));
        var joined = Voice(10, 100, selfMuted: false, streaming: false);
        cache.Apply(VoiceUpdate(2, joined));

        var updated = Voice(10, 200, selfMuted: true, streaming: true);
        cache.Apply(VoiceUpdate(3, updated));

        var server = cache.Snapshot.Servers[0];
        Assert.Empty(server.Channels.Single(channel => channel.Id == 100).VoiceMembers);
        var moved = Assert.Single(server.Channels.Single(channel => channel.Id == 200).VoiceMembers);
        Assert.True(moved.IsSelfMuted);
        Assert.True(moved.IsStreaming);

        cache.Apply(
            ExplorerCacheUpdate.Voice(
                BotId,
                4,
                [new VoiceStateCacheChange(1, 10, null, null)],
                DateTimeOffset.UtcNow));
        Assert.All(
            cache.Snapshot.Servers[0].Channels,
            channel => Assert.Empty(channel.VoiceMembers));
    }

    private static BotExplorerCache ReadyCache(params ServerReadModel[] servers)
    {
        var cache = new BotExplorerCache(BotId);
        cache.Apply(ExplorerCacheUpdate.Reset(BotId, 1, servers, DateTimeOffset.UtcNow));
        return cache;
    }

    private static ExplorerCacheUpdate MemberUpdate(
        Guid botId,
        long sequence,
        ulong serverId,
        params MemberReadModel[] members) =>
        ExplorerCacheUpdate.Members(
            botId,
            sequence,
            ExplorerCacheUpdateKind.MembersBatchUpserted,
            new MemberCacheStateChange(
                serverId,
                null,
                DataCompleteness.Partial,
                true,
                members.ToImmutableArray(),
                members.Length,
                DateTimeOffset.UtcNow,
                null),
            DateTimeOffset.UtcNow);

    private static ExplorerCacheUpdate MemberState(
        long sequence,
        DataCompleteness completeness,
        MemberReadModel[] members) =>
        ExplorerCacheUpdate.Members(
            BotId,
            sequence,
            ExplorerCacheUpdateKind.MembersStateChanged,
            new MemberCacheStateChange(
                1,
                null,
                completeness,
                true,
                members.ToImmutableArray(),
                members.Length,
                DateTimeOffset.UtcNow,
                null),
            DateTimeOffset.UtcNow);

    private static ExplorerCacheUpdate VoiceUpdate(long sequence, VoiceStateReadModel voice) =>
        ExplorerCacheUpdate.Voice(
            BotId,
            sequence,
            [new VoiceStateCacheChange(1, voice.UserId, Member(voice.UserId, voice.DisplayName), voice)],
            DateTimeOffset.UtcNow);

    private static MemberReadModel Member(ulong id, string name) =>
        new(
            id,
            name,
            name,
            null,
            name,
            null,
            false,
            Snowflake.DecodeTimestamp(id << 22),
            DateTimeOffset.UtcNow,
            [20],
            "Member",
            1,
            null,
            false,
            null,
            null,
            true);

    private static VoiceStateReadModel Voice(
        ulong userId,
        ulong channelId,
        bool selfMuted,
        bool streaming) =>
        new(
            userId,
            "Voice member",
            false,
            channelId,
            $"Voice {channelId}",
            selfMuted,
            false,
            false,
            false,
            streaming,
            false,
            false,
            null);

    private static ChannelReadModel VoiceChannel(ulong id) =>
        new(
            id,
            $"Voice {id}",
            ChannelKind.Voice,
            "Voice",
            0,
            DateTimeOffset.UtcNow,
            null,
            null,
            null,
            [],
            null,
            null,
            null,
            null,
            64_000,
            0,
            null,
            0,
            [],
            null,
            null,
            null);

    private static ServerReadModel Server(
        ulong id,
        params ChannelReadModel[] channels) =>
        new(
            id,
            $"Server {id}",
            null,
            null,
            99,
            DateTimeOffset.UtcNow,
            0,
            0,
            0,
            channels.Length,
            0,
            0,
            2,
            0,
            "None",
            0,
            null,
            "Bot",
            2,
            50,
            [20],
            [
                new RoleReadModel(id, "@everyone", 0, PermissionBits.ViewChannel, true),
                new RoleReadModel(20, "Bot", 2, PermissionBits.ManageRoles, false)
            ],
            channels.ToImmutableArray(),
            ServerAvailability.Available,
            DateTimeOffset.UtcNow)
        {
            Members = MemberCollectionReadModel.Limited(true, null, 0)
        };
}
