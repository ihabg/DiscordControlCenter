using System.Collections.Immutable;
using DiscordControlCenter.Application.Explorer;
using DiscordControlCenter.Core.Explorer;

namespace DiscordControlCenter.Application.Tests;

public sealed class PermissionResolutionServiceTests
{
    private static readonly Guid BotProfileId = Guid.Parse("3cbdaf8e-bdc3-4e4d-94a8-226621d4f2d4");

    [Fact]
    public void AdministratorOverridesChannelDeny()
    {
        var service = new PermissionResolutionService();
        var server = Server(PermissionBits.Administrator);
        var channel = Channel(
            new PermissionOverwriteReadModel(
                1,
                PermissionTargetKind.Role,
                0,
                1,
                PermissionBits.None,
                PermissionBits.ViewChannel));

        var resolution = service.ResolveChannel(BotProfileId, 1, server, channel);

        var view = resolution.Permissions.Single(item => item.Permission == PermissionBits.ViewChannel);
        Assert.Equal(PermissionStatus.AllowedThroughAdministrator, view.Status);
        Assert.Equal("Administrator", view.Source);
    }

    [Fact]
    public void RoleOverwriteTakesPrecedenceOverEveryoneOverwrite()
    {
        var service = new PermissionResolutionService();
        var server = Server(PermissionBits.ViewChannel | PermissionBits.SendMessages);
        var channel = Channel(
            new PermissionOverwriteReadModel(
                1,
                PermissionTargetKind.Role,
                0,
                1,
                PermissionBits.None,
                PermissionBits.SendMessages),
            new PermissionOverwriteReadModel(
                20,
                PermissionTargetKind.Role,
                1,
                0,
                PermissionBits.SendMessages,
                PermissionBits.None));

        var resolution = service.ResolveChannel(BotProfileId, 1, server, channel);

        var send = resolution.Permissions.Single(item => item.Permission == PermissionBits.SendMessages);
        Assert.Equal(PermissionStatus.Allowed, send.Status);
        Assert.Equal("Role overwrite", send.Source);
    }

    [Fact]
    public void BotMemberOverwriteTakesPrecedenceOverRoleOverwrite()
    {
        var service = new PermissionResolutionService();
        var server = Server(PermissionBits.ViewChannel);
        var channel = Channel(
            new PermissionOverwriteReadModel(
                20,
                PermissionTargetKind.Role,
                1,
                0,
                PermissionBits.SendMessages,
                PermissionBits.None),
            new PermissionOverwriteReadModel(
                99,
                PermissionTargetKind.User,
                0,
                1,
                PermissionBits.None,
                PermissionBits.SendMessages));

        var resolution = service.ResolveChannel(BotProfileId, 1, server, channel);

        var send = resolution.Permissions.Single(item => item.Permission == PermissionBits.SendMessages);
        Assert.Equal(PermissionStatus.Denied, send.Status);
        Assert.Equal("Bot member overwrite", send.Source);
    }

    [Fact]
    public void PermissionCacheInvalidationRecalculatesSameSnapshotVersion()
    {
        var service = new PermissionResolutionService();
        var server = Server(PermissionBits.ViewChannel);
        var denied = Channel(
            new PermissionOverwriteReadModel(
                99,
                PermissionTargetKind.User,
                0,
                1,
                PermissionBits.None,
                PermissionBits.SendMessages));
        var allowed = Channel(
            new PermissionOverwriteReadModel(
                99,
                PermissionTargetKind.User,
                1,
                0,
                PermissionBits.SendMessages,
                PermissionBits.None));

        var first = service.ResolveChannel(BotProfileId, 5, server, denied);
        var cached = service.ResolveChannel(BotProfileId, 5, server, allowed);
        service.Invalidate(BotProfileId, server.Id);
        var refreshed = service.ResolveChannel(BotProfileId, 5, server, allowed);

        Assert.Equal(
            PermissionStatus.Denied,
            first.Permissions.Single(item => item.Permission == PermissionBits.SendMessages).Status);
        Assert.Same(first, cached);
        Assert.Equal(
            PermissionStatus.Allowed,
            refreshed.Permissions.Single(item => item.Permission == PermissionBits.SendMessages).Status);
    }

    [Fact]
    public void MemberUsesEveryoneAndAggregatedRolePermissions()
    {
        var service = new PermissionResolutionService();
        var server = Server(PermissionBits.SendMessages | PermissionBits.AttachFiles);
        var member = Member(500, rolesComplete: true);

        var resolution = service.ResolveMember(
            BotProfileId,
            1,
            server,
            member,
            Channel());

        Assert.Equal(
            PermissionStatus.Allowed,
            resolution.Permissions.Single(
                item => item.Permission == PermissionBits.ViewChannel).Status);
        Assert.Equal(
            PermissionStatus.Allowed,
            resolution.Permissions.Single(
                item => item.Permission == PermissionBits.AttachFiles).Status);
    }

