namespace DiscordControlCenter.Core.Bots;

public sealed record BotProfile(
    Guid Id,
    string DisplayName,
    byte[] ProtectedToken,
    string TokenFingerprint,
    bool IsEnabled,
    DateTimeOffset CreatedAt,
    ulong? DiscordUserId = null,
    string? DiscordUsername = null,
    string? AvatarUrl = null,
    DateTimeOffset? LastConnectedAt = null,
    bool EnableFullMemberAccess = false)
{
    public const string MaskedToken = "••••••••••••••••";

    public BotProfile WithIdentity(BotIdentity identity, DateTimeOffset connectedAt) =>
        this with
        {
            DiscordUserId = identity.UserId,
            DiscordUsername = identity.Username,
            AvatarUrl = identity.AvatarUrl,
            LastConnectedAt = connectedAt
        };
}
