using DiscordControlCenter.Core.Persistence;
using Microsoft.Extensions.Logging;

namespace DiscordControlCenter.Infrastructure.Persistence;

public sealed class SqliteDatabaseInitializer(
    SqliteConnectionFactory connectionFactory,
    ILogger<SqliteDatabaseInitializer> logger) : IDatabaseInitializer
{
    private const int CurrentSchemaVersion = 3;

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

        await using (var versionCommand = connection.CreateCommand())
        {
            versionCommand.Transaction = transaction;
            versionCommand.CommandText =
                """
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

    private static readonly Action<ILogger, int, Exception?> DatabaseReadyLog =
        LoggerMessage.Define<int>(
            LogLevel.Information,
            new EventId(1001, nameof(DatabaseReadyLog)),
            "SQLite schema version {SchemaVersion} is ready");
}
