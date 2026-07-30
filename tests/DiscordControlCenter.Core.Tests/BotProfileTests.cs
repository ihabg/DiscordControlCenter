using DiscordControlCenter.Core.Bots;

namespace DiscordControlCenter.Core.Tests;

public sealed class BotProfileTests
{
    [Fact]
    public void MaskedTokenDoesNotExposeAnyCredentialCharacters()
    {
        Assert.Equal("••••••••••••••••", BotProfile.MaskedToken);
        Assert.DoesNotContain('.', BotProfile.MaskedToken);
    }

    [Fact]
    public void WithIdentityPreservesProtectedCredentialAndUpdatesConnectionMetadata()
    {
        var protectedToken = new byte[] { 1, 2, 3 };
        var profile = new BotProfile(
            Guid.NewGuid(),
            "Operations",
            protectedToken,
            "ABC123",
            true,
            DateTimeOffset.UtcNow);
        var identity = new BotIdentity(42, "control-bot", "https://example.invalid/avatar.png");
        var connectedAt = DateTimeOffset.UtcNow.AddMinutes(1);

        var updated = profile.WithIdentity(identity, connectedAt);

        Assert.Same(protectedToken, updated.ProtectedToken);
        Assert.Equal((ulong)42, updated.DiscordUserId);
        Assert.Equal("control-bot", updated.DiscordUsername);
        Assert.Equal(connectedAt, updated.LastConnectedAt);
    }
}
