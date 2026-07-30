using System.Collections.Immutable;
using DiscordControlCenter.Application.Explorer;
using DiscordControlCenter.Core.Explorer;

namespace DiscordControlCenter.Application.Tests;

public sealed class RoleHierarchySafetyServiceTests
{
    private readonly RoleHierarchySafetyService _service = new();

    [Fact]
    public void BotWithoutManageRolesIsDenied()
    {
        var result = _service.CanManageRole(
            Server(PermissionBits.None),
            Role(30, 1));

        Assert.Equal(SafetyDecision.Denied, result.Decision);
        Assert.Equal(HierarchyReasonCode.MissingRequiredPermission, result.ReasonCode);
    }

    [Fact]
    public void TargetRoleBelowBotIsAllowed()
    {
        var result = _service.CanManageRole(
            Server(PermissionBits.ManageRoles),
            Role(30, 4));

        Assert.Equal(SafetyDecision.Allowed, result.Decision);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public void TargetRoleEqualToOrAboveBotIsDenied(int position)
    {
        var result = _service.CanManageRole(
            Server(PermissionBits.ManageRoles),
            Role(30, position));

        Assert.Equal(HierarchyReasonCode.TargetAtOrAboveBot, result.ReasonCode);
    }

    [Fact]
    public void ManagedTargetRoleIsDenied()
    {
        var target = Role(30, 4) with { IsManaged = true };

        var result = _service.CanManageRole(Server(PermissionBits.ManageRoles), target);

        Assert.Equal(HierarchyReasonCode.TargetManagedExternally, result.ReasonCode);
    }

    [Fact]
    public void EveryoneTargetIsDenied()
    {
        var result = _service.CanManageRole(
            Server(PermissionBits.ManageRoles),
            new RoleReadModel(1, "@everyone", 0, PermissionBits.None, true));

        Assert.Equal(HierarchyReasonCode.TargetIsEveryone, result.ReasonCode);
    }

    [Fact]
    public void AssigningPermissionBotDoesNotPossessIsDenied()
    {
        var result = _service.CanAssignRole(
            Server(PermissionBits.ManageRoles),
            Role(30, 4, PermissionBits.BanMembers));

        Assert.Equal(
            HierarchyReasonCode.BotDoesNotPossessAssignedPermission,
            result.ReasonCode);
    }

    [Theory]
    [InlineData(4, SafetyDecision.Allowed)]
    [InlineData(10, SafetyDecision.Denied)]
    [InlineData(11, SafetyDecision.Denied)]
    public void MemberHierarchyIsComparedToBot(
        int targetPosition,
        SafetyDecision expected)
    {
        var result = _service.CanModerateMember(
            Server(PermissionBits.ModerateMembers),
            Member(targetPosition, rolesComplete: true));

        Assert.Equal(expected, result.Decision);
    }

    [Fact]
    public void IncompleteMemberHierarchyProducesUnknown()
    {
        var result = _service.CanModerateMember(
            Server(PermissionBits.ModerateMembers),
            Member(4, rolesComplete: false));

        Assert.Equal(SafetyDecision.Unknown, result.Decision);
        Assert.Equal(HierarchyReasonCode.IncompleteData, result.ReasonCode);
    }

    private static RoleReadModel Role(
        ulong id,
        int position,
        PermissionBits permissions = PermissionBits.None) =>
        new(id, $"Role {id}", position, permissions, false);

    private static MemberReadModel Member(int position, bool rolesComplete) =>
        new(
            500,
            "member",
            null,
            null,
            "member",
            null,
            false,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            [30],
            "Target",
            position,
            null,
            false,
            null,
            null,
            rolesComplete);

    private static ServerReadModel Server(PermissionBits botPermissions)
    {
        var roles = ImmutableArray.Create(
            new RoleReadModel(1, "@everyone", 0, PermissionBits.None, true),
            new RoleReadModel(20, "Bot", 10, botPermissions, false));
        return new ServerReadModel(
            1,
            "Server",
            null,
            null,
            999,
            DateTimeOffset.UtcNow,
            2,
            0,
            0,
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
            100,
            [20],
            roles,
            [],
            ServerAvailability.Available,
            DateTimeOffset.UtcNow);
    }
}
