namespace DiscordControlCenter.Core.Explorer;

[Flags]
public enum PermissionBits : ulong
{
    None = 0,
    Administrator = 1UL << 0,
    ManageChannels = 1UL << 1,
    ManageRoles = 1UL << 2,
    ManageServer = 1UL << 3,
    ViewAuditLog = 1UL << 4,
    ManageWebhooks = 1UL << 5,
    KickMembers = 1UL << 6,
    BanMembers = 1UL << 7,
    ModerateMembers = 1UL << 8,
    ViewChannel = 1UL << 9,
    SendMessages = 1UL << 10,
    SendMessagesInThreads = 1UL << 11,
    CreatePublicThreads = 1UL << 12,
    CreatePrivateThreads = 1UL << 13,
    EmbedLinks = 1UL << 14,
    AttachFiles = 1UL << 15,
    AddReactions = 1UL << 16,
    ManageMessages = 1UL << 17,
    ReadMessageHistory = 1UL << 18,
    MentionEveryone = 1UL << 19,
    Connect = 1UL << 20,
    Speak = 1UL << 21,
    Stream = 1UL << 22,
    UseVoiceActivity = 1UL << 23,
    PrioritySpeaker = 1UL << 24,
    MuteMembers = 1UL << 25,
    DeafenMembers = 1UL << 26,
    MoveMembers = 1UL << 27,
    RequestToSpeak = 1UL << 28
}

public enum PermissionStatus
{
    Allowed,
    Denied,
    NotApplicable,
    AllowedThroughAdministrator
}

public sealed record PermissionResult(
    string Group,
    string Name,
    PermissionBits Permission,
    PermissionStatus Status,
    string? Source);

public sealed record PermissionResolution(
    PermissionBits EffectivePermissions,
    IReadOnlyList<PermissionResult> Permissions);

public static class PermissionBitsExtensions
{
    public static bool Has(this PermissionBits value, PermissionBits permission) =>
        (value & permission) == permission;
}
