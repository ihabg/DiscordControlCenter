using DiscordControlCenter.Core.Persistence;
using DiscordControlCenter.Core.Explorer;
using DiscordControlCenter.Core.Operations;
using Microsoft.Extensions.Logging;

namespace DiscordControlCenter.Infrastructure.Persistence;

public sealed class SqliteDatabaseInitializer(
    SqliteConnectionFactory connectionFactory,
    ILogger<SqliteDatabaseInitializer> logger) : IDatabaseInitializer
{
    private const int CurrentSchemaVersion = 4;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var journalCommand = connection.CreateCommand())
        {
            journalCommand.CommandText = "PRAGMA journal_mode = WAL;";
            await journalCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS SchemaVersions (
                Version INTEGER NOT NULL PRIMARY KEY,
                AppliedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS BotProfiles (
                Id TEXT NOT NULL PRIMARY KEY,
                DisplayName TEXT NOT NULL,
                ProtectedToken BLOB NOT NULL,
                TokenFingerprint TEXT NOT NULL,
                IsEnabled INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL,
                DiscordUserId TEXT NULL,
                DiscordUsername TEXT NULL,
                AvatarUrl TEXT NULL,
                LastConnectedAt TEXT NULL,
                EnableFullMemberAccess INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS AuditEntries (
                Id TEXT NOT NULL PRIMARY KEY,
                Timestamp TEXT NOT NULL,
                BotProfileId TEXT NULL,
                ActionType TEXT NOT NULL,
                Target TEXT NOT NULL,
                Status TEXT NOT NULL,
                Description TEXT NOT NULL,
                ErrorSummary TEXT NULL,
                DurationMilliseconds INTEGER NOT NULL,
                CorrelationId TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_AuditEntries_Timestamp
                ON AuditEntries (Timestamp DESC);

            CREATE UNIQUE INDEX IF NOT EXISTS UX_BotProfiles_DiscordUserId
                ON BotProfiles (DiscordUserId)
                WHERE DiscordUserId IS NOT NULL;

            INSERT OR IGNORE INTO SchemaVersions (Version, AppliedAt)
                VALUES (1, $appliedAt);
            """;
        command.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var hasMemberIntentColumn = false;
        await using (var columnCommand = connection.CreateCommand())
        {
            columnCommand.Transaction = transaction;
            columnCommand.CommandText = "PRAGMA table_info(BotProfiles);";
            await using var reader = await columnCommand
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(
                        reader.GetString(1),
                        "EnableFullMemberAccess",
                        StringComparison.OrdinalIgnoreCase))
                {
                    hasMemberIntentColumn = true;
                    break;
                }
            }
        }

        if (!hasMemberIntentColumn)
        {
            await using var migrationCommand = connection.CreateCommand();
            migrationCommand.Transaction = transaction;
            migrationCommand.CommandText =
                """
                ALTER TABLE BotProfiles
                    ADD COLUMN EnableFullMemberAccess INTEGER NOT NULL DEFAULT 0;
                """;
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var operationMigrationCommand = connection.CreateCommand())
        {
            operationMigrationCommand.Transaction = transaction;
            operationMigrationCommand.CommandText =
                """
                CREATE TABLE IF NOT EXISTS OperationHistory (
                    OperationId TEXT NOT NULL PRIMARY KEY,
                    CorrelationId TEXT NOT NULL,
                    PlanType TEXT NOT NULL,
                    BotProfileId TEXT NOT NULL,
                    ServerId TEXT NOT NULL,
                    ServerName TEXT NOT NULL,
                    TargetIds TEXT NOT NULL,
                    SafeDisplayNames TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    StartedAt TEXT NULL,
                    FinishedAt TEXT NULL,
                    State TEXT NOT NULL,
                    CompletedCount INTEGER NOT NULL,
                    FailedCount INTEGER NOT NULL,
                    CancelledCount INTEGER NOT NULL,
                    CompensationSummary TEXT NOT NULL,
                    BackupIdentifier TEXT NULL,
                    SafeErrorCodes TEXT NULL,
                    DurationMilliseconds INTEGER NOT NULL,
                    AuditReason TEXT NULL,
                    PlanJson TEXT NOT NULL,
                    ResultJson TEXT NULL
                );

                CREATE INDEX IF NOT EXISTS IX_OperationHistory_CreatedAt
                    ON OperationHistory (CreatedAt DESC);

                CREATE INDEX IF NOT EXISTS IX_OperationHistory_BotServer
                    ON OperationHistory (BotProfileId, ServerId, CreatedAt DESC);

                CREATE TABLE IF NOT EXISTS OperationBackups (
                    BackupIdentifier TEXT NOT NULL PRIMARY KEY,
                    OperationId TEXT NOT NULL,
                    CorrelationId TEXT NOT NULL,
                    BotProfileId TEXT NOT NULL,
                    ServerId TEXT NOT NULL,
                    ServerName TEXT NOT NULL,
                    ExplorerSequence INTEGER NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    SnapshotJson TEXT NOT NULL
                );

                CREATE UNIQUE INDEX IF NOT EXISTS UX_OperationBackups_OperationId
                    ON OperationBackups (OperationId);
                """;
            await operationMigrationCommand
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        await using (var recoveryMigrationCommand = connection.CreateCommand())
        {
            recoveryMigrationCommand.Transaction = transaction;
            recoveryMigrationCommand.CommandText =
                """
                CREATE TABLE IF NOT EXISTS BackupCatalogMetadata (
                    BackupIdentifier TEXT NOT NULL PRIMARY KEY,
                    BackupReason TEXT NOT NULL,
                    SourceOperationType TEXT NOT NULL,
                    CategoryCount INTEGER NOT NULL,
                    ChannelCount INTEGER NOT NULL,
                    PermissionOverwriteCount INTEGER NOT NULL,
                    SchemaVersion INTEGER NOT NULL,
                    IsPinned INTEGER NOT NULL,
                    SizeBytes INTEGER NOT NULL,
                    IsCorrupt INTEGER NOT NULL,
                    SafeIssue TEXT NULL
                );

                CREATE INDEX IF NOT EXISTS IX_BackupCatalog_ServerCreated
                    ON OperationBackups (ServerId, CreatedAt DESC);

                CREATE INDEX IF NOT EXISTS IX_BackupCatalog_BotCreated
                    ON OperationBackups (BotProfileId, CreatedAt DESC);

                CREATE TABLE IF NOT EXISTS BackupRetentionSettings (
                    Id INTEGER NOT NULL PRIMARY KEY CHECK (Id = 1),
                    KeepIndefinitely INTEGER NOT NULL,
                    MaximumAgeDays INTEGER NULL,
                    NewestPerServer INTEGER NULL,
                    PreserveFailedOperationBackups INTEGER NOT NULL,
                    MaximumStorageBytes INTEGER NULL,
                    UpdatedAt TEXT NOT NULL
                );

                INSERT OR IGNORE INTO BackupRetentionSettings
                    (Id, KeepIndefinitely, MaximumAgeDays, NewestPerServer,
                     PreserveFailedOperationBackups, MaximumStorageBytes, UpdatedAt)
                VALUES (1, 1, NULL, NULL, 1, NULL, $phase4bAppliedAt);

                CREATE TABLE IF NOT EXISTS OperationStateTransitions (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    OperationId TEXT NOT NULL,
                    State TEXT NOT NULL,
                    Timestamp TEXT NOT NULL,
                    ReasonCode TEXT NOT NULL,
                    SafeSummary TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS IX_OperationTransitions_OperationTime
                    ON OperationStateTransitions (OperationId, Timestamp);

                CREATE TABLE IF NOT EXISTS ManualReconciliationDecisions (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    OperationId TEXT NOT NULL,
                    CorrelationId TEXT NOT NULL,
                    StepId TEXT NOT NULL,
                    Resolution TEXT NOT NULL,
                    Timestamp TEXT NOT NULL,
                    SafeExplanation TEXT NOT NULL,
                    RelevantResourceIds TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS IX_ManualReconciliation_OperationTime
                    ON ManualReconciliationDecisions (OperationId, Timestamp);

                CREATE TABLE IF NOT EXISTS BackupCleanupAudit (
                    Id TEXT NOT NULL PRIMARY KEY,
                    Timestamp TEXT NOT NULL,
                    BackupIdentifiers TEXT NOT NULL,
                    DeletedCount INTEGER NOT NULL,
                    ReclaimedBytes INTEGER NOT NULL,
                    SafeReason TEXT NOT NULL
                );
                """;
            recoveryMigrationCommand.Parameters.AddWithValue(
                "$phase4bAppliedAt",
                DateTimeOffset.UtcNow.ToString("O"));
            await recoveryMigrationCommand
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        await BackfillBackupCatalogAsync(
                connection,
                transaction,
                cancellationToken)
            .ConfigureAwait(false);

        await using (var versionCommand = connection.CreateCommand())
        {
            versionCommand.Transaction = transaction;
            versionCommand.CommandText =
                """
                INSERT OR IGNORE INTO SchemaVersions (Version, AppliedAt)
                    VALUES (2, $appliedAt);
                INSERT OR IGNORE INTO SchemaVersions (Version, AppliedAt)
                    VALUES (3, $appliedAt);
                INSERT OR IGNORE INTO SchemaVersions (Version, AppliedAt)
                    VALUES ($version, $appliedAt);
                """;
            versionCommand.Parameters.AddWithValue("$version", CurrentSchemaVersion);
            versionCommand.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
            await versionCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        DatabaseReadyLog(logger, CurrentSchemaVersion, null);
    }

    private static async Task BackfillBackupCatalogAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var rows = new List<(string Identifier, string Json, string SourceType, long SizeBytes)>();
        await using (var readCommand = connection.CreateCommand())
        {
            readCommand.Transaction = transaction;
            readCommand.CommandText =
                """
                SELECT b.BackupIdentifier, b.SnapshotJson,
                       COALESCE(h.PlanType, 'DeleteChannels'),
                       length(CAST(b.SnapshotJson AS BLOB))
                FROM OperationBackups b
                LEFT JOIN OperationHistory h ON h.OperationId = b.OperationId
                WHERE NOT EXISTS (
                    SELECT 1 FROM BackupCatalogMetadata m
                    WHERE m.BackupIdentifier = b.BackupIdentifier);
                """;
            await using var reader = await readCommand
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(
                    (
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetInt64(3)
                    ));
            }
        }

        foreach (var row in rows)
        {
            var categoryCount = 0;
            var channelCount = 0;
            var overwriteCount = 0;
            var schemaVersion = 1;
            var isCorrupt = false;
            string? safeIssue = null;
            try
            {
                var backup = OperationJson.Deserialize<ServerStructureBackup>(row.Json)
                    ?? throw new InvalidDataException("Backup data is empty.");
                categoryCount = backup.Channels.Count(channel => channel.Kind == ChannelKind.Category);
                channelCount = backup.Channels.Length - categoryCount;
                overwriteCount = backup.Channels.Sum(channel => channel.PermissionOverwrites.Length);
                schemaVersion = backup.SchemaVersion;
            }
            catch (Exception exception) when (
                exception is System.Text.Json.JsonException
                    or InvalidDataException
                    or NotSupportedException)
            {
                isCorrupt = true;
                safeIssue = "The structural backup could not be parsed.";
            }

            await using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText =
                """
                INSERT OR IGNORE INTO BackupCatalogMetadata
                    (BackupIdentifier, BackupReason, SourceOperationType, CategoryCount,
                     ChannelCount, PermissionOverwriteCount, SchemaVersion, IsPinned,
                     SizeBytes, IsCorrupt, SafeIssue)
                VALUES
                    ($identifier, $reason, $sourceType, $categoryCount, $channelCount,
                     $overwriteCount, $schemaVersion, 0, $sizeBytes, $isCorrupt, $safeIssue);
                """;
            insertCommand.Parameters.AddWithValue("$identifier", row.Identifier);
            insertCommand.Parameters.AddWithValue("$reason", "Pre-operation structural backup");
            insertCommand.Parameters.AddWithValue("$sourceType", row.SourceType);
            insertCommand.Parameters.AddWithValue("$categoryCount", categoryCount);
            insertCommand.Parameters.AddWithValue("$channelCount", channelCount);
            insertCommand.Parameters.AddWithValue("$overwriteCount", overwriteCount);
            insertCommand.Parameters.AddWithValue("$schemaVersion", schemaVersion);
            insertCommand.Parameters.AddWithValue("$sizeBytes", row.SizeBytes);
            insertCommand.Parameters.AddWithValue("$isCorrupt", isCorrupt ? 1 : 0);
            insertCommand.Parameters.AddWithValue("$safeIssue", safeIssue ?? (object)DBNull.Value);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static readonly Action<ILogger, int, Exception?> DatabaseReadyLog =
        LoggerMessage.Define<int>(
            LogLevel.Information,
            new EventId(1001, nameof(DatabaseReadyLog)),
            "SQLite schema version {SchemaVersion} is ready");
}
