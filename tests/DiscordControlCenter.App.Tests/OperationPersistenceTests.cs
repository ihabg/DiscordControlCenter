using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using DiscordControlCenter.Core.Explorer;
using DiscordControlCenter.Core.Messaging;
using DiscordControlCenter.Core.Operations;
using DiscordControlCenter.Infrastructure.Configuration;
using DiscordControlCenter.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordControlCenter.App.Tests;

public sealed class OperationPersistenceTests
{
    [Fact]
    public async Task MigrationCreatesOperationalRecoveryAndMessagingTables()
    {
        await using var database = await TestDatabase.CreateAsync();

        var versions = await database.ReadStringsAsync(
            "SELECT CAST(Version AS TEXT) FROM SchemaVersions ORDER BY Version;");
        var tables = await database.ReadStringsAsync(
            "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;");

        Assert.Contains("3", versions);
        Assert.Contains("4", versions);
        Assert.Contains("OperationHistory", tables);
        Assert.Contains("OperationBackups", tables);
        Assert.Contains("BackupCatalogMetadata", tables);
        Assert.Contains("BackupRetentionSettings", tables);
        Assert.Contains("OperationStateTransitions", tables);
        Assert.Contains("ManualReconciliationDecisions", tables);
        Assert.Contains("BackupCleanupAudit", tables);
        Assert.Contains("5", versions);
        Assert.Contains("DeliveryHistory", tables);
    }

    [Fact]
    public async Task DeliveryHistoryStoresOnlySafeOperationalMetadata()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteDeliveryHistoryRepository(database.ConnectionFactory);
        var secretBody = "body that must never be stored in the delivery ledger";
        var plan = new MessageOperationPlan(
            Guid.NewGuid(), Guid.NewGuid(), MessageOperationKind.ManualChannelMessage, Guid.NewGuid(),
            MessageDestination.Channel(123, "Server", 456, "general"),
            new MessageContent(secretBody, null, AllowedMentionPolicy.None), DateTimeOffset.UtcNow,
            0, false, "Confirm delivery", null, null);
        var result = new MessageDeliveryResult(
            plan.OperationId, plan.CorrelationId, MessageOperationState.Delivered,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 789, 1, null);

        await repository.RecordAsync(plan, result, CancellationToken.None);

