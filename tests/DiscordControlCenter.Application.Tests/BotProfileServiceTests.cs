using DiscordControlCenter.Application.Bots;
using DiscordControlCenter.Application.Common;
using DiscordControlCenter.Core.Auditing;
using DiscordControlCenter.Core.Bots;
using DiscordControlCenter.Core.Common;
using DiscordControlCenter.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordControlCenter.Application.Tests;

public sealed class BotProfileServiceTests
{
    [Fact]
    public async Task AddAsyncValidatesThenStoresOnlyProtectedToken()
    {
        var repository = new MemoryBotRepository();
        var protector = new FakeTokenProtector();
        var validator = new FakeValidator();
        var service = CreateService(repository, protector, validator);

        var result = await service.AddAsync(
            new AddBotRequest("  Operations Bot  ", "valid.token.value"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var saved = Assert.Single(repository.Profiles);
        Assert.Equal("Operations Bot", saved.DisplayName);
        Assert.Equal(new byte[] { 9, 8, 7 }, saved.ProtectedToken);
        Assert.DoesNotContain(
            System.Text.Encoding.UTF8.GetBytes("valid.token.value"),
            saved.ProtectedToken);
        Assert.Equal(1, validator.CallCount);
    }

    [Theory]
    [InlineData("", "token", "Display name is required.")]
    [InlineData("name", "", "Bot token is required.")]
    [InlineData("name", "bad token", "Bot token cannot contain whitespace.")]
    public async Task AddAsyncInvalidInputDoesNotValidateOrPersist(
        string name,
        string token,
        string expectedError)
    {
        var repository = new MemoryBotRepository();
        var validator = new FakeValidator();
        var service = CreateService(repository, new FakeTokenProtector(), validator);

        var result = await service.AddAsync(
            new AddBotRequest(name, token),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(expectedError, result.Error);
        Assert.Empty(repository.Profiles);
        Assert.Equal(0, validator.CallCount);
    }

    [Fact]
    public async Task ReplaceTokenAsyncRejectsDifferentDiscordBotIdentity()
    {
        var profile = new BotProfile(
            Guid.NewGuid(),
            "Existing",
            [1, 2],
            "OLD",
            true,
            DateTimeOffset.UtcNow,
            999,
            "existing-bot");
        var repository = new MemoryBotRepository(profile);
        var service = CreateService(
            repository,
            new FakeTokenProtector(),
            new FakeValidator(123));

        var result = await service.ReplaceTokenAsync(
            profile.Id,
            "replacement.token",
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("different Discord bot", result.Error);
        Assert.Equal(new byte[] { 1, 2 }, repository.Profiles[0].ProtectedToken);
    }

    private static BotProfileService CreateService(
        MemoryBotRepository repository,
        ITokenProtector protector,
        IDiscordTokenValidator validator) =>
        new(
            repository,
            protector,
            validator,
            new NoOpConnectionManager(),
            new MemoryAuditRepository(),
            new FixedClock(),
            NullLogger<BotProfileService>.Instance);

    private sealed class FakeValidator(ulong userId = 123) : IDiscordTokenValidator
    {
        public int CallCount { get; private set; }

        public Task<BotIdentity> ValidateAsync(string token, CancellationToken cancellationToken)
        {
            _ = token;
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(new BotIdentity(userId, "validated-bot", null));
        }
    }

    private sealed class FakeTokenProtector : ITokenProtector
    {
        public byte[] Protect(string token)
        {
            _ = token;
            return [9, 8, 7];
        }

        public string Unprotect(byte[] protectedToken)
        {
            _ = protectedToken;
            return "valid.token.value";
        }

        public string CreateFingerprint(string token)
        {
            _ = token;
            return "FINGERPRINT";
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } =
            new(2026, 7, 29, 10, 0, 0, TimeSpan.Zero);
    }

    private sealed class MemoryAuditRepository : IAuditRepository
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task AddAsync(AuditEntry entry, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditEntry>> GetRecentAsync(
            int count,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<AuditEntry>>(Entries.Take(count).ToArray());
        }
    }

    private sealed class NoOpConnectionManager : IBotConnectionManager
    {
        public event EventHandler<BotConnectionSnapshot>? StatusChanged
        {
            add { }
            remove { }
        }

        public IReadOnlyCollection<BotConnectionSnapshot> Snapshots => [];

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<OperationResult> ConnectAsync(
            Guid botProfileId,
            CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Success());

        public Task<OperationResult> DisconnectAsync(
            Guid botProfileId,
            CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Success());

        public Task ConnectAllAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DisconnectAllAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
