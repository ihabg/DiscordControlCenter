namespace DiscordControlCenter.Core.Common;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