    [Fact]
    public void MemberSpecificDenyOverridesRoleAllow()
    {
        var service = new PermissionResolutionService();
        var server = Server(PermissionBits.SendMessages);
        var member = Member(500, rolesComplete: true);
        var channel = Channel(
            new PermissionOverwriteReadModel(
                20,
                PermissionTargetKind.Role,
                1,
                0,
                PermissionBits.SendMessages,
                PermissionBits.None),
            new PermissionOverwriteReadModel(
                500,
                PermissionTargetKind.User,
                0,
                1,
                PermissionBits.None,
                PermissionBits.SendMessages));

        var resolution = service.ResolveMember(
            BotProfileId,
            1,
            server,
            member,
            channel);

        var send = resolution.Permissions.Single(
            item => item.Permission == PermissionBits.SendMessages);
        Assert.Equal(PermissionStatus.Denied, send.Status);
        Assert.Equal("Member-specific overwrite", send.Source);
    }

    [Fact]
    public void IncompleteMemberRolesProduceUnknown()
    {
        var service = new PermissionResolutionService();
        var member = Member(500, rolesComplete: false);

        var resolution = service.ResolveMember(
            BotProfileId,
            1,
            Server(PermissionBits.SendMessages),
            member,
            Channel());

        Assert.All(
            resolution.Permissions.Where(item => item.Status != PermissionStatus.NotApplicable),
            item => Assert.Equal(PermissionStatus.Unknown, item.Status));
    }

    [Fact]
    public void ComparisonHighlightsBothSidesAndDifferences()
    {
        var service = new PermissionResolutionService();
        var firstRole = new RoleReadModel(
            30,
            "First",
            2,
            PermissionBits.SendMessages,
            false);
        var secondRole = new RoleReadModel(
            31,
            "Second",
            2,
            PermissionBits.AttachFiles,
            false);
        var baseServer = Server(PermissionBits.ViewChannel);
        var server = baseServer with
        {
            Roles = baseServer.Roles.Add(firstRole).Add(secondRole),
            RoleCount = baseServer.RoleCount + 2
        };
        var first = service.ResolveRole(
            BotProfileId,
            1,
            server,
            firstRole,
            Channel());
        var second = service.ResolveRole(
            BotProfileId,
            1,
            server,
            secondRole,
            Channel());

        var comparison = service.Compare(first, second);

        Assert.Equal(
            PermissionComparisonStatus.FirstOnly,
            comparison.Permissions.Single(
                item => item.Permission == PermissionBits.SendMessages).Comparison);
        Assert.Equal(
            PermissionComparisonStatus.SecondOnly,
            comparison.Permissions.Single(
                item => item.Permission == PermissionBits.AttachFiles).Comparison);
        Assert.Equal(
            PermissionComparisonStatus.BothAllowed,
            comparison.Permissions.Single(
                item => item.Permission == PermissionBits.ViewChannel).Comparison);
    }

    private static ServerReadModel Server(PermissionBits botRolePermissions)
    {
        var roles = ImmutableArray.Create(
            new RoleReadModel(1, "@everyone", 0, PermissionBits.ViewChannel, true),
            new RoleReadModel(20, "Bot", 10, botRolePermissions, false));
        return new ServerReadModel(
            1,
            "Server",
            null,
            null,
            42,
            DateTimeOffset.UtcNow,
            1,
            0,
            1,
            0,
            0,
            0,
            roles.Length,
            0,
            "None",
            0,
            null,
            "Bot",
            10,
            99,
            [20],
            roles,
            ImmutableArray<ChannelReadModel>.Empty,
            ServerAvailability.Available,
            DateTimeOffset.UtcNow);
    }

    private static ChannelReadModel Channel(
        params PermissionOverwriteReadModel[] overwrites) =>
        new(
            100,
            "general",
            ChannelKind.Text,
            "Text",
            1,
            DateTimeOffset.UtcNow,
            null,
            null,
            null,
            overwrites.ToImmutableArray(),
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

    private static MemberReadModel Member(ulong id, bool rolesComplete) =>
        new(
            id,
            "member",
            null,
            null,
            "member",
            null,
            false,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            [20],
            "Bot",
            10,
            null,
            false,
            null,
            null,
            rolesComplete);
}
