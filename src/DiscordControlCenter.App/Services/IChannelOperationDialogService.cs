using System.Collections.Immutable;
using DiscordControlCenter.Core.Explorer;

namespace DiscordControlCenter.App.Services;

public enum ChannelOperationUiMode
{
    Create,
    Edit,
    Rename,
    Move,
    Clone,
    Lock,
    SynchronizePermissions,
    Delete
}

public sealed record ChannelOperationContext(
    Guid BotProfileId,
    string BotDisplayName,
    ServerReadModel Server,
    ImmutableArray<ChannelReadModel> SelectedChannels);

public interface IChannelOperationDialogService
{
    Task<bool> ConfigurePreviewConfirmAndQueueAsync(
        ChannelOperationContext context,
        ChannelOperationUiMode mode,
        CancellationToken cancellationToken);
}
