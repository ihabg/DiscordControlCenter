using System.Collections.Immutable;
using DiscordControlCenter.Application.Explorer;
using DiscordControlCenter.Core.Bots;
using DiscordControlCenter.Core.Explorer;
using DiscordControlCenter.Application.Messaging;
using DiscordControlCenter.Core.Messaging;

namespace DiscordControlCenter.Application.Tests;

public sealed class MessagingSafetyTests
{
    [Fact]
    public async Task ApprovalPreflightAlwaysReturnsFourteenStableSafeChecks()
    {
        var botId = Guid.NewGuid();
        var approval = Approval(botId, new MessageContent("Saved message", null, AllowedMentionPolicy.None));
        var service = new ScheduledApprovalPreflightService(
            new MemoryBotRepository(),
            new FakeConnectionManager(),
            new FakeOperationExplorer(BotExplorerSnapshot.Disconnected(botId)),
            new PermissionResolutionService());

        var result = await service.EvaluateAsync(approval, CancellationToken.None);

        Assert.Equal(14, result.Checks.Count);
        Assert.Equal(14, result.Checks.Select(check => check.Id).Distinct().Count());
        Assert.Equal(Enum.GetValues<ScheduledApprovalPreflightCheckId>(), result.Checks.Select(check => check.Id));
        Assert.All(result.Checks, check =>
        {
            Assert.False(string.IsNullOrWhiteSpace(check.Label));
            Assert.False(string.IsNullOrWhiteSpace(check.Explanation));
        });
        Assert.Equal(ScheduledApprovalPreflightState.Blocked, result.Checks[4].State);
        Assert.Equal(ScheduledApprovalPreflightState.NotRequired, result.Checks[11].State);
        Assert.Equal(ScheduledApprovalPreflightState.NotRequired, result.Checks[12].State);
        Assert.Equal(ScheduledApprovalPreflightState.NotRequired, result.Checks[13].State);
    }

    [Fact]
    public void ImmutableUsageUsesSharedLimitsAndPreservesEmbedFieldOrder()
    {
        var content = new MessageContent(
            new string('x', 1_800),
            new EmbedDraft("Title", new string('d', 4_097), null, null, "Author", null, null, null, null, "Footer", null, null,
                [new EmbedFieldDraft("First", "Value", false), new EmbedFieldDraft("Second", "Value", true)]),
            AllowedMentionPolicy.None);

        var usage = MessageLimits.GetUsage(content);

        Assert.Equal(ContentUsageState.NearLimit, Assert.Single(usage.PlainMessageRows).State);
        Assert.Equal(ContentUsageState.OverLimit, usage.EmbedRows.Single(row => row.Id == "embed.description").State);
        Assert.Equal(["embed.field.0.name", "embed.field.0.value", "embed.field.1.name", "embed.field.1.value"], usage.EmbedRows.Where(row => row.Id.StartsWith("embed.field.", StringComparison.Ordinal)).Select(row => row.Id));
        Assert.Equal(MessageLimits.MaximumMessageCharacters, usage.PlainMessageRows[0].Maximum);
    }

