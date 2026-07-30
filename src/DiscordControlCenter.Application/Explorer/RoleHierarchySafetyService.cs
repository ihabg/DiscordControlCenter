using DiscordControlCenter.Core.Explorer;

namespace DiscordControlCenter.Application.Explorer;

public sealed class RoleHierarchySafetyService : IRoleHierarchySafetyService
{
    public HierarchyPreflightResult CanManageRole(
        ServerReadModel server,
        RoleReadModel targetRole) =>
        EvaluateRole(server, targetRole, requirePossessedPermissions: false);

    public HierarchyPreflightResult CanAssignRole(
        ServerReadModel server,
        RoleReadModel targetRole) =>
        EvaluateRole(server, targetRole, requirePossessedPermissions: true);

    public HierarchyPreflightResult CanRemoveRole(
        ServerReadModel server,
        RoleReadModel targetRole) =>
        EvaluateRole(server, targetRole, requirePossessedPermissions: false);

    public HierarchyPreflightResult CanModerateMember(
        ServerReadModel server,
        MemberReadModel targetMember) =>
        EvaluateMember(
            server,
            targetMember,
            PermissionBits.ModerateMembers,
            "moderate this member");

    public HierarchyPreflightResult CanChangeNickname(
        ServerReadModel server,
        MemberReadModel targetMember) =>
        EvaluateMember(
            server,
            targetMember,
            PermissionBits.ManageNicknames,
            "change this member's nickname");

    private static HierarchyPreflightResult EvaluateRole(
        ServerReadModel server,
        RoleReadModel targetRole,
        bool requirePossessedPermissions)
    {
        var botPermissions = GetBotPermissions(server);
        var botPosition = server.BotRolePosition;
        if (!botPermissions.Has(PermissionBits.ManageRoles)
            && !botPermissions.Has(PermissionBits.Administrator)
            && server.OwnerId != server.BotUserId)
        {
            return Denied(
                HierarchyReasonCode.MissingRequiredPermission,
                "The bot does not have Manage Roles.",
                PermissionBits.ManageRoles,
                botPosition,
                targetRole.Position);
        }

        if (targetRole.IsEveryone)
        {
            return Denied(
                HierarchyReasonCode.TargetIsEveryone,
                "@everyone cannot be managed or assigned as a normal role.",
                PermissionBits.ManageRoles,
                botPosition,
                targetRole.Position);
        }

        if (targetRole.IsManaged)
        {
            return Denied(
                HierarchyReasonCode.TargetManagedExternally,
                "Discord, a bot, or an integration manages this role.",
                PermissionBits.ManageRoles,
                botPosition,
                targetRole.Position);
        }

        if (botPosition is null)
        {
            return Unknown(
                "The bot's highest role is unavailable.",
                PermissionBits.ManageRoles,
                null,
                targetRole.Position);
        }

        if (server.OwnerId != server.BotUserId && targetRole.Position >= botPosition.Value)
        {
            return Denied(
                HierarchyReasonCode.TargetAtOrAboveBot,
                "The target role is equal to or above the bot's highest role.",
                PermissionBits.ManageRoles,
                botPosition,
                targetRole.Position);
        }

        if (requirePossessedPermissions
            && !botPermissions.Has(PermissionBits.Administrator)
            && (targetRole.Permissions & ~botPermissions) != PermissionBits.None)
        {
            return Denied(
                HierarchyReasonCode.BotDoesNotPossessAssignedPermission,
                "The role grants a permission the bot does not possess.",
                PermissionBits.ManageRoles,
                botPosition,
                targetRole.Position);
        }

        return Allowed(
            "The role is below the bot and is not externally managed.",
            PermissionBits.ManageRoles,
            botPosition,
            targetRole.Position);
    }

    private static HierarchyPreflightResult EvaluateMember(
        ServerReadModel server,
        MemberReadModel targetMember,
        PermissionBits requiredPermission,
        string action)
    {
        var botPermissions = GetBotPermissions(server);
        var botPosition = server.BotRolePosition;
        if (!botPermissions.Has(requiredPermission)
            && !botPermissions.Has(PermissionBits.Administrator)
            && server.OwnerId != server.BotUserId)
        {
            return Denied(
                HierarchyReasonCode.MissingRequiredPermission,
                $"The bot lacks the permission required to {action}.",
                requiredPermission,
                botPosition,
                targetMember.HighestRolePosition);
        }

        if (!targetMember.RolesAreComplete || targetMember.HighestRolePosition is null)
        {
            return Unknown(
                "The member's complete role hierarchy is unavailable.",
                requiredPermission,
                botPosition,
                targetMember.HighestRolePosition);
        }

        if (targetMember.Id == server.OwnerId)
        {
            return Denied(
                HierarchyReasonCode.TargetIsServerOwner,
                "The server owner cannot be moderated by this bot.",
                requiredPermission,
                botPosition,
                targetMember.HighestRolePosition);
        }

        if (botPosition is null)
        {
            return Unknown(
                "The bot's highest role is unavailable.",
                requiredPermission,
                null,
                targetMember.HighestRolePosition);
        }

        if (server.OwnerId != server.BotUserId
            && targetMember.HighestRolePosition.Value >= botPosition.Value)
        {
            return Denied(
                HierarchyReasonCode.TargetAtOrAboveBot,
                "The member's highest role is equal to or above the bot's highest role.",
                requiredPermission,
                botPosition,
                targetMember.HighestRolePosition);
        }

        return Allowed(
            $"The member is below the bot's highest role, so the bot can {action}.",
            requiredPermission,
            botPosition,
            targetMember.HighestRolePosition);
    }

    private static PermissionBits GetBotPermissions(ServerReadModel server)
    {
        var permissions = server.Roles
            .Where(role => role.IsEveryone || server.BotRoleIds.Contains(role.Id))
            .Aggregate(PermissionBits.None, (current, role) => current | role.Permissions);
        return permissions.Has(PermissionBits.Administrator)
            ? Enum.GetValues<PermissionBits>()
                .Aggregate(PermissionBits.None, (current, permission) => current | permission)
            : permissions;
    }

    private static HierarchyPreflightResult Allowed(
        string explanation,
        PermissionBits permission,
        int? botPosition,
        int? targetPosition) =>
        new(
            SafetyDecision.Allowed,
            HierarchyReasonCode.Allowed,
            explanation,
            permission,
            botPosition,
            targetPosition,
            DataCompleteness.Complete);

    private static HierarchyPreflightResult Denied(
        HierarchyReasonCode reason,
        string explanation,
        PermissionBits permission,
        int? botPosition,
        int? targetPosition) =>
        new(
            SafetyDecision.Denied,
            reason,
            explanation,
            permission,
            botPosition,
            targetPosition,
            DataCompleteness.Complete);

    private static HierarchyPreflightResult Unknown(
        string explanation,
        PermissionBits permission,
        int? botPosition,
        int? targetPosition) =>
        new(
            SafetyDecision.Unknown,
            HierarchyReasonCode.IncompleteData,
            explanation,
            permission,
            botPosition,
            targetPosition,
            DataCompleteness.Partial);
}
