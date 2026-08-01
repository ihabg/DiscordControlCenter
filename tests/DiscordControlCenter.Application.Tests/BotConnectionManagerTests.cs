using DiscordControlCenter.Application.Bots;
using DiscordControlCenter.Application.Explorer;
using DiscordControlCenter.Application.Messaging;
using DiscordControlCenter.Application.Operations;
using DiscordControlCenter.Core.Bots;
using DiscordControlCenter.Core.Explorer;
using DiscordControlCenter.Core.Operations;
using DiscordControlCenter.Core.Messaging;
using DiscordControlCenter.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordControlCenter.Application.Tests;

public sealed class BotConnectionManagerTests
{
    [Fact]
    public async Task ConnectAllAsyncOneFailureDoesNotPreventOtherBotConnecting()
    {
        var first = CreateProfile("First");
        var second = CreateProfile("Second");
        var repository = new MemoryBotRepository(first, second);
        var factory = new FakeClientFactory(first.Id);
        await using var manager = new BotConnectionManager(
            repository,
            new PlainTestProtector(),
            factory,
            new PermissionResolutionService(),
            NullLogger<BotConnectionManager>.Instance);
        await manager.InitializeAsync(CancellationToken.None);

        await manager.ConnectAllAsync(CancellationToken.None);

        Assert.Equal(BotConnectionState.Faulted, manager.Snapshots.Single(x => x.BotProfileId == first.Id).State);
        Assert.Equal(BotConnectionState.Connected, manager.Snapshots.Single(x => x.BotProfileId == second.Id).State);
    }

    private static BotProfile CreateProfile(string name) =>
        new(
            Guid.NewGuid(),
            name,
            [1],
            "fingerprint",
            true,
            DateTimeOffset.UtcNow);

    private sealed class PlainTestProtector : ITokenProtector
    {
        public byte[] Protect(string token) => [1];
        public string Unprotect(byte[] protectedToken) => "test-token";
        public string CreateFingerprint(string token) => "fingerprint";
    }

    private sealed class FakeClientFactory(Guid failingId) : IDiscordBotClientFactory
    {
        public IDiscordBotClient Create(Guid botProfileId, bool enableFullMemberAccess)
        {
            _ = enableFullMemberAccess;
            return
            new FakeClient(botProfileId, botProfileId == failingId);
        }
    }

    private sealed class FakeClient(Guid id, bool shouldFail) : IDiscordBotClient
    {
        public event EventHandler<BotConnectionSnapshot>? StatusChanged;
        public event EventHandler<ExplorerCacheUpdate>? ExplorerChanged;

        public BotConnectionSnapshot Snapshot { get; private set; } =
            BotConnectionSnapshot.Disconnected(id);

        public Task ConnectAsync(string token, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (shouldFail)
            {
                throw new InvalidOperationException("simulated");
            }

            Snapshot = new BotConnectionSnapshot(
                id,
                BotConnectionState.Connected,
                20,
                3,
                new BotIdentity(100, "bot", null),
                DateTimeOffset.UtcNow,
                null);
            StatusChanged?.Invoke(this, Snapshot);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            Snapshot = BotConnectionSnapshot.Disconnected(id);
            StatusChanged?.Invoke(this, Snapshot);
            return Task.CompletedTask;
        }

        public Task<ExplorerCacheUpdate> RefreshExplorerAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var update = ExplorerCacheUpdate.Reset(
                id,
                1,
                [],
                DateTimeOffset.UtcNow);
            ExplorerChanged?.Invoke(this, update);
            return Task.FromResult(update);
        }

        public Task LoadMembersAsync(ulong serverId, CancellationToken cancellationToken)
        {
            _ = serverId;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<bool> MessageExistsAsync(ulong channelId, ulong messageId, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<ChannelWriteOutcome> CreateCategoryAsync(
            ulong serverId,
            ChannelOperationStateSnapshot after,
            string? auditReason,
            CancellationToken cancellationToken) =>
            UnsupportedWriteAsync();

        public Task<ChannelWriteOutcome> CreateTextChannelAsync(
            ulong serverId,
            ChannelOperationStateSnapshot after,
            string? auditReason,
            CancellationToken cancellationToken) =>
            UnsupportedWriteAsync();

        public Task<ChannelWriteOutcome> CreateVoiceChannelAsync(
            ulong serverId,
            ChannelOperationStateSnapshot after,
            string? auditReason,
            CancellationToken cancellationToken) =>
            UnsupportedWriteAsync();

        public Task<ChannelWriteOutcome> ModifyChannelAsync(
            ulong serverId,
            ulong channelId,
            ChannelOperationStateSnapshot before,
            ChannelOperationStateSnapshot after,
            string? auditReason,
            CancellationToken cancellationToken) =>
            UnsupportedWriteAsync();

        public Task<ChannelWriteOutcome> ReorderChannelsAsync(
            ulong serverId,
            IReadOnlyList<ChannelPositionUpdate> positions,
            string? auditReason,
            CancellationToken cancellationToken) =>
            UnsupportedWriteAsync();

        public Task<ChannelWriteOutcome> SetPermissionOverwriteAsync(
            ulong serverId,
            ulong channelId,
            ChannelPermissionOverwriteSnapshot overwrite,
            string? auditReason,
            CancellationToken cancellationToken) =>
            UnsupportedWriteAsync();

        public Task<ChannelWriteOutcome> DeletePermissionOverwriteAsync(
            ulong serverId,
            ulong channelId,
            ulong targetId,
            PermissionTargetKind targetType,
            string? auditReason,
            CancellationToken cancellationToken) =>
            UnsupportedWriteAsync();

        public Task<ChannelWriteOutcome> DeleteChannelAsync(
            ulong serverId,
            ulong channelId,
            string? auditReason,
            CancellationToken cancellationToken) =>
            UnsupportedWriteAsync();

        public Task<MessageWriteOutcome> SendChannelMessageAsync(
            MessageOperationPlan plan,
            CancellationToken cancellationToken) =>
            UnsupportedMessageWriteAsync();

        public Task<MessageWriteOutcome> SendDirectMessageAsync(
            MessageOperationPlan plan,
            CancellationToken cancellationToken) =>
            UnsupportedMessageWriteAsync();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static Task<ChannelWriteOutcome> UnsupportedWriteAsync() =>
            Task.FromResult(
                new ChannelWriteOutcome(
                    false,
                    null,
                    null,
                    OperationOutcomeCertainty.KnownFailed));

        private static Task<MessageWriteOutcome> UnsupportedMessageWriteAsync() =>
            Task.FromResult(
                new MessageWriteOutcome(
                    false,
                    null,
                    new MessageDeliveryFailure(
                        MessageDeliveryFailureKind.Validation,
                        "UNSUPPORTED",
                        "Unsupported by the test client.",
                        false,
                        false)));
    }
}
