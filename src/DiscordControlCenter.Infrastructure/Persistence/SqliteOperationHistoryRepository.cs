using System.Globalization;
using System.Text;
using DiscordControlCenter.Core.Operations;

namespace DiscordControlCenter.Infrastructure.Persistence;

public sealed class SqliteOperationHistoryRepository(
    SqliteConnectionFactory connectionFactory) : IOperationHistoryRepository
{
    public Task AddAsync(
        OperationHistoryEntry entry,
        CancellationToken cancellationToken) =>
        SaveAsync(entry, updateExisting: false, cancellationToken);

    public Task UpdateAsync(
        OperationHistoryEntry entry,
        CancellationToken cancellationToken) =>
        SaveAsync(entry, updateExisting: true, cancellationToken);

    public async Task<OperationHistoryEntry?> GetAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            {SelectColumns}
            FROM OperationHistory
            WHERE OperationId = $operationId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$operationId", operationId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? Read(reader)
            : null;
    }

    public async Task<IReadOnlyList<OperationHistoryEntry>> GetRecentAsync(
        int count,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            {SelectColumns}
            FROM OperationHistory
            ORDER BY CreatedAt DESC
            LIMIT $count;
            """;
        command.Parameters.AddWithValue("$count", count);
        return await ReadManyAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<OperationHistoryEntry>> GetInterruptedAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            {SelectColumns}
            FROM OperationHistory
            WHERE State IN ('Pending', 'Running', 'Waiting', 'Cancelling')
            ORDER BY CreatedAt;
            """;
        return await ReadManyAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveAsync(
        OperationHistoryEntry entry,
        bool updateExisting,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO OperationHistory
                (OperationId, CorrelationId, PlanType, BotProfileId, ServerId, ServerName,
                 TargetIds, SafeDisplayNames, CreatedAt, StartedAt, FinishedAt, State,
                 CompletedCount, FailedCount, CancelledCount, CompensationSummary,
                 BackupIdentifier, SafeErrorCodes, DurationMilliseconds, AuditReason,
                 PlanJson, ResultJson)
            VALUES
                ($operationId, $correlationId, $planType, $botProfileId, $serverId, $serverName,
                 $targetIds, $safeDisplayNames, $createdAt, $startedAt, $finishedAt, $state,
                 $completedCount, $failedCount, $cancelledCount, $compensationSummary,
                 $backupIdentifier, $safeErrorCodes, $duration, $auditReason,
                 $planJson, $resultJson)
            ON CONFLICT(OperationId) DO UPDATE SET
                StartedAt = excluded.StartedAt,
                FinishedAt = excluded.FinishedAt,
                State = excluded.State,
                CompletedCount = excluded.CompletedCount,
                FailedCount = excluded.FailedCount,
                CancelledCount = excluded.CancelledCount,
                CompensationSummary = excluded.CompensationSummary,
                BackupIdentifier = excluded.BackupIdentifier,
                SafeErrorCodes = excluded.SafeErrorCodes,
                DurationMilliseconds = excluded.DurationMilliseconds,
                AuditReason = excluded.AuditReason,
                ResultJson = excluded.ResultJson;
            """;
        if (!updateExisting)
        {
            var conflictIndex = command.CommandText.IndexOf(
                "ON CONFLICT",
                StringComparison.Ordinal);
            command.CommandText = $"{command.CommandText[..conflictIndex].TrimEnd()};";
        }

        command.Parameters.AddWithValue("$operationId", entry.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$correlationId", entry.CorrelationId.ToString("D"));
        command.Parameters.AddWithValue("$planType", entry.OperationType.ToString());
        command.Parameters.AddWithValue("$botProfileId", entry.BotProfileId.ToString("D"));
        command.Parameters.AddWithValue("$serverId", entry.ServerId.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$serverName", entry.ServerName);
        command.Parameters.AddWithValue("$targetIds", entry.TargetIds);
        command.Parameters.AddWithValue("$safeDisplayNames", entry.SafeDisplayNames);
        command.Parameters.AddWithValue("$createdAt", entry.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue(
            "$startedAt",
            entry.StartedAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$finishedAt",
            entry.FinishedAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$state", entry.State.ToString());
        command.Parameters.AddWithValue("$completedCount", entry.CompletedCount);
        command.Parameters.AddWithValue("$failedCount", entry.FailedCount);
        command.Parameters.AddWithValue("$cancelledCount", entry.CancelledCount);
        command.Parameters.AddWithValue("$compensationSummary", entry.CompensationSummary);
        command.Parameters.AddWithValue(
            "$backupIdentifier",
            entry.BackupIdentifier ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$safeErrorCodes",
            entry.SafeErrorCodes ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$duration", entry.DurationMilliseconds);
        command.Parameters.AddWithValue("$auditReason", entry.AuditReason ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$planJson", entry.PlanJson);
        command.Parameters.AddWithValue("$resultJson", entry.ResultJson ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<OperationHistoryEntry>> ReadManyAsync(
        Microsoft.Data.Sqlite.SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var entries = new List<OperationHistoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(Read(reader));
        }

        return entries;
    }

    internal static OperationHistoryEntry Read(Microsoft.Data.Sqlite.SqliteDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            Enum.Parse<ChannelOperationType>(reader.GetString(2)),
            Guid.Parse(reader.GetString(3)),
            ulong.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            ParseDate(reader.GetString(8)),
            reader.IsDBNull(9) ? null : ParseDate(reader.GetString(9)),
            reader.IsDBNull(10) ? null : ParseDate(reader.GetString(10)),
            Enum.Parse<ChannelOperationState>(reader.GetString(11)),
            reader.GetInt32(12),
            reader.GetInt32(13),
            reader.GetInt32(14),
            reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.IsDBNull(17) ? null : reader.GetString(17),
            reader.GetInt64(18),
            reader.IsDBNull(19) ? null : reader.GetString(19),
            reader.GetString(20),
            reader.IsDBNull(21) ? null : reader.GetString(21));

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    internal const string SelectColumns =
        """
        SELECT OperationId, CorrelationId, PlanType, BotProfileId, ServerId, ServerName,
               TargetIds, SafeDisplayNames, CreatedAt, StartedAt, FinishedAt, State,
               CompletedCount, FailedCount, CancelledCount, CompensationSummary,
               BackupIdentifier, SafeErrorCodes, DurationMilliseconds, AuditReason,
               PlanJson, ResultJson
        """;
}

public sealed class SqliteOperationBackupRepository(
    SqliteConnectionFactory connectionFactory) : IOperationBackupRepository
{
    public async Task SaveAsync(
        ServerStructureBackup backup,
        CancellationToken cancellationToken)
    {
        var json = OperationJson.Serialize(backup);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO OperationBackups
                (BackupIdentifier, OperationId, CorrelationId, BotProfileId, ServerId,
                 ServerName, ExplorerSequence, CreatedAt, SnapshotJson)
            VALUES
                ($backupIdentifier, $operationId, $correlationId, $botProfileId, $serverId,
                 $serverName, $explorerSequence, $createdAt, $snapshotJson);
            """;
        command.Parameters.AddWithValue("$backupIdentifier", backup.BackupIdentifier);
        command.Parameters.AddWithValue("$operationId", backup.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$correlationId", backup.CorrelationId.ToString("D"));
        command.Parameters.AddWithValue("$botProfileId", backup.BotProfileId.ToString("D"));
        command.Parameters.AddWithValue("$serverId", backup.ServerId.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$serverName", backup.ServerName);
        command.Parameters.AddWithValue("$explorerSequence", backup.ExplorerSequence);
        command.Parameters.AddWithValue("$createdAt", backup.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$snapshotJson", json);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var metadataCommand = connection.CreateCommand();
        metadataCommand.Transaction = transaction;
        metadataCommand.CommandText =
            """
            INSERT INTO BackupCatalogMetadata
                (BackupIdentifier, BackupReason, SourceOperationType, CategoryCount,
                 ChannelCount, PermissionOverwriteCount, SchemaVersion, IsPinned,
                 SizeBytes, IsCorrupt, SafeIssue)
            VALUES
                ($identifier, $reason, $sourceType, $categoryCount, $channelCount,
                 $overwriteCount, $schemaVersion, 0, $sizeBytes, 0, NULL);
            """;
        var categoryCount = backup.Channels.Count(channel => channel.Kind == Core.Explorer.ChannelKind.Category);
        metadataCommand.Parameters.AddWithValue("$identifier", backup.BackupIdentifier);
        metadataCommand.Parameters.AddWithValue("$reason", backup.BackupReason);
        metadataCommand.Parameters.AddWithValue("$sourceType", backup.SourceOperationType.ToString());
        metadataCommand.Parameters.AddWithValue("$categoryCount", categoryCount);
        metadataCommand.Parameters.AddWithValue("$channelCount", backup.Channels.Length - categoryCount);
        metadataCommand.Parameters.AddWithValue(
            "$overwriteCount",
            backup.Channels.Sum(channel => channel.PermissionOverwrites.Length));
        metadataCommand.Parameters.AddWithValue("$schemaVersion", backup.SchemaVersion);
        metadataCommand.Parameters.AddWithValue("$sizeBytes", Encoding.UTF8.GetByteCount(json));
        await metadataCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ServerStructureBackup?> GetAsync(
        string backupIdentifier,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT SnapshotJson
            FROM OperationBackups
            WHERE BackupIdentifier = $backupIdentifier
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$backupIdentifier", backupIdentifier);
        var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return json is null
            ? null
            : OperationJson.Deserialize<ServerStructureBackup>(json);
    }
}

internal static class OperationJson
{
    private static readonly System.Text.Json.JsonSerializerOptions Options =
        new(System.Text.Json.JsonSerializerDefaults.General)
        {
            WriteIndented = false
        };

    public static string Serialize<T>(T value) =>
        System.Text.Json.JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string value) =>
        System.Text.Json.JsonSerializer.Deserialize<T>(value, Options);
}
