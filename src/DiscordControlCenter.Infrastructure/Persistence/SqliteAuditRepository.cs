using System.Globalization;
using DiscordControlCenter.Core.Auditing;

namespace DiscordControlCenter.Infrastructure.Persistence;

public sealed class SqliteAuditRepository(SqliteConnectionFactory connectionFactory) : IAuditRepository
{
    public async Task AddAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO AuditEntries
                (Id, Timestamp, BotProfileId, ActionType, Target, Status, Description,
                 ErrorSummary, DurationMilliseconds, CorrelationId)
            VALUES
                ($id, $timestamp, $botProfileId, $actionType, $target, $status, $description,
                 $errorSummary, $duration, $correlationId);
            """;
        command.Parameters.AddWithValue("$id", entry.Id.ToString("D"));
        command.Parameters.AddWithValue("$timestamp", entry.Timestamp.ToString("O"));
        command.Parameters.AddWithValue(
            "$botProfileId",
            entry.BotProfileId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$actionType", entry.ActionType);
        command.Parameters.AddWithValue("$target", entry.Target);
        command.Parameters.AddWithValue("$status", entry.Status);
        command.Parameters.AddWithValue("$description", entry.Description);
        command.Parameters.AddWithValue("$errorSummary", entry.ErrorSummary ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$duration", entry.DurationMilliseconds);
        command.Parameters.AddWithValue("$correlationId", entry.CorrelationId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AuditEntry>> GetRecentAsync(
        int count,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        var entries = new List<AuditEntry>();
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, Timestamp, BotProfileId, ActionType, Target, Status, Description,
                   ErrorSummary, DurationMilliseconds, CorrelationId
            FROM AuditEntries
            ORDER BY Timestamp DESC
            LIMIT $count;
            """;
        command.Parameters.AddWithValue("$count", count);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new AuditEntry(
                Guid.Parse(reader.GetString(0)),
                DateTimeOffset.Parse(
                    reader.GetString(1),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
                reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetInt64(8),
                Guid.Parse(reader.GetString(9))));
        }

        return entries;
    }
}