    [Fact]
    public void PlannerRequiresAnExplicitRecipientForDirectMessage()
    {
        var draft = new MessageDraft(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new MessageDestination(MessageDestinationKind.IndividualDirectMessage, 1, "Test", null, null, null, null),
            new MessageContent("Hello", null, AllowedMentionPolicy.None),
            ImmutableArray<MessageAttachmentReference>.Empty,
            null,
            DateTimeOffset.UtcNow);

        var result = new MessagePlanBuilder().Build(draft, MessageOperationKind.IndividualDirectMessage);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error => error.Contains("exactly one member", StringComparison.Ordinal));
    }

    [Fact]
    public void PlannerFlagsBroadMentionForStrongerConfirmation()
    {
        var draft = new MessageDraft(
            Guid.NewGuid(),
            Guid.NewGuid(),
            MessageDestination.Channel(1, "Test", 2, "welcome"),
            new MessageContent("@everyone hello", null, AllowedMentionPolicy.None),
            ImmutableArray<MessageAttachmentReference>.Empty,
            null,
            DateTimeOffset.UtcNow);

        var result = new MessagePlanBuilder().Build(draft, MessageOperationKind.ManualChannelMessage);

        Assert.True(result.IsSuccess);
        Assert.True(result.Plan!.RequiresStrongConfirmation);
        Assert.Equal("CONFIRM MESSAGE DELIVERY", result.Plan.RequiredConfirmationText);
    }

    [Fact]
    public void RendererEscapesMemberControlledMentionLikeValues()
    {
        var template = new MessageTemplate(
            Guid.NewGuid(),
            "Welcome",
            null,
            new MessageContent("Welcome {member.displayName}", null, AllowedMentionPolicy.None),
            ImmutableArray<TemplateVariableDefinition>.Empty,
            ImmutableArray<string>.Empty,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null);

        var result = new TemplateRenderer().Render(template, new Dictionary<string, string?> { ["member.displayName"] = "@everyone" });

        Assert.True(result.IsSuccess);
        Assert.Equal("Welcome @\u200Beveryone", result.Content!.Body);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void SchedulerSkipsMissedOccurrencesByDefault()
    {
        var now = DateTimeOffset.UtcNow;
        var definition = new ScheduledMessageDefinition(
            Guid.NewGuid(), Guid.NewGuid(), MessageDestination.Channel(1, "Test", 2, "welcome"), null,
            new MessageContent("Scheduled", null, AllowedMentionPolicy.None), ScheduledMessageRecurrence.Daily,
            TimeOnly.FromDateTime(now.AddHours(-1).DateTime), TimeZoneInfo.Utc.Id, ImmutableArray<DayOfWeek>.Empty,
            now.AddDays(-2), null, true, MissedOccurrencePolicy.Skip, 0, now.AddDays(-2), now);

        var due = new ScheduledMessageService().GetDueOccurrences(definition, now);

        Assert.Empty(due);
    }

    [Fact]
    public void SchedulerReturnsTheCurrentDueOccurrenceWithoutReplayingOlderOnes()
    {
        var now = new DateTimeOffset(2026, 7, 31, 12, 0, 20, TimeSpan.Zero);
        var definition = new ScheduledMessageDefinition(
            Guid.NewGuid(), Guid.NewGuid(), MessageDestination.Channel(1, "Test", 2, "welcome"), null,
            new MessageContent("Scheduled", null, AllowedMentionPolicy.None), ScheduledMessageRecurrence.Daily,
            new TimeOnly(12, 0), TimeZoneInfo.Utc.Id, ImmutableArray<DayOfWeek>.Empty,
            now.AddDays(-4), null, true, MissedOccurrencePolicy.Skip, 0, now.AddDays(-2), null);

        var due = new ScheduledMessageService().GetDueOccurrences(definition, now);

        Assert.Equal([now.Date], due.Select(item => item.Date).ToArray());
    }

    [Fact]
    public async Task DeliveryRecordsSafeOutcomeWithoutPersistingMessageContent()
    {
        var plan = new MessageOperationPlan(
            Guid.NewGuid(), Guid.NewGuid(), MessageOperationKind.ManualChannelMessage, Guid.NewGuid(),
            MessageDestination.Channel(1, "Test", 2, "welcome"),
            new MessageContent("This body must not enter delivery history", null, AllowedMentionPolicy.None),
            DateTimeOffset.UtcNow, 0, false, "Confirm delivery", null, null);
        var history = new RecordingHistory();
        var executor = new MessageDeliveryExecutor(new AllowingPreflight(), new SuccessfulWriter(), history);

        var result = await executor.DeliverAsync(plan, CancellationToken.None);

        Assert.Equal(MessageOperationState.Delivered, result.State);
        Assert.Same(plan, history.Plan);
        Assert.Same(result, history.Result);
        Assert.DoesNotContain(plan.Content.Body, history.SafeFields, StringComparison.Ordinal);
    }

    private sealed class AllowingPreflight : IMessagePreflightService
    {
        public MessagePreflightResult Validate(MessageOperationPlan plan) =>
            new(true, false, [], DateTimeOffset.UtcNow);
    }

    private static ScheduledMessageApproval Approval(Guid botId, MessageContent content)
    {
        var definition = new ScheduledMessageDefinition(
            Guid.NewGuid(), botId, MessageDestination.Channel(1, "Test", 2, "general"), null, content,
            ScheduledMessageRecurrence.Daily, new TimeOnly(12, 0), TimeZoneInfo.Utc.Id,
            ImmutableArray<DayOfWeek>.Empty, DateTimeOffset.UtcNow, null, true,
            MissedOccurrencePolicy.RequireManualApproval, 0, null, null);
        return new ScheduledMessageApproval(
            new ScheduledMessageOccurrence(Guid.NewGuid(), definition.Id, DateTimeOffset.UtcNow, MessageOperationState.PendingApproval, Guid.NewGuid(), null, null), definition)
        {
            ImmutableContent = content,
            Compatibility = SnapshotCompatibility.Supported
        };
    }

    private sealed class SuccessfulWriter : IDiscordMessageWriter
    {
        public Task<MessageWriteOutcome> SendChannelMessageAsync(MessageOperationPlan plan, CancellationToken cancellationToken) =>
            Task.FromResult(new MessageWriteOutcome(true, 42, null));

        public Task<MessageWriteOutcome> SendDirectMessageAsync(MessageOperationPlan plan, CancellationToken cancellationToken) =>
            Task.FromResult(new MessageWriteOutcome(true, 42, null));
    }

    private sealed class RecordingHistory : IDeliveryHistoryRepository
    {
        public MessageOperationPlan? Plan { get; private set; }
        public MessageDeliveryResult? Result { get; private set; }
        public string SafeFields => string.Join('|', Plan?.OperationId, Result?.State, Result?.Failure?.SafeCode);

        public Task RecordAsync(MessageOperationPlan plan, MessageDeliveryResult result, CancellationToken cancellationToken)
        {
            Plan = plan;
            Result = result;
            return Task.CompletedTask;
        }
    }
}
