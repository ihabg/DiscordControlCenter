using DiscordControlCenter.Core.Messaging;

namespace DiscordControlCenter.Core.Tests;

public sealed class ScheduledApprovalQueryTests
{
    [Fact]
    public void HistoryQueryCarriesIndependentDecisionBoundsAndStableSort()
    {
        var from = DateTimeOffset.UtcNow.AddDays(-2);
        var to = DateTimeOffset.UtcNow;
        var query = new ScheduledApprovalQuery(null, Guid.NewGuid(), 42, Guid.NewGuid(), MessageOperationState.Uncertain, null, null, null, ScheduledApprovalSort.DecisionOldest, 2, 25)
        {
            HistoryOnly = true,
            FromDecision = from,
            ToDecision = to,
            RequiresManualReview = true
        };

        Assert.True(query.HistoryOnly);
        Assert.Equal(ScheduledApprovalSort.DecisionOldest, query.Sort);
        Assert.Equal(from, query.FromDecision);
        Assert.Equal(to, query.ToDecision);
        Assert.True(query.RequiresManualReview);
    }

    [Fact]
    public void DeletedScheduleUsesFriendlyDisplayName()
    {
        var option = new ScheduledApprovalScheduleOption(Guid.NewGuid(), "Prior schedule", true);

        Assert.Equal("Deleted or unavailable schedule", option.DisplayName);
    }
}
