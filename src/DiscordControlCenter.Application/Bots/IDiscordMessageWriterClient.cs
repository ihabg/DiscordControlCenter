using DiscordControlCenter.Application.Messaging;
using DiscordControlCenter.Core.Messaging;

namespace DiscordControlCenter.Application.Bots;

public interface IDiscordMessageWriterClient
{
    Task<MessageWriteOutcome> SendChannelMessageAsync(
        MessageOperationPlan plan,
        CancellationToken cancellationToken);

    Task<MessageWriteOutcome> SendDirectMessageAsync(
        MessageOperationPlan plan,
        CancellationToken cancellationToken);
}
