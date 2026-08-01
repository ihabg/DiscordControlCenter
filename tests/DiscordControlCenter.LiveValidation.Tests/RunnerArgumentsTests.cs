using DiscordControlCenter.LiveValidation;

namespace DiscordControlCenter.LiveValidation.Tests;

public sealed class RunnerArgumentsTests
{
    [Fact]
    public void ExactExplicitGuardIsAccepted() => Assert.True(RunnerArguments.TryParse(["--server-name", "teast", "--confirm", "VALIDATE TEAST", "--allow-discord-write", "true"], out _, out _));

    [Theory]
    [InlineData("test")]
    [InlineData("")]
    public void OtherServerNamesAreRejected(string name) => Assert.False(RunnerArguments.TryParse(["--server-name", name, "--confirm", "VALIDATE TEAST", "--allow-discord-write", "true"], out _, out _));

    [Fact]
    public void MissingConfirmationIsRejected() => Assert.False(RunnerArguments.TryParse(["--server-name", "teast", "--allow-discord-write", "true"], out _, out _));

    [Fact]
    public void MissingWriteFlagIsRejected() => Assert.False(RunnerArguments.TryParse(["--server-name", "teast", "--confirm", "VALIDATE TEAST"], out _, out _));

    [Fact]
    public void UnrelatedApplicationTextIsRejected() => Assert.False(RunnerArguments.TryParse(["--server-name", "teast", "--confirm", "VALIDATE TEAST", "--allow-discord-write", "soundpad"], out _, out _));
}
