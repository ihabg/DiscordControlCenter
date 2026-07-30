using System.Collections.Immutable;
using System.Text.Json;
using DiscordControlCenter.Core.Explorer;
using DiscordControlCenter.Core.Operations;
using DiscordControlCenter.Infrastructure.Configuration;
using DiscordControlCenter.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordControlCenter.App.Tests;

public sealed class OperationPersistenceTests
{
    [Fact]
    public async Task MigrationCreatesVersionThreeOperationTables()
    {
        await using var database = await TestDatabase.CreateAsync();

        var versions = await database.ReadStringsAsync(
            "SELECT CAST(Version AS TEXT) FROM SchemaVersions ORDER BY Version;");
        var tables = await database.ReadStringsAsync(
            "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;");

        Assert.Contains("3", versions);
        Assert.Contains("OperationHistory", tables);
        Assert.Contains("OperationBackups", tables);
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