        var values = await database.ReadStringsAsync("SELECT Kind || ':' || State || ':' || AttemptCount FROM DeliveryHistory;");
        var columns = await database.ReadStringsAsync("SELECT name FROM pragma_table_info('DeliveryHistory');");
        Assert.Equal(["ManualChannelMessage:Delivered:1"], values);
        Assert.DoesNotContain(columns, column => column.Contains("content", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(secretBody, string.Join('|', values), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HistoryRoundTripsSafePlanAndResultFields()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteOperationHistoryRepository(database.ConnectionFactory);
        var plan = UiOperationTestData.Plan();
        var entry = Entry(plan, ChannelOperationState.Completed, "SAFE_TEST_CODE");

        await repository.AddAsync(entry, CancellationToken.None);
        var restored = await repository.GetAsync(plan.OperationId, CancellationToken.None);

        Assert.Equal(entry, restored);
        Assert.DoesNotContain("token", restored!.PlanJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authorization", restored.PlanJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BackupRoundTripsOnlyRelevantStructure()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteOperationBackupRepository(database.ConnectionFactory);
        var plan = UiOperationTestData.Plan();
        var backup = new ServerStructureBackup(
            "backup-test",
            plan.OperationId,
            plan.CorrelationId,
            plan.BotProfileId,
            plan.ServerId,
            plan.ServerNameSnapshot,
            plan.SourceExplorerSequence,
            DateTimeOffset.UtcNow,
            plan.ExactBeforeState);

        await repository.SaveAsync(backup, CancellationToken.None);
        var restored = await repository.GetAsync("backup-test", CancellationToken.None);

        Assert.NotNull(restored);
        Assert.Equal(backup.BackupIdentifier, restored.BackupIdentifier);
        Assert.Equal(backup.OperationId, restored.OperationId);
        Assert.Equal(backup.CorrelationId, restored.CorrelationId);
        Assert.Equal(backup.BotProfileId, restored.BotProfileId);
        Assert.Equal(backup.ServerId, restored.ServerId);
        Assert.Equal(backup.ServerName, restored.ServerName);
        Assert.Equal(backup.ExplorerSequence, restored.ExplorerSequence);
        Assert.True(backup.Channels.SequenceEqual(restored.Channels));
        Assert.DoesNotContain(
            "Message",
            JsonSerializer.Serialize(restored),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VersionThreeHistoryAndBackupsArePreservedAndBackfilled()
    {
        await using var database = await TestDatabase.CreateAsync();
        var history = new SqliteOperationHistoryRepository(database.ConnectionFactory);
        var backups = new SqliteOperationBackupRepository(database.ConnectionFactory);
        var plan = UiOperationTestData.Plan();
        var entry = Entry(plan, ChannelOperationState.Completed, null);
        var backup = Backup(plan, "schema-three-backup", DateTimeOffset.UtcNow);
        await history.AddAsync(entry, CancellationToken.None);
        await backups.SaveAsync(backup, CancellationToken.None);
        await database.ExecuteAsync(
            """
            DROP TABLE BackupCatalogMetadata;
            DROP TABLE BackupRetentionSettings;
            DROP TABLE OperationStateTransitions;
            DROP TABLE ManualReconciliationDecisions;
            DROP TABLE BackupCleanupAudit;
            DELETE FROM SchemaVersions WHERE Version = 4;
            """);

        await database.InitializeAsync();

        var restoredHistory = await history.GetAsync(plan.OperationId, CancellationToken.None);
        var restoredBackup = await backups.GetAsync(backup.BackupIdentifier, CancellationToken.None);
        var catalog = new SqliteOperationalRecoveryRepository(database.ConnectionFactory);
        var metadata = await catalog.GetCatalogItemAsync(
            backup.BackupIdentifier,
            CancellationToken.None);
        Assert.NotNull(restoredHistory);
        Assert.NotNull(restoredBackup);
        Assert.NotNull(metadata);
        Assert.Equal(backup.Channels.Length, metadata.CategoryCount + metadata.ChannelCount);
        Assert.Equal(
            backup.Channels.Sum(channel => channel.PermissionOverwrites.Length),
            metadata.PermissionOverwriteCount);
        Assert.False(metadata.IsPinned);
    }

    [Fact]
    public async Task RecentHistoryIsBoundedAndNewestFirst()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteOperationHistoryRepository(database.ConnectionFactory);
        var basePlan = UiOperationTestData.Plan();
        for (var index = 0; index < 4; index++)
        {
            var plan = basePlan with
            {
                OperationId = Guid.NewGuid(),
                CorrelationId = Guid.NewGuid(),
                CreatedAt = basePlan.CreatedAt.AddMinutes(index)
            };
            await repository.AddAsync(
                Entry(plan, ChannelOperationState.Completed, null),
                CancellationToken.None);
        }

        var recent = await repository.GetRecentAsync(2, CancellationToken.None);

        Assert.Equal(2, recent.Count);
        Assert.True(recent[0].CreatedAt > recent[1].CreatedAt);
    }

    [Fact]
    public async Task DuplicateOperationIdCannotBeInsertedTwice()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteOperationHistoryRepository(database.ConnectionFactory);
        var plan = UiOperationTestData.Plan();
        var entry = Entry(plan, ChannelOperationState.Pending, null);
        await repository.AddAsync(entry, CancellationToken.None);

        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(
            () => repository.AddAsync(entry, CancellationToken.None));
    }

    [Fact]
    public async Task OperationSchemaContainsNoCredentialOrMessageColumns()
    {
        await using var database = await TestDatabase.CreateAsync();

        var historyColumns = await database.ReadStringsAsync(
            "SELECT name FROM pragma_table_info('OperationHistory');");
        var backupColumns = await database.ReadStringsAsync(
            "SELECT name FROM pragma_table_info('OperationBackups');");
        var columns = historyColumns.Concat(backupColumns).ToArray();

        Assert.DoesNotContain(columns, column =>
            column.Contains("Token", StringComparison.OrdinalIgnoreCase)
            || column.Contains("Authorization", StringComparison.OrdinalIgnoreCase)
            || column.Contains("MessageContent", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BackupCatalogSupportsSearchPagingPinningAndLocalOnlyDeletion()
    {
        await using var database = await TestDatabase.CreateAsync();
        var backups = new SqliteOperationBackupRepository(database.ConnectionFactory);
        var catalog = new SqliteOperationalRecoveryRepository(database.ConnectionFactory);
        var plan = UiOperationTestData.Plan();
        var first = Backup(plan, "backup-first", DateTimeOffset.UtcNow.AddDays(-2));
        var second = Backup(
            plan with
            {
                OperationId = Guid.NewGuid(),
                CorrelationId = Guid.NewGuid()
            },
            "backup-second",
            DateTimeOffset.UtcNow.AddDays(-1));
        await backups.SaveAsync(first, CancellationToken.None);
        await backups.SaveAsync(second, CancellationToken.None);

        var page = await catalog.QueryAsync(
            BackupQuery(search: "backup-", pageNumber: 1, pageSize: 1),
            CancellationToken.None);
        var serverSearch = await catalog.QueryAsync(
            BackupQuery(search: plan.ServerId.ToString(CultureInfo.InvariantCulture)),
            CancellationToken.None);

        Assert.Equal(2, page.TotalCount);
        Assert.Equal(2, page.TotalPages);
        Assert.Single(page.Items);
        Assert.Equal("backup-second", page.Items[0].BackupIdentifier);
        Assert.Equal(2, serverSearch.TotalCount);

        await catalog.SetPinnedAsync("backup-first", true, CancellationToken.None);
        await catalog.DeleteLocalAsync(
            ["backup-first", "backup-second"],
            "Persistence test",
            CancellationToken.None);
        var remaining = await catalog.QueryAsync(
            BackupQuery(),
            CancellationToken.None);
        var cleanupAudit = await database.ReadStringsAsync(
            "SELECT SafeReason FROM BackupCleanupAudit;");

        var pinned = Assert.Single(remaining.Items);
        Assert.Equal("backup-first", pinned.BackupIdentifier);
        Assert.True(pinned.IsPinned);
        Assert.Contains("Persistence test", cleanupAudit);
    }

    [Fact]
    public async Task RetentionDryRunPreservesPinnedAndFailedOperationBackups()
    {
        await using var database = await TestDatabase.CreateAsync();
        var history = new SqliteOperationHistoryRepository(database.ConnectionFactory);
        var backups = new SqliteOperationBackupRepository(database.ConnectionFactory);
        var catalog = new SqliteOperationalRecoveryRepository(database.ConnectionFactory);
        var plan = UiOperationTestData.Plan();
        var failedPlan = plan with
        {
            OperationId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid()
        };
        await history.AddAsync(
            Entry(failedPlan, ChannelOperationState.Failed, "SAFE_FAILURE"),
            CancellationToken.None);
        await backups.SaveAsync(
            Backup(plan, "backup-pinned", DateTimeOffset.UtcNow.AddDays(-60)),
            CancellationToken.None);
        await backups.SaveAsync(
            Backup(failedPlan, "backup-failed", DateTimeOffset.UtcNow.AddDays(-50)),
            CancellationToken.None);
        var ordinaryPlan = plan with
        {
            OperationId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid()
        };
        await backups.SaveAsync(
            Backup(ordinaryPlan, "backup-expired", DateTimeOffset.UtcNow.AddDays(-40)),
            CancellationToken.None);
        await catalog.SetPinnedAsync("backup-pinned", true, CancellationToken.None);

        var preview = await catalog.PreviewCleanupAsync(
            new BackupRetentionPolicy(false, 30, null, true, null),
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        var candidate = Assert.Single(preview.Candidates);
        Assert.Equal("backup-expired", candidate.BackupIdentifier);
        Assert.DoesNotContain(preview.Candidates, item => item.BackupIdentifier == "backup-pinned");
        Assert.DoesNotContain(preview.Candidates, item => item.BackupIdentifier == "backup-failed");
        Assert.True(preview.EstimatedBytesReclaimed > 0);
    }

    [Fact]
    public async Task CatalogClassifiesNewerSchemaAndCorruptBackups()
    {
        await using var database = await TestDatabase.CreateAsync();
        var backups = new SqliteOperationBackupRepository(database.ConnectionFactory);
        var catalog = new SqliteOperationalRecoveryRepository(database.ConnectionFactory);
        var plan = UiOperationTestData.Plan();
        await backups.SaveAsync(
            Backup(plan, "backup-newer", DateTimeOffset.UtcNow) with { SchemaVersion = 99 },
            CancellationToken.None);
        await backups.SaveAsync(
            Backup(
                plan with
                {
                    OperationId = Guid.NewGuid(),
                    CorrelationId = Guid.NewGuid()
                },
                "backup-corrupt",
                DateTimeOffset.UtcNow),
            CancellationToken.None);
        await database.ExecuteAsync(
            """
            UPDATE OperationBackups SET SnapshotJson = '{not-json'
            WHERE BackupIdentifier = 'backup-corrupt';
            UPDATE BackupCatalogMetadata SET IsCorrupt = 1, SafeIssue = 'Corrupt test record.'
            WHERE BackupIdentifier = 'backup-corrupt';
            """);

        var newer = await catalog.QueryAsync(
            BackupQuery(compatibility: BackupCompatibility.NewerSchema),
            CancellationToken.None);
        var corrupt = await catalog.QueryAsync(
            BackupQuery(compatibility: BackupCompatibility.Corrupt),
            CancellationToken.None);

        Assert.Equal("backup-newer", Assert.Single(newer.Items).BackupIdentifier);
        Assert.Equal(BackupCompatibility.NewerSchema, newer.Items[0].Compatibility);
        Assert.Equal("backup-corrupt", Assert.Single(corrupt.Items).BackupIdentifier);
        Assert.Equal(BackupCompatibility.Corrupt, corrupt.Items[0].Compatibility);
    }

    [Fact]
    public async Task SafeExportsAreVersionedPagedAndExcludeSensitiveFields()
    {
        await using var database = await TestDatabase.CreateAsync();
        var history = new SqliteOperationHistoryRepository(database.ConnectionFactory);
        var query = new SqliteOperationalRecoveryRepository(database.ConnectionFactory);
        var plan = UiOperationTestData.Plan() with { AuditReason = "Routine test" };
        await history.AddAsync(Entry(plan, ChannelOperationState.Completed, null), CancellationToken.None);
        var exporter = new DiscordControlCenter.Application.Operations.OperationExportService(
            query,
            new ExportBackupCatalog(query));
        await using var json = new MemoryStream();
        await using var csv = new MemoryStream();

        var jsonCount = await exporter.ExportHistoryJsonAsync(
            json,
            HistoryQuery(),
            CancellationToken.None);
        var csvCount = await exporter.ExportHistoryCsvAsync(
            csv,
            HistoryQuery(),
            CancellationToken.None);
        var jsonText = Encoding.UTF8.GetString(json.ToArray());
        var csvText = Encoding.UTF8.GetString(csv.ToArray());

        Assert.Equal(1, jsonCount);
        Assert.Equal(1, csvCount);
        Assert.Contains("\"SchemaVersion\": 1", jsonText, StringComparison.Ordinal);
        Assert.Contains(plan.OperationId.ToString(), jsonText, StringComparison.Ordinal);
        Assert.Contains(plan.OperationId.ToString(), csvText, StringComparison.Ordinal);
        Assert.DoesNotContain("AuthorizationHeader", jsonText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ProtectedToken", jsonText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PlanJson", jsonText, StringComparison.Ordinal);
        Assert.DoesNotContain("ResultJson", jsonText, StringComparison.Ordinal);
    }

    private static ServerStructureBackup Backup(
        OperationPlan plan,
        string identifier,
        DateTimeOffset createdAt) =>
        new(
            identifier,
            plan.OperationId,
            plan.CorrelationId,
            plan.BotProfileId,
            plan.ServerId,
            plan.ServerNameSnapshot,
            plan.SourceExplorerSequence,
            createdAt,
            plan.ExactBeforeState)
        {
            BackupReason = "Test structural backup",
            SourceOperationType = plan.OperationType
        };

    private static BackupQuery BackupQuery(
        string? search = null,
        BackupCompatibility? compatibility = null,
        int pageNumber = 1,
        int pageSize = 50) =>
        new(
            search,
            null,
            null,
            null,
            null,
            null,
            compatibility,
            BackupSort.Newest,
            pageNumber,
            pageSize);

    private static OperationHistoryQuery HistoryQuery() =>
        new(
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            OperationHistorySort.Newest,
            1,
            50);

    private static OperationHistoryEntry Entry(
        OperationPlan plan,
        ChannelOperationState state,
        string? safeCode) =>
        new(
            plan.OperationId,
            plan.CorrelationId,
            plan.OperationType,
            plan.BotProfileId,
            plan.ServerId,
            plan.ServerNameSnapshot,
            string.Join(',', plan.ExactTargetIds),
            "general",
            plan.CreatedAt,
            plan.CreatedAt.AddSeconds(1),
            plan.CreatedAt.AddSeconds(2),
            state,
            1,
            0,
            0,
            "No compensation required.",
            null,
            safeCode,
            1000,
            plan.AuditReason,
            JsonSerializer.Serialize(plan),
            null);

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly string _directory;

        private TestDatabase(
            string directory,
            SqliteConnectionFactory connectionFactory)
        {
            _directory = directory;
            ConnectionFactory = connectionFactory;
        }

        public SqliteConnectionFactory ConnectionFactory { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                $"discord-control-center-tests-{Guid.NewGuid():N}");
            var paths = new ApplicationPaths(directory);
            var connectionFactory = new SqliteConnectionFactory(paths);
            var initializer = new SqliteDatabaseInitializer(
                connectionFactory,
                NullLogger<SqliteDatabaseInitializer>.Instance);
            await initializer.InitializeAsync(CancellationToken.None);
            return new TestDatabase(directory, connectionFactory);
        }

        public async Task<IReadOnlyList<string>> ReadStringsAsync(string sql)
        {
            await using var connection = await ConnectionFactory.OpenAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            var values = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
            while (await reader.ReadAsync(CancellationToken.None))
            {
                values.Add(reader.GetString(0));
            }

            return values;
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var connection = await ConnectionFactory.OpenAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        public async Task InitializeAsync()
        {
            var initializer = new SqliteDatabaseInitializer(
                ConnectionFactory,
                NullLogger<SqliteDatabaseInitializer>.Instance);
            await initializer.InitializeAsync(CancellationToken.None);
        }

        public ValueTask DisposeAsync()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class ExportBackupCatalog(IBackupCatalogRepository repository) :
    DiscordControlCenter.Application.Operations.IBackupCatalogService
{
    public Task<PagedResult<BackupCatalogItem>> QueryAsync(
        BackupQuery query,
        CancellationToken cancellationToken) =>
        repository.QueryAsync(query, cancellationToken);

    public Task<BackupDetail?> GetDetailAsync(
        string backupIdentifier,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task SetPinnedAsync(
        string backupIdentifier,
        bool pinned,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task DeleteLocalAsync(
        IReadOnlyCollection<string> backupIdentifiers,
        string safeReason,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<BackupRetentionPolicy> GetRetentionPolicyAsync(
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task SaveRetentionPolicyAsync(
        BackupRetentionPolicy policy,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<BackupCleanupPreview> PreviewCleanupAsync(
        BackupRetentionPolicy policy,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

internal static class UiOperationTestData
{
    internal static OperationPlan Plan(
        OperationConfirmationRequirement? confirmation = null) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            100,
            "Disposable Test Server",
            5,
            DateTimeOffset.UtcNow,
            ChannelOperationType.EditChannel,
            "Edit general",
            [301],
            [State("general")],
            [State("renamed")],
            [PermissionBits.ManageChannels],
            [],
            OperationRiskLevel.Moderate,
            [
                new OperationStep(
                    Guid.NewGuid(),
                    1,
                    OperationStepKind.ModifyChannel,
                    "Rename general",
                    new OperationTarget(301, "general", OperationTargetKind.Channel, 300, "fingerprint"),
                    State("general"),
                    State("renamed"),
                    null,
                    false,
                    new OperationCompensation(
                        OperationCompensationCapability.ExactWhenTargetUnchanged,
                        OperationStepKind.ModifyChannel,
                        301,
                        State("general"),
                        null,
                        "Restore the old name."))
            ],
            1,
            confirmation
                ?? new OperationConfirmationRequirement(
                    OperationConfirmationKind.Explicit,
                    "Confirm.",
                    string.Empty),
            OperationCompensationCapability.ExactWhenTargetUnchanged,
            "test reason");

    internal static OperationPreview Preview(
        OperationPlan plan,
        OperationConfirmationRequirement? confirmation = null) =>
        new(
            plan.OperationId,
            plan.CorrelationId,
            plan.Title,
            "Test bot",
            plan.ServerNameSnapshot,
            plan.RiskLevel,
            1,
            1,
            ["Manage Channels"],
            [new PropertyChange("Name", "general", "renamed")],
            [],
            ["The name changes."],
            confirmation ?? plan.ConfirmationRequirement,
            plan.AuditReason);

    private static ChannelOperationStateSnapshot State(string name) =>
        new(
            301,
            name,
            ChannelKind.Text,
            0,
            300,
            "Operations",
            "Topic",
            false,
            0,
            60,
            null,
            null,
            null,
            ImmutableArray<ChannelPermissionOverwriteSnapshot>.Empty);
}
