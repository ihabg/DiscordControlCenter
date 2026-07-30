namespace DiscordControlCenter.Core.Auditing;

public sealed record AuditEntry(
    Guid Id,
    DateTimeOffset Timestamp,
    Guid? BotProfileId,
    string ActionType,
    string Target,
    string Status,
    string Description,
    string? ErrorSummary,
    long DurationMilliseconds,
    Guid CorrelationId);
