using DiscordControlCenter.Application.Messaging;
using DiscordControlCenter.Core.Messaging;
using DiscordControlCenter.UiHarness;

namespace DiscordControlCenter.UiHarness.Tests;

public sealed class HarnessScenarioTests
{
    [Fact]
    public void SeedScenariosAreDeterministicAndCoverTheRequestedCases()
    {
        var first = HarnessScenario.CreateAll();
        var second = HarnessScenario.CreateAll();

        Assert.Equal(64, first.Count);
        Assert.Equal(first.Select(item => item.Name), second.Select(item => item.Name));
        Assert.Contains(first, item => item.Kind == HarnessScenarioKind.BroadMention);
        Assert.Contains(first, item => item.Kind == HarnessScenarioKind.Disconnected);
        Assert.Contains(first, item => item.Kind == HarnessScenarioKind.Unsupported);
        Assert.Contains(first, item => item.Kind == HarnessScenarioKind.Delivered);
        Assert.Contains(first, item => item.Kind == HarnessScenarioKind.Uncertain);
        Assert.Contains(first, item => item.Kind == HarnessScenarioKind.Archived);
        Assert.Contains(first, item => item.Kind == HarnessScenarioKind.NoBotServerScope);
        Assert.Contains(first, item => item.Kind == HarnessScenarioKind.ScheduleFaulted);
        Assert.Contains(first, item => item.Kind == HarnessScenarioKind.ScheduleInvalidTimeZone);
        Assert.Contains(first, item => item.Kind == HarnessScenarioKind.OccurrenceUncertain);
        Assert.Contains(first, item => item.Kind == HarnessScenarioKind.ScheduleOccurrenceError);
        Assert.Contains(first, item => item.Kind == HarnessScenarioKind.DraftNewBlank);
        Assert.Contains(first, item => item.Kind == HarnessScenarioKind.DraftScopedTemplates);
        Assert.Contains(first, item => item.Kind == HarnessScenarioKind.DraftMissingTemplate);
        Assert.Contains(first, item => item.Kind == HarnessScenarioKind.DraftInvalidZone);
        Assert.Contains(first, item => item.Kind == HarnessScenarioKind.DraftConflict);
        Assert.Contains(first, item => item.Kind == HarnessScenarioKind.DraftNarrow);
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

    [Fact]
    public async Task DraftHarnessUsesScopedFriendlyOptionsAndNeverResolvesProductionServices()
    {
        var service = new HarnessScheduledMessageDraftService { Scenario = new HarnessScenario("Scoped", HarnessScenarioKind.DraftScopedTemplates) };
        var options = await service.GetTemplateOptionsAsync(HarnessScheduledMessageQueryService.BotId, HarnessScheduledMessageQueryService.ServerId, CancellationToken.None);

        Assert.Equal("Harness scoped template", Assert.Single(options).Name);
        Assert.DoesNotContain("token", string.Join(' ', options.Select(item => item.Name)), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(typeof(HarnessScheduledMessageDraftService).GetConstructors().SelectMany(item => item.GetParameters()), item => item.ParameterType.Name.Contains("Writer", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(HarnessScenarioKind.Delivered, MessageOperationState.Delivered)]
    [InlineData(HarnessScenarioKind.Failed, MessageOperationState.Failed)]
    [InlineData(HarnessScenarioKind.Uncertain, MessageOperationState.Uncertain)]
    [InlineData(HarnessScenarioKind.Skipped, MessageOperationState.Skipped)]
    [InlineData(HarnessScenarioKind.Archived, MessageOperationState.Archived)]
    public void TerminalHistoryScenariosExposeTheExpectedSafeState(HarnessScenarioKind kind, MessageOperationState state)
    {
        var approval = HarnessScenario.CreateAll().Single(item => item.Kind == kind).CreateApproval();

        Assert.Equal(state, approval.Occurrence.State);
        Assert.DoesNotContain("token", approval.Snapshot.Destination.ChannelName ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }
}
