namespace DiscordControlCenter.Core.Bots;

public enum BotConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Disconnecting,
    Faulted
}
