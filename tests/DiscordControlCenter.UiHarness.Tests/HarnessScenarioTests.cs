using DiscordControlCenter.Application.Messaging;
using DiscordControlCenter.UiHarness;

namespace DiscordControlCenter.UiHarness.Tests;

public sealed class HarnessScenarioTests
{
    [Fact]
    public void SeedScenariosAreDeterministicAndCoverTheRequestedCases()
    {
        var first = HarnessScenario.CreateAll();
        var second = HarnessScenario.CreateAll();

        Assert.Equal(13, first.Count);
        Assert.Equal(first.Select(item => item.Name), second.Select(item => item.Name));
        Assert.Contains(first, item => item.Kind == HarnessScenarioKind.BroadMention);
        Assert.Contains(first, item => item.Kind == HarnessScenarioKind.Disconnected);
        Assert.Contains(first, item => item.Kind == HarnessScenarioKind.Unsupported);
    }

    [Fact]
    public void EveryScenarioRepresentsAllFourteenOrderedPreflightChecks()
    {
        foreach (var scenario in HarnessScenario.CreateAll())
        {
            var usage = MessageLimits.GetUsage(scenario.CreateApproval().ImmutableContent);
            var checks = scenario.CreateChecks(connected: true, sendMessages: true, usage);

            Assert.Equal(14, checks.Count);
            Assert.Equal(Enum.GetValues<ScheduledApprovalPreflightCheckId>(), checks.Select(item => item.Id));
        }
    }

    [Fact]
    public void UsageStatesAndBroadMentionConfirmationAreRepresented()
    {
        var near = HarnessScenario.CreateAll().Single(item => item.Kind == HarnessScenarioKind.NearLimit).CreateApproval();
        var over = HarnessScenario.CreateAll().Single(item => item.Kind == HarnessScenarioKind.OverLimit).CreateApproval();
        var broad = HarnessScenario.CreateAll().Single(item => item.Kind == HarnessScenarioKind.BroadMention).CreateApproval();

        Assert.Contains(MessageLimits.GetUsage(near.ImmutableContent).PlainMessageRows, item => item.State == ContentUsageState.NearLimit);
        Assert.Contains(MessageLimits.GetUsage(over.ImmutableContent).PlainMessageRows, item => item.State == ContentUsageState.OverLimit && item.BlocksApproval);
        Assert.True(broad.ImmutableContent!.AllowedMentions.HasBroadMentions);
    }

    [Fact]
    public void HarnessIsNotPackableOrPublishableAsASingleProductionArtifact()
    {
        var project = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "DiscordControlCenter.UiHarness", "DiscordControlCenter.UiHarness.csproj"));

        Assert.Contains("<IsPackable>false</IsPackable>", project, StringComparison.Ordinal);
        Assert.Contains("<PublishSingleFile>false</PublishSingleFile>", project, StringComparison.Ordinal);
    }
}
