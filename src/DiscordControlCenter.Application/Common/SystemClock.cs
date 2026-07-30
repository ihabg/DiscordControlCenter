using DiscordControlCenter.Core.Common;

namespace DiscordControlCenter.Application.Common;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
