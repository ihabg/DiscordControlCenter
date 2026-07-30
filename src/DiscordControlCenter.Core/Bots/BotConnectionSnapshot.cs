namespace DiscordControlCenter.Core.Bots;

public sealed record BotConnectionSnapshot(
    Guid BotProfileId,
    BotConnectionState State,
    int? GatewayLatencyMilliseconds,
    int ServerCount,
    BotIdentity? Identity,
    DateTimeOffset? LastConnectedAt,
    string? ErrorMessage)
{
    public static BotConnectionSnapshot Disconnected(Guid botProfileId) =>
        new(botProfileId, BotConnectionState.Disconnected, null, 0, null, null, null);
}
