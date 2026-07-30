using DiscordControlCenter.Application.Operations;
using DiscordControlCenter.Core.Explorer;
using DiscordControlCenter.Core.Operations;

namespace DiscordControlCenter.Application.Bots;

public interface IDiscordChannelWriterClient
{
    Task<ChannelWriteOutcome> CreateCategoryAsync(
        ulong serverId,
        ChannelOperationStateSnapshot after,
        string? auditReason,
        CancellationToken cancellationToken);

    Task<ChannelWriteOutcome> CreateTextChannelAsync(
        ulong serverId,
        ChannelOperationStateSnapshot after,
        string? auditReason,
        CancellationToken cancellationToken);

    Task<ChannelWriteOutcome> CreateVoiceChannelAsync(
        ulong serverId,
        ChannelOperationStateSnapshot after,
        string? auditReason,
        CancellationToken cancellationToken);

    Task<ChannelWriteOutcome> ModifyChannelAsync(
        ulong serverId,
        ulong channelId,
        ChannelOperationStateSnapshot before,
        ChannelOperationStateSnapshot after,
        string? auditReason,
        CancellationToken cancellationToken);

    Task<ChannelWriteOutcome> ReorderChannelsAsync(
        ulong serverId,
        IReadOnlyList<ChannelPositionUpdate> positions,
        string? auditReason,
        CancellationToken cancellationToken);

    Task<ChannelWriteOutcome> SetPermissionOverwriteAsync(
        ulong serverId,
        ulong channelId,
        ChannelPermissionOverwriteSnapshot overwrite,
        string? auditReason,
        CancellationToken cancellationToken);

    Task<ChannelWriteOutcome> DeletePermissionOverwriteAsync(
        ulong serverId,
        ulong channelId,
        ulong targetId,
        PermissionTargetKind targetType,
        string? auditReason,
        CancellationToken cancellationToken);

    Task<ChannelWriteOutcome> DeleteChannelAsync(
        ulong serverId,
        ulong channelId,
        string? auditReason,
        CancellationToken cancellationToken);
}
