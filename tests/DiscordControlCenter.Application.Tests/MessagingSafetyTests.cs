using System.Collections.Immutable;
using DiscordControlCenter.Application.Messaging;
using DiscordControlCenter.Core.Messaging;

namespace DiscordControlCenter.Application.Tests;

public sealed class MessagingSafetyTests
{
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
