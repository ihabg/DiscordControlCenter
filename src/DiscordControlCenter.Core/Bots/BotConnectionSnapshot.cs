namespace DiscordControlCenter.Core.Bots;

public sealed record BotConnectionSnapshot(
    Guid BotProfileId,
    BotConnectionState State,
    int? GatewayLatencyMilliseconds,
    int ServerCount,
    BotIdentity? Identity,
    DateTimeOffset? LastConnectedAt,
    string? ErrorMessage,
    DateTimeOffset? LastReadyAt = null,
    DateTimeOffset? LastDisconnectedAt = null,
    DateTimeOffset? LastReconnectedAt = null,
    bool FullMemberAccessEnabled = false,
    long VoiceStateEventCount = 0,
    DateTimeOffset? LastVoiceStateEventAt = null,
    string? RecentGatewayError = null)
{
    public static BotConnectionSnapshot Disconnected(Guid botProfileId) =>
        new(botProfileId, BotConnectionState.Disconnected, null, 0, null, null, null);
}
