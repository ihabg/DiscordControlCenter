using DiscordControlCenter.Application.Bots;
using DiscordControlCenter.Application.Explorer;
using DiscordControlCenter.Core.Bots;
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
        public IDiscordBotClient Create(Guid botProfileId) =>
            new FakeClient(botProfileId, botProfileId == failingId);
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

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
