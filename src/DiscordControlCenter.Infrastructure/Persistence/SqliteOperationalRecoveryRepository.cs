using System.Collections.Immutable;
using System.Globalization;
using DiscordControlCenter.Core.Operations;
using Microsoft.Data.Sqlite;

namespace DiscordControlCenter.Infrastructure.Persistence;

public sealed class SqliteOperationalRecoveryRepository(
    SqliteConnectionFactory connectionFactory) :
    IOperationHistoryQueryRepository,
    IBackupCatalogRepository,
    IManualReconciliationRepository
{
    private const int CurrentBackupSchemaVersion = 2;

    public async Task<PagedResult<OperationHistoryEntry>> QueryAsync(
        OperationHistoryQuery query,
        CancellationToken cancellationToken)
    {
        ValidatePage(query.PageNumber, query.PageSize);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var where = new List<string>();
        var parameters = new List<(string Name, object Value)>();
        AddSearch(
            where,
            parameters,
            query.SearchText,
            "(h.OperationId LIKE $search OR h.CorrelationId LIKE $search OR "
            + "h.ServerName LIKE $search OR h.ServerId LIKE $search OR "
            + "h.TargetIds LIKE $search OR h.SafeDisplayNames LIKE $search OR h.PlanJson LIKE $search)");
        Add(where, parameters, query.BotProfileId, "h.BotProfileId = $botId", "$botId", value => value.ToString("D"));
        Add(where, parameters, query.ServerId, "h.ServerId = $serverId", "$serverId", Invariant);
        Add(where, parameters, query.OperationType, "h.PlanType = $operationType", "$operationType", value => value.ToString());
        Add(where, parameters, query.State, "h.State = $state", "$state", value => value.ToString());
        if (query.RiskLevel is { } risk)
        {
            where.Add("json_valid(h.PlanJson) = 1 AND CAST(json_extract(h.PlanJson, '$.RiskLevel') AS INTEGER) = $risk");
            parameters.Add(("$risk", (int)risk));
        }

        AddDateRange(where, parameters, query.CreatedFrom, query.CreatedTo, "h.CreatedAt");
        if (query.HasBackup is { } hasBackup)
        {
            where.Add(hasBackup ? "h.BackupIdentifier IS NOT NULL" : "h.BackupIdentifier IS NULL");
        }

        if (query.RequiresManualReconciliation is { } manual)
        {
            where.Add(
                manual
                    ? "h.State = 'ReconciliationRequired'"
                    : "h.State <> 'ReconciliationRequired'");
        }

        var whereSql = where.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", where);
        var orderSql = query.Sort switch
        {
            OperationHistorySort.Oldest => "h.CreatedAt ASC",
            OperationHistorySort.Duration => "h.DurationMilliseconds DESC, h.CreatedAt DESC",
            OperationHistorySort.AffectedResources =>
                "CASE WHEN json_valid(h.PlanJson) THEN json_array_length(h.PlanJson, '$.Steps') ELSE 0 END DESC, h.CreatedAt DESC",
            _ => "h.CreatedAt DESC"
        };

        var total = await CountAsync(
                connection,
                $"SELECT COUNT(*) FROM OperationHistory h{whereSql};",
                parameters,
                cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            {SqliteOperationHistoryRepository.SelectColumns}
            FROM OperationHistory h
            {whereSql}
            ORDER BY {orderSql}
            LIMIT $pageSize OFFSET $offset;
            """;
        AddParameters(command, parameters);
        command.Parameters.AddWithValue("$pageSize", query.PageSize);
        command.Parameters.AddWithValue("$offset", (query.PageNumber - 1) * query.PageSize);
        var entries = ImmutableArray.CreateBuilder<OperationHistoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(Enrich(SqliteOperationHistoryRepository.Read(reader)));
        }

        return new PagedResult<OperationHistoryEntry>(
            entries.ToImmutable(),
            query.PageNumber,
            query.PageSize,
            total);
    }

    public async Task<OperationHistoryDetail?> GetDetailAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var history = await new SqliteOperationHistoryRepository(connectionFactory)
            .GetAsync(operationId, cancellationToken)
            .ConfigureAwait(false);
        if (history is null)
        {
            return null;
        }

        OperationPlan? plan = null;
        ChannelOperationResult? result = null;
        string? issue = null;
        try
        {
            plan = OperationJson.Deserialize<OperationPlan>(history.PlanJson);
            result = history.ResultJson is null
                ? null
                : OperationJson.Deserialize<ChannelOperationResult>(history.ResultJson);
        }
        catch (Exception exception) when (
            exception is System.Text.Json.JsonException or NotSupportedException)
        {
            issue = "Persisted operation details are corrupt or use an unsupported schema.";
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var transitions = await ReadTransitionsAsync(connection, operationId, cancellationToken).ConfigureAwait(false);
        var decisions = await ReadDecisionsAsync(connection, operationId, cancellationToken).ConfigureAwait(false);
        return new OperationHistoryDetail(
            Enrich(history),
            plan,
            result,
            transitions,
            decisions,
            issue);
    }

    public async Task AddTransitionAsync(
        OperationStateTransition transition,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO OperationStateTransitions
                (OperationId, State, Timestamp, ReasonCode, SafeSummary)
            VALUES ($operationId, $state, $timestamp, $reasonCode, $summary);
            """;
        command.Parameters.AddWithValue("$operationId", transition.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$state", transition.State.ToString());
        command.Parameters.AddWithValue("$timestamp", transition.Timestamp.ToString("O"));
        command.Parameters.AddWithValue("$reasonCode", transition.ReasonCode);
        command.Parameters.AddWithValue("$summary", transition.SafeSummary);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddManualDecisionAsync(
        ManualReconciliationDecision decision,
        CancellationToken cancellationToken) =>
        await AddDecisionAsync(decision, cancellationToken).ConfigureAwait(false);

    public async Task AddAsync(
        ManualReconciliationDecision decision,
        CancellationToken cancellationToken) =>
        await AddDecisionAsync(decision, cancellationToken).ConfigureAwait(false);

    public async Task<PagedResult<BackupCatalogItem>> QueryAsync(
        BackupQuery query,
        CancellationToken cancellationToken)
    {
        ValidatePage(query.PageNumber, query.PageSize);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var where = new List<string>();
        var parameters = new List<(string Name, object Value)>();
        AddSearch(
            where,
            parameters,
            query.SearchText,
            "(b.BackupIdentifier LIKE $search OR b.CorrelationId LIKE $search OR "
            + "b.ServerName LIKE $search OR b.ServerId LIKE $search)");
        Add(where, parameters, query.BotProfileId, "b.BotProfileId = $botId", "$botId", value => value.ToString("D"));
        Add(where, parameters, query.ServerId, "b.ServerId = $serverId", "$serverId", Invariant);
        Add(
            where,
            parameters,
            query.SourceOperationType,
            "m.SourceOperationType = $sourceType",
            "$sourceType",
            value => value.ToString());
        AddDateRange(where, parameters, query.CreatedFrom, query.CreatedTo, "b.CreatedAt");
        if (query.Compatibility is { } compatibility)
        {
            where.Add(
                compatibility switch
                {
                    BackupCompatibility.Corrupt => "m.IsCorrupt = 1",
                    BackupCompatibility.NewerSchema => "m.SchemaVersion > $currentSchema",
                    BackupCompatibility.Unsupported => "m.SchemaVersion > $currentSchema OR m.IsCorrupt = 1",
                    _ => "m.SchemaVersion <= $currentSchema AND m.IsCorrupt = 0"
                });
            parameters.Add(("$currentSchema", CurrentBackupSchemaVersion));
        }

        var whereSql = where.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", where);
        var orderSql = query.Sort switch
        {
            BackupSort.Oldest => "b.CreatedAt ASC",
            BackupSort.Server => "b.ServerName COLLATE NOCASE, b.CreatedAt DESC",
            BackupSort.ResourceCount => "(m.CategoryCount + m.ChannelCount) DESC, b.CreatedAt DESC",
            _ => "b.CreatedAt DESC"
        };
        var fromSql =
            " FROM OperationBackups b JOIN BackupCatalogMetadata m "
            + "ON m.BackupIdentifier = b.BackupIdentifier";
        var total = await CountAsync(
                connection,
                $"SELECT COUNT(*){fromSql}{whereSql};",
                parameters,
                cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT b.BackupIdentifier, b.OperationId, b.CorrelationId, b.BotProfileId,
                   b.ServerId, b.ServerName, b.CreatedAt, m.BackupReason,
                   m.SourceOperationType, m.CategoryCount, m.ChannelCount,
                   m.PermissionOverwriteCount, b.ExplorerSequence, m.SchemaVersion,
                   m.IsPinned, m.SizeBytes, m.IsCorrupt, m.SafeIssue
            {fromSql}
            {whereSql}
            ORDER BY {orderSql}
            LIMIT $pageSize OFFSET $offset;
            """;
        AddParameters(command, parameters);
        command.Parameters.AddWithValue("$pageSize", query.PageSize);
        command.Parameters.AddWithValue("$offset", (query.PageNumber - 1) * query.PageSize);
        var items = ImmutableArray.CreateBuilder<BackupCatalogItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(ReadBackup(reader));
        }

        return new PagedResult<BackupCatalogItem>(
            items.ToImmutable(),
            query.PageNumber,
            query.PageSize,
            total);
    }

    public async Task<BackupCatalogItem?> GetCatalogItemAsync(
        string backupIdentifier,
        CancellationToken cancellationToken)
    {
        var page = await QueryAsync(
                new BackupQuery(
                    backupIdentifier,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    BackupSort.Newest,
                    1,
                    10),
                cancellationToken)
            .ConfigureAwait(false);
        return page.Items.FirstOrDefault(item =>
            string.Equals(item.BackupIdentifier, backupIdentifier, StringComparison.Ordinal));
    }

    public async Task SetPinnedAsync(
        string backupIdentifier,
        bool isPinned,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE BackupCatalogMetadata SET IsPinned = $pinned WHERE BackupIdentifier = $identifier;";
        command.Parameters.AddWithValue("$pinned", isPinned ? 1 : 0);
        command.Parameters.AddWithValue("$identifier", backupIdentifier);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new KeyNotFoundException("The local backup no longer exists.");
        }
    }

    public async Task DeleteLocalAsync(
        IReadOnlyCollection<string> backupIdentifiers,
        string safeReason,
        CancellationToken cancellationToken)
    {
        if (backupIdentifiers.Count == 0)
        {
            return;
        }

        var identifiers = backupIdentifiers.Distinct(StringComparer.Ordinal).ToArray();
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var reclaimed = 0L;
        var deletedIdentifiers = new List<string>();
        foreach (var identifier in identifiers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var sizeCommand = connection.CreateCommand();
            sizeCommand.Transaction = transaction;
            sizeCommand.CommandText =
                "SELECT SizeBytes FROM BackupCatalogMetadata WHERE BackupIdentifier = $identifier AND IsPinned = 0;";
            sizeCommand.Parameters.AddWithValue("$identifier", identifier);
            var size = await sizeCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (size is null)
            {
                continue;
            }

            reclaimed += Convert.ToInt64(size, CultureInfo.InvariantCulture);
            deletedIdentifiers.Add(identifier);
            await using var deleteBackup = connection.CreateCommand();
            deleteBackup.Transaction = transaction;
            deleteBackup.CommandText =
                """
                DELETE FROM OperationBackups WHERE BackupIdentifier = $identifier;
                DELETE FROM BackupCatalogMetadata WHERE BackupIdentifier = $identifier;
                """;
            deleteBackup.Parameters.AddWithValue("$identifier", identifier);
            await deleteBackup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var auditCommand = connection.CreateCommand();
        auditCommand.Transaction = transaction;
        auditCommand.CommandText =
            """
            INSERT INTO BackupCleanupAudit
                (Id, Timestamp, BackupIdentifiers, DeletedCount, ReclaimedBytes, SafeReason)
            VALUES ($id, $timestamp, $identifiers, $count, $bytes, $reason);
            """;
        auditCommand.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        auditCommand.Parameters.AddWithValue("$timestamp", DateTimeOffset.UtcNow.ToString("O"));
        auditCommand.Parameters.AddWithValue("$identifiers", string.Join(",", deletedIdentifiers));
        auditCommand.Parameters.AddWithValue("$count", deletedIdentifiers.Count);
        auditCommand.Parameters.AddWithValue("$bytes", reclaimed);
        auditCommand.Parameters.AddWithValue("$reason", Sanitize(safeReason, 200));
        await auditCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<BackupRetentionPolicy> GetRetentionPolicyAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT KeepIndefinitely, MaximumAgeDays, NewestPerServer,
                   PreserveFailedOperationBackups, MaximumStorageBytes
            FROM BackupRetentionSettings WHERE Id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new BackupRetentionPolicy(true, null, null, true, null);
        }

        return new BackupRetentionPolicy(
            reader.GetInt32(0) != 0,
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetInt32(2),
            reader.GetInt32(3) != 0,
            reader.IsDBNull(4) ? null : reader.GetInt64(4));
    }

    public async Task SaveRetentionPolicyAsync(
        BackupRetentionPolicy policy,
        CancellationToken cancellationToken)
    {
        ValidateRetention(policy);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE BackupRetentionSettings
            SET KeepIndefinitely = $keep, MaximumAgeDays = $days,
                NewestPerServer = $newest, PreserveFailedOperationBackups = $preserve,
                MaximumStorageBytes = $bytes, UpdatedAt = $updated
            WHERE Id = 1;
            """;
        command.Parameters.AddWithValue("$keep", policy.KeepIndefinitely ? 1 : 0);
        command.Parameters.AddWithValue("$days", policy.MaximumAgeDays ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$newest", policy.NewestPerServer ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$preserve", policy.PreserveFailedOperationBackups ? 1 : 0);
        command.Parameters.AddWithValue("$bytes", policy.MaximumStorageBytes ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<BackupCleanupPreview> PreviewCleanupAsync(
        BackupRetentionPolicy policy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ValidateRetention(policy);
        if (policy.KeepIndefinitely)
        {
            return new BackupCleanupPreview([], 0, now);
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT b.BackupIdentifier, b.ServerId, b.ServerName, b.CreatedAt,
                   m.SizeBytes, m.IsPinned, COALESCE(h.State, '')
            FROM OperationBackups b
            JOIN BackupCatalogMetadata m ON m.BackupIdentifier = b.BackupIdentifier
            LEFT JOIN OperationHistory h ON h.OperationId = b.OperationId
            ORDER BY b.ServerId, b.CreatedAt DESC;
            """;
        var rows = new List<RetentionRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(
                new RetentionRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    ParseDate(reader.GetString(3)),
                    reader.GetInt64(4),
                    reader.GetInt32(5) != 0,
                    reader.GetString(6)));
        }

        var candidates = new Dictionary<string, BackupCleanupCandidate>(StringComparer.Ordinal);
        bool Protected(RetentionRow row) =>
            row.IsPinned
            || policy.PreserveFailedOperationBackups
            && row.OperationState is nameof(ChannelOperationState.Failed)
                or nameof(ChannelOperationState.PartiallyCompleted)
                or nameof(ChannelOperationState.ReconciliationRequired);

        if (policy.MaximumAgeDays is { } days)
        {
            var cutoff = now.AddDays(-days);
            foreach (var row in rows.Where(row => !Protected(row) && row.CreatedAt < cutoff))
            {
                candidates[row.Identifier] = Candidate(row, $"Older than {days} days.");
            }
        }

        if (policy.NewestPerServer is { } newest)
        {
            foreach (var group in rows.GroupBy(row => row.ServerId, StringComparer.Ordinal))
            {
                foreach (var row in group.Skip(newest).Where(row => !Protected(row)))
                {
                    candidates.TryAdd(row.Identifier, Candidate(row, $"Outside newest {newest} for this server."));
                }
            }
        }

        if (policy.MaximumStorageBytes is { } maximum)
        {
            var retainedBytes = rows.Where(row => !candidates.ContainsKey(row.Identifier))
                .Sum(row => row.SizeBytes);
            foreach (var row in rows.OrderBy(row => row.CreatedAt))
            {
                if (retainedBytes <= maximum)
                {
                    break;
                }

                if (Protected(row) || candidates.ContainsKey(row.Identifier))
                {
                    continue;
                }

                candidates[row.Identifier] = Candidate(row, "Required by the configured storage limit.");
                retainedBytes -= row.SizeBytes;
            }
        }

        var ordered = candidates.Values.OrderBy(item => item.CreatedAt).ToImmutableArray();
        return new BackupCleanupPreview(ordered, ordered.Sum(item => item.SizeBytes), now);
    }

    private async Task AddDecisionAsync(
        ManualReconciliationDecision decision,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO ManualReconciliationDecisions
                (OperationId, CorrelationId, StepId, Resolution, Timestamp,
                 SafeExplanation, RelevantResourceIds)
            VALUES ($operationId, $correlationId, $stepId, $resolution, $timestamp,
                    $explanation, $resourceIds);
            """;
        command.Parameters.AddWithValue("$operationId", decision.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$correlationId", decision.CorrelationId.ToString("D"));
        command.Parameters.AddWithValue("$stepId", decision.StepId.ToString("D"));
        command.Parameters.AddWithValue("$resolution", decision.Resolution.ToString());
        command.Parameters.AddWithValue("$timestamp", decision.Timestamp.ToString("O"));
        command.Parameters.AddWithValue("$explanation", Sanitize(decision.SafeExplanation, 500));
        command.Parameters.AddWithValue("$resourceIds", string.Join(",", decision.RelevantResourceIds));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static OperationHistoryEntry Enrich(OperationHistoryEntry entry)
    {
        try
        {
            var plan = OperationJson.Deserialize<OperationPlan>(entry.PlanJson);
            var result = entry.ResultJson is null
                ? null
                : OperationJson.Deserialize<ChannelOperationResult>(entry.ResultJson);
            return entry with
            {
                Title = plan?.Title ?? entry.OperationType.ToString(),
                RiskLevel = plan?.RiskLevel ?? OperationRiskLevel.Low,
                AffectedResourceCount = plan?.Steps.Length ?? 0,
                ReconciliationStatus =
                    result?.Reconciliation.Status ?? OperationReconciliationStatus.NotRequired
            };
        }
        catch (Exception exception) when (
            exception is System.Text.Json.JsonException or NotSupportedException)
        {
            return entry with { Title = "Corrupt operation record" };
        }
    }

    private static BackupCatalogItem ReadBackup(SqliteDataReader reader)
    {
        var schema = reader.GetInt32(13);
        var corrupt = reader.GetInt32(16) != 0;
        var compatibility = corrupt
            ? BackupCompatibility.Corrupt
            : schema > CurrentBackupSchemaVersion
                ? BackupCompatibility.NewerSchema
                : BackupCompatibility.FullySupported;
        return new BackupCatalogItem(
            reader.GetString(0),
            Guid.Parse(reader.GetString(1)),
            Guid.Parse(reader.GetString(2)),
            Guid.Parse(reader.GetString(3)),
            ulong.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
            reader.GetString(5),
            ParseDate(reader.GetString(6)),
            reader.GetString(7),
            Enum.TryParse<ChannelOperationType>(reader.GetString(8), out var type)
                ? type
                : ChannelOperationType.DeleteChannels,
            reader.GetInt32(9),
            reader.GetInt32(10),
            reader.GetInt32(11),
            reader.GetInt64(12),
            schema,
            reader.GetInt32(14) != 0,
            reader.GetInt64(15),
            compatibility,
            false,
            false,
            reader.IsDBNull(17) ? null : reader.GetString(17));
    }

    private static async Task<ImmutableArray<OperationStateTransition>> ReadTransitionsAsync(
        SqliteConnection connection,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, State, Timestamp, ReasonCode, SafeSummary
            FROM OperationStateTransitions
            WHERE OperationId = $operationId ORDER BY Timestamp;
            """;
        command.Parameters.AddWithValue("$operationId", operationId.ToString("D"));
        var items = ImmutableArray.CreateBuilder<OperationStateTransition>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(
                new OperationStateTransition(
                    reader.GetInt64(0),
                    operationId,
                    Enum.Parse<ChannelOperationState>(reader.GetString(1)),
                    ParseDate(reader.GetString(2)),
                    reader.GetString(3),
                    reader.GetString(4)));
        }

        return items.ToImmutable();
    }

    private static async Task<ImmutableArray<ManualReconciliationDecision>> ReadDecisionsAsync(
        SqliteConnection connection,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, CorrelationId, StepId, Resolution, Timestamp,
                   SafeExplanation, RelevantResourceIds
            FROM ManualReconciliationDecisions
            WHERE OperationId = $operationId ORDER BY Timestamp;
            """;
        command.Parameters.AddWithValue("$operationId", operationId.ToString("D"));
        var items = ImmutableArray.CreateBuilder<ManualReconciliationDecision>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var ids = reader.GetString(6)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(id => ulong.Parse(id, CultureInfo.InvariantCulture))
                .ToImmutableArray();
            items.Add(
                new ManualReconciliationDecision(
                    reader.GetInt64(0),
                    operationId,
                    Guid.Parse(reader.GetString(1)),
                    Guid.Parse(reader.GetString(2)),
                    Enum.Parse<ManualReconciliationResolution>(reader.GetString(3)),
                    ParseDate(reader.GetString(4)),
                    reader.GetString(5),
                    ids));
        }

        return items.ToImmutable();
    }

    private static async Task<int> CountAsync(
        SqliteConnection connection,
        string sql,
        IReadOnlyList<(string Name, object Value)> parameters,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameters(command, parameters);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private static void AddParameters(
        SqliteCommand command,
        IEnumerable<(string Name, object Value)> parameters)
    {
        foreach (var parameter in parameters.DistinctBy(item => item.Name))
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }
    }

    private static void AddSearch(
        List<string> where,
        List<(string Name, object Value)> parameters,
        string? search,
        string sql)
    {
        if (!string.IsNullOrWhiteSpace(search))
        {
            where.Add(sql);
            parameters.Add(("$search", $"%{search.Trim()}%"));
        }
    }

    private static void Add<T>(
        List<string> where,
        List<(string Name, object Value)> parameters,
        T? value,
        string sql,
        string name,
        Func<T, object> convert)
        where T : struct
    {
        if (value is { } item)
        {
            where.Add(sql);
            parameters.Add((name, convert(item)));
        }
    }

    private static void AddDateRange(
        List<string> where,
        List<(string Name, object Value)> parameters,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string column)
    {
        if (from is { } start)
        {
            where.Add($"{column} >= $createdFrom");
            parameters.Add(("$createdFrom", start.ToString("O")));
        }

        if (to is { } end)
        {
            where.Add($"{column} <= $createdTo");
            parameters.Add(("$createdTo", end.ToString("O")));
        }
    }

    private static string Invariant(ulong value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    private static void ValidatePage(int page, int size)
    {
        if (page < 1 || size is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(page), "Page must be positive and size must be 1-200.");
        }
    }

    private static void ValidateRetention(BackupRetentionPolicy policy)
    {
        if (policy.MaximumAgeDays is < 1
            || policy.NewestPerServer is < 1
            || policy.MaximumStorageBytes is < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy),
                "Retention values must be positive when specified.");
        }
    }

    private static string Sanitize(string value, int maximumLength)
    {
        var sanitized = new string(value.Where(character => !char.IsControl(character)).ToArray()).Trim();
        return sanitized.Length <= maximumLength ? sanitized : sanitized[..maximumLength];
    }

    private static BackupCleanupCandidate Candidate(RetentionRow row, string reason) =>
        new(row.Identifier, row.ServerName, row.CreatedAt, row.SizeBytes, reason);

    private sealed record RetentionRow(
        string Identifier,
        string ServerId,
        string ServerName,
        DateTimeOffset CreatedAt,
        long SizeBytes,
        bool IsPinned,
        string OperationState);
}
