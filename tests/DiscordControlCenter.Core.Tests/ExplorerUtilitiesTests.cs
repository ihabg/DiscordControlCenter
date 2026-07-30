using System.Collections.Immutable;
using DiscordControlCenter.Core.Explorer;

namespace DiscordControlCenter.Core.Tests;

public sealed class ExplorerUtilitiesTests
{
    [Fact]
    public void RolesAreOrderedHighestFirstWithEveryoneLast()
    {
        var roles = new[]
        {
            new RoleReadModel(1, "@everyone", 0, PermissionBits.None, true),
            new RoleReadModel(2, "Middle", 5, PermissionBits.None, false),
            new RoleReadModel(3, "High", 10, PermissionBits.None, false)
        };

        var ordered = ExplorerSearch.OrderRoles(roles);

        Assert.Equal([3UL, 2UL, 1UL], ordered.Select(role => role.Id));
    }

    [Fact]
    public void SynchronizationIgnoresOverwriteOrder()
    {
        var first = Overwrite(1, PermissionTargetKind.Role, 4, 8);
        var second = Overwrite(2, PermissionTargetKind.User, 16, 32);

        var result = PermissionSynchronization.AreSynchronized(
            10,
            [first, second],
            [second, first]);

        Assert.True(result);
    }

    [Fact]
    public void SynchronizationDetectsMissingOverwrite()
    {
        var first = Overwrite(1, PermissionTargetKind.Role, 4, 8);
        var second = Overwrite(2, PermissionTargetKind.User, 16, 32);

        Assert.False(
            PermissionSynchronization.AreSynchronized(10, [first], [first, second]));
    }

    [Fact]
    public void SynchronizationDetectsAdditionalOverwrite()
    {
        var first = Overwrite(1, PermissionTargetKind.Role, 4, 8);
        var second = Overwrite(2, PermissionTargetKind.User, 16, 32);

        Assert.False(
            PermissionSynchronization.AreSynchronized(10, [first, second], [first]));
    }

    [Fact]
    public void SynchronizationDetectsChangedAllowedValue()
    {
        Assert.False(
            PermissionSynchronization.AreSynchronized(
                10,
                [Overwrite(1, PermissionTargetKind.Role, 4, 8)],
                [Overwrite(1, PermissionTargetKind.Role, 2, 8)]));
    }

    [Fact]
    public void SynchronizationDetectsChangedDeniedValue()
    {
        Assert.False(
            PermissionSynchronization.AreSynchronized(
                10,
                [Overwrite(1, PermissionTargetKind.Role, 4, 8)],
                [Overwrite(1, PermissionTargetKind.Role, 4, 16)]));
    }

    [Fact]
    public void SynchronizationIsUnavailableWithoutParentCategory()
    {
        Assert.Null(
            PermissionSynchronization.AreSynchronized(
                null,
                [],
                []));
    }

    [Fact]
    public void EmptyOverwriteCollectionsAreSynchronized()
    {
        Assert.True(
            PermissionSynchronization.AreSynchronized(
                10,
                [],
                []));
    }

    [Fact]
    public void ServerSearchMatchesNameAndId()
    {
        var alpha = Server(100, "Alpha");
        var beta = Server(200, "Beta");

        Assert.Same(alpha, Assert.Single(ExplorerSearch.FilterServers([alpha, beta], "alp")));
        Assert.Same(beta, Assert.Single(ExplorerSearch.FilterServers([alpha, beta], "200")));
    }

    [Fact]
    public void ChannelSearchPreservesMatchedChannelCategory()
    {
        var category = Channel(10, "Operations", ChannelKind.Category, null, 2);
        var match = Channel(11, "incident-room", ChannelKind.Text, 10, 2);
        var other = Channel(12, "general", ChannelKind.Text, 10, 1);
        var server = Server(100, "Alpha") with
        {
            Channels = [category, match, other]
        };

        var result = ExplorerSearch.BuildChannelTree(server, "incident");

        var group = Assert.Single(result);
        Assert.Equal("Operations", group.Name);
        Assert.Equal(match, Assert.Single(group.Channels));
    }

    [Fact]
    public void ChannelSearchMatchesNumericId()
    {
        var channel = Channel(987654, "general", ChannelKind.Text, null, 1);
        var server = Server(100, "Alpha") with { Channels = [channel] };

        var result = ExplorerSearch.BuildChannelTree(server, "7654");

        Assert.Equal(channel, Assert.Single(Assert.Single(result).Channels));
    }

    [Fact]
    public void SnowflakeTimestampDecodesDiscordEpoch()
    {
        Assert.Equal(
            new DateTimeOffset(2015, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Snowflake.DecodeTimestamp(0));
    }

    private static PermissionOverwriteReadModel Overwrite(
        ulong id,
        PermissionTargetKind type,
        ulong allow,
        ulong deny) =>
        new(id, type, allow, deny, PermissionBits.None, PermissionBits.None);

    private static ServerReadModel Server(ulong id, string name) =>
        new(
            id,
            name,
            null,
            null,
            1,
            Snowflake.DecodeTimestamp(id),
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
            DateTimeOffset.UtcNow);

    private static ChannelReadModel Channel(
        ulong id,
        string name,
        ChannelKind kind,
        ulong? categoryId,
        int position) =>
        new(
            id,
            name,
            kind,
            kind.ToString(),
            position,
            Snowflake.DecodeTimestamp(id),
            categoryId,
            categoryId is null ? null : "Operations",
            categoryId is null ? null : true,
            ImmutableArray<PermissionOverwriteReadModel>.Empty,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            ImmutableArray<string>.Empty,
            null,
            null,
            null);
}
