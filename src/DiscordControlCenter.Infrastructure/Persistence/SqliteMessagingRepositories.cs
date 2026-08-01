using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using DiscordControlCenter.Core.Messaging;

namespace DiscordControlCenter.Infrastructure.Persistence;

public sealed class SqliteMessageTemplateRepository(SqliteConnectionFactory connectionFactory) : IMessageTemplateRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<MessageTemplate>> SearchAsync(string? search, CancellationToken cancellationToken)
    {
        var result = new List<MessageTemplate>();
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, Name, Description, ContentJson, VariablesJson, TagsJson, Version, CreatedAt, UpdatedAt, LastUsedAt, BotProfileId, ServerId
            FROM MessageTemplates
            WHERE $search = '' OR Name LIKE '%' || $search || '%' OR COALESCE(Description, '') LIKE '%' || $search || '%'
            ORDER BY UpdatedAt DESC, Name COLLATE NOCASE
            LIMIT 200;
            """;
        command.Parameters.AddWithValue("$search", search?.Trim() ?? string.Empty);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadTemplate(reader));
        }

        return result;
    }

    public async Task<MessageTemplate?> GetAsync(Guid templateId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Id, Name, Description, ContentJson, VariablesJson, TagsJson, Version, CreatedAt, UpdatedAt, LastUsedAt, BotProfileId, ServerId FROM MessageTemplates WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", templateId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadTemplate(reader) : null;
    }

    public async Task SaveAsync(MessageTemplate messageTemplate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messageTemplate);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO MessageTemplates (Id, Name, Description, ContentJson, VariablesJson, TagsJson, Version, CreatedAt, UpdatedAt, LastUsedAt, BotProfileId, ServerId)
            VALUES ($id, $name, $description, $content, $variables, $tags, $version, $createdAt, $updatedAt, $lastUsedAt, $botProfileId, $serverId)
            ON CONFLICT(Id) DO UPDATE SET
                Name = excluded.Name, Description = excluded.Description, ContentJson = excluded.ContentJson,
                VariablesJson = excluded.VariablesJson, TagsJson = excluded.TagsJson, Version = excluded.Version,
                UpdatedAt = excluded.UpdatedAt, LastUsedAt = excluded.LastUsedAt, BotProfileId = excluded.BotProfileId, ServerId = excluded.ServerId;
            """;
        command.Parameters.AddWithValue("$id", messageTemplate.Id.ToString("D"));
        command.Parameters.AddWithValue("$name", messageTemplate.Name.Trim());
        command.Parameters.AddWithValue("$description", messageTemplate.Description ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$content", JsonSerializer.Serialize(messageTemplate.Content, JsonOptions));
        command.Parameters.AddWithValue("$variables", JsonSerializer.Serialize(messageTemplate.Variables, JsonOptions));
        command.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(messageTemplate.Tags, JsonOptions));
        command.Parameters.AddWithValue("$version", messageTemplate.Version);
        command.Parameters.AddWithValue("$createdAt", messageTemplate.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", messageTemplate.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$lastUsedAt", messageTemplate.LastUsedAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$botProfileId", messageTemplate.BotProfileId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$serverId", messageTemplate.ServerId?.ToString(CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid templateId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM MessageTemplates WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", templateId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static MessageTemplate ReadTemplate(Microsoft.Data.Sqlite.SqliteDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            JsonSerializer.Deserialize<MessageContent>(reader.GetString(3), JsonOptions) ?? throw new InvalidOperationException("Template content is invalid."),
            JsonSerializer.Deserialize<ImmutableArray<TemplateVariableDefinition>>(reader.GetString(4), JsonOptions),
            JsonSerializer.Deserialize<ImmutableArray<string>>(reader.GetString(5), JsonOptions),
            DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind))
        {
            Version = reader.GetInt32(6),
            BotProfileId = reader.IsDBNull(10) ? null : Guid.Parse(reader.GetString(10)),
            ServerId = reader.IsDBNull(11) ? null : ulong.Parse(reader.GetString(11), CultureInfo.InvariantCulture)
        };
}

public sealed class SqliteAutomationRuleRepository(SqliteConnectionFactory connectionFactory) : IAutomationRuleRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<AutomationRule>> ListAsync(Guid? botProfileId, ulong? serverId, CancellationToken cancellationToken)
    {
        var rules = new List<AutomationRule>();
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT v.DefinitionJson FROM AutomationRules r
            INNER JOIN AutomationRuleVersions v ON v.RuleId = r.Id AND v.Version = r.CurrentVersion
            WHERE ($botProfileId IS NULL OR r.BotProfileId = $botProfileId)
              AND ($serverId IS NULL OR r.ServerId = $serverId)
            ORDER BY r.UpdatedAt DESC LIMIT 200;
            """;
        command.Parameters.AddWithValue("$botProfileId", botProfileId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$serverId", serverId?.ToString(CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rules.Add(JsonSerializer.Deserialize<AutomationRule>(reader.GetString(0), JsonOptions) ?? throw new InvalidOperationException("Automation rule is invalid."));
        }

        return rules;
    }

    public async Task<AutomationRule?> GetAsync(Guid ruleId, CancellationToken cancellationToken)
    {
        var rules = await ListAsync(null, null, cancellationToken).ConfigureAwait(false);
        return rules.FirstOrDefault(rule => rule.Id == ruleId);
    }

    public async Task SaveVersionAsync(AutomationRule rule, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rule);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var version = connection.CreateCommand())
        {
            version.Transaction = transaction;
            version.CommandText =
                "INSERT INTO AutomationRuleVersions (RuleId, Version, DefinitionJson, CreatedAt) VALUES ($id, $version, $definition, $createdAt);";
            version.Parameters.AddWithValue("$id", rule.Id.ToString("D"));
            version.Parameters.AddWithValue("$version", rule.Version);
            version.Parameters.AddWithValue("$definition", JsonSerializer.Serialize(rule, JsonOptions));
            version.Parameters.AddWithValue("$createdAt", rule.UpdatedAt.ToString("O"));
            await version.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText =
                """
                INSERT INTO AutomationRules (Id, BotProfileId, ServerId, State, CurrentVersion, Name, UpdatedAt)
                VALUES ($id, $botProfileId, $serverId, $state, $version, $name, $updatedAt)
                ON CONFLICT(Id) DO UPDATE SET State = excluded.State, CurrentVersion = excluded.CurrentVersion,
                    Name = excluded.Name, UpdatedAt = excluded.UpdatedAt;
                """;
            current.Parameters.AddWithValue("$id", rule.Id.ToString("D"));
            current.Parameters.AddWithValue("$botProfileId", rule.BotProfileId.ToString("D"));
            current.Parameters.AddWithValue("$serverId", rule.ServerId.ToString(CultureInfo.InvariantCulture));
            current.Parameters.AddWithValue("$state", rule.State.ToString());
            current.Parameters.AddWithValue("$version", rule.Version);
            current.Parameters.AddWithValue("$name", rule.Name);
            current.Parameters.AddWithValue("$updatedAt", rule.UpdatedAt.ToString("O"));
            await current.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class SqliteAutomationExecutionRepository(SqliteConnectionFactory connectionFactory) : IAutomationExecutionRepository
{
    public async Task<bool> HasCompletedAsync(Guid ruleId, int version, ulong memberId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM AutomationExecutions WHERE RuleId = $ruleId AND RuleVersion = $version AND MemberId = $memberId LIMIT 1;";
        command.Parameters.AddWithValue("$ruleId", ruleId.ToString("D"));
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$memberId", memberId.ToString(CultureInfo.InvariantCulture));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    public async Task SaveAsync(JoinWorkflowExecution execution, AutomationExecutionResult result, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR IGNORE INTO AutomationExecutions
                (Id, RuleId, RuleVersion, BotProfileId, ServerId, MemberId, CorrelationId, State, FailureReason, SafeSummary, StartedAt, FinishedAt)
            VALUES ($id, $ruleId, $ruleVersion, $botProfileId, $serverId, $memberId, $correlationId, $state, $reason, $summary, $startedAt, $finishedAt);
            """;
        command.Parameters.AddWithValue("$id", execution.Id.ToString("D"));
        command.Parameters.AddWithValue("$ruleId", execution.RuleId.ToString("D"));
        command.Parameters.AddWithValue("$ruleVersion", execution.RuleVersion);
        command.Parameters.AddWithValue("$botProfileId", execution.BotProfileId.ToString("D"));
        command.Parameters.AddWithValue("$serverId", execution.ServerId.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$memberId", execution.MemberId.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$correlationId", result.CorrelationId.ToString("D"));
        command.Parameters.AddWithValue("$state", result.RuleState.ToString());
        command.Parameters.AddWithValue("$reason", result.FailureReason.ToString());
        command.Parameters.AddWithValue("$summary", result.SafeSummary);
        command.Parameters.AddWithValue("$startedAt", execution.StartedAt.ToString("O"));
        command.Parameters.AddWithValue("$finishedAt", result.FinishedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class SqliteDeliveryHistoryRepository(SqliteConnectionFactory connectionFactory) : IDeliveryHistoryRepository
{
    public async Task RecordAsync(
        MessageOperationPlan plan,
        MessageDeliveryResult result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(result);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO DeliveryHistory
                (OperationId, CorrelationId, Kind, BotProfileId, ServerId, DestinationId, RecipientUserId, TemplateId, TemplateVersion, RuleId, RuleVersion, State, AttemptCount, SafeFailureCode, StartedAt, FinishedAt)
            VALUES
                ($operationId, $correlationId, $kind, $botProfileId, $serverId, $destinationId, $recipientUserId, $templateId, $templateVersion, $ruleId, $ruleVersion, $state, $attemptCount, $safeFailureCode, $startedAt, $finishedAt)
            ON CONFLICT(OperationId) DO UPDATE SET
                State = excluded.State, AttemptCount = excluded.AttemptCount, SafeFailureCode = excluded.SafeFailureCode,
                FinishedAt = excluded.FinishedAt;
            """;
        command.Parameters.AddWithValue("$operationId", plan.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$correlationId", plan.CorrelationId.ToString("D"));
        command.Parameters.AddWithValue("$kind", plan.Kind.ToString());
        command.Parameters.AddWithValue("$botProfileId", plan.BotProfileId.ToString("D"));
        command.Parameters.AddWithValue("$serverId", plan.Destination.ServerId.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$destinationId", plan.Destination.ChannelId?.ToString(CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$recipientUserId", plan.Destination.RecipientUserId?.ToString(CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$templateId", plan.TemplateId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$templateVersion", plan.TemplateVersion ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$ruleId", plan.AutomationRuleId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$ruleVersion", plan.AutomationRuleVersion ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$state", result.State.ToString());
        command.Parameters.AddWithValue("$attemptCount", result.AttemptCount);
        command.Parameters.AddWithValue("$safeFailureCode", result.Failure?.SafeCode ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$startedAt", result.StartedAt.ToString("O"));
        command.Parameters.AddWithValue("$finishedAt", result.FinishedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class SqliteScheduledMessageRepository(SqliteConnectionFactory connectionFactory) : IScheduledMessageRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<ScheduledMessageDefinition>> ListEnabledAsync(CancellationToken cancellationToken)
    {
        var result = new List<ScheduledMessageDefinition>();
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DefinitionJson FROM ScheduledMessages WHERE IsEnabled = 1 ORDER BY UpdatedAt ASC LIMIT 500;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(JsonSerializer.Deserialize<ScheduledMessageDefinition>(reader.GetString(0), JsonOptions)
                ?? throw new InvalidOperationException("Scheduled message definition is invalid."));
        }

        return result;
    }

    public async Task<ScheduledMessageDefinition?> GetScheduleAsync(Guid scheduleId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DefinitionJson FROM ScheduledMessages WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", scheduleId.ToString("D"));
        var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return json is null ? null : JsonSerializer.Deserialize<ScheduledMessageDefinition>(json, JsonOptions);
    }

    public async Task<ScheduledMessagePage> QuerySchedulesAsync(ScheduledMessageQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.CreatedFrom is { } createdFrom && query.CreatedTo is { } createdTo && createdFrom > createdTo
            || query.NextRunFrom is { } nextFrom && query.NextRunTo is { } nextTo && nextFrom > nextTo)
        {
            throw new ArgumentException("The selected date range is invalid.", nameof(query));
        }

        if (query.PageSize is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(query));
        var page = Math.Max(1, query.PageNumber);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        const string where = """
            WHERE s.BotProfileId = $botId AND s.ServerId = $serverId
              AND ($search = '' OR s.ScheduleName LIKE '%' || $search || '%' OR s.Id LIKE '%' || $search || '%' OR COALESCE(t.Name, '') LIKE '%' || $search || '%' OR COALESCE(json_extract(s.DefinitionJson, '$.destination.channelName'), '') LIKE '%' || $search || '%')
              AND ($recurrence IS NULL OR json_extract(s.DefinitionJson, '$.recurrence') = $recurrence)
              AND ($templateId IS NULL OR json_extract(s.DefinitionJson, '$.templateId') = $templateId)
              AND ($channelId IS NULL OR json_extract(s.DefinitionJson, '$.destination.channelId') = $channelId)
              AND ($missedPolicy IS NULL OR json_extract(s.DefinitionJson, '$.missedOccurrencePolicy') = $missedPolicy)
              AND ($createdFrom IS NULL OR s.CreatedAt >= $createdFrom) AND ($createdTo IS NULL OR s.CreatedAt <= $createdTo)
              AND ($nextFrom IS NULL OR json_extract(s.DefinitionJson, '$.nextRunAt') >= $nextFrom) AND ($nextTo IS NULL OR json_extract(s.DefinitionJson, '$.nextRunAt') <= $nextTo)
              AND ($lifecycle IS NULL OR COALESCE(json_extract(s.DefinitionJson, '$.savedLifecycle'), CASE WHEN json_extract(s.DefinitionJson, '$.endAt') IS NOT NULL AND json_extract(s.DefinitionJson, '$.endAt') < $now THEN 'Expired' WHEN s.IsEnabled = 1 THEN 'Enabled' ELSE 'Disabled' END) = $lifecycle)
              AND ($hasWarning IS NULL OR ($hasWarning = 1 AND (json_extract(s.DefinitionJson, '$.savedLifecycle') = 'Faulted' OR json_extract(s.DefinitionJson, '$.endAt') < $now OR json_extract(s.DefinitionJson, '$.timeZoneId') IS NULL OR (json_extract(s.DefinitionJson, '$.recurrence') = 'Weekly' AND COALESCE(json_array_length(json_extract(s.DefinitionJson, '$.weekdays')), 0) = 0))) OR ($hasWarning = 0 AND NOT (json_extract(s.DefinitionJson, '$.savedLifecycle') = 'Faulted' OR json_extract(s.DefinitionJson, '$.endAt') < $now OR json_extract(s.DefinitionJson, '$.timeZoneId') IS NULL OR (json_extract(s.DefinitionJson, '$.recurrence') = 'Weekly' AND COALESCE(json_array_length(json_extract(s.DefinitionJson, '$.weekdays')), 0) = 0))))
            """;
        await using var count = connection.CreateCommand();
        count.CommandText = $"SELECT COUNT(*) FROM ScheduledMessages s LEFT JOIN MessageTemplates t ON t.Id = json_extract(s.DefinitionJson, '$.templateId') {where};";
        AddScheduleParameters(count, query);
        var total = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT s.Id, s.ScheduleName, s.IsEnabled, s.DefinitionJson, s.CreatedAt, s.UpdatedAt, COALESCE(b.DisplayName, 'Saved bot'), COALESCE(t.Name, '') FROM ScheduledMessages s LEFT JOIN BotProfiles b ON b.Id = s.BotProfileId LEFT JOIN MessageTemplates t ON t.Id = json_extract(s.DefinitionJson, '$.templateId') {where} ORDER BY {GetScheduleSort(query.Sort)} LIMIT $limit OFFSET $offset;";
        AddScheduleParameters(command, query);
        command.Parameters.AddWithValue("$limit", query.PageSize);
        command.Parameters.AddWithValue("$offset", (page - 1) * query.PageSize);
        var items = new List<ScheduledMessageListItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var definition = JsonSerializer.Deserialize<ScheduledMessageDefinition>(reader.GetString(3), JsonOptions) ?? throw new InvalidOperationException("Scheduled message definition is invalid.");
            var lifecycle = definition.SavedLifecycle ?? (definition.EndAt is { } end && end < DateTimeOffset.UtcNow ? ScheduledMessageLifecycle.Expired : definition.IsEnabled ? ScheduledMessageLifecycle.Enabled : ScheduledMessageLifecycle.Disabled);
            var hasWarning = lifecycle is ScheduledMessageLifecycle.Faulted or ScheduledMessageLifecycle.Expired
                || string.IsNullOrWhiteSpace(definition.TimeZoneId)
                || definition.Recurrence == ScheduledMessageRecurrence.Weekly && definition.Weekdays.Length == 0;
            items.Add(new ScheduledMessageListItem(definition.Id, reader.GetString(1), lifecycle, reader.GetString(6), definition.Destination.ServerName, definition.Destination.ChannelName ?? "Deleted or unavailable channel", string.IsNullOrWhiteSpace(reader.GetString(7)) ? "Deleted or unavailable template" : reader.GetString(7), definition.Recurrence, definition.TimeZoneId, definition.NextRunAt, definition.LastRunAt, null, definition.MissedOccurrencePolicy, hasWarning, 1, DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }

        return new ScheduledMessagePage(items, total, page, query.PageSize, DateTimeOffset.UtcNow);
    }

    public async Task<ScheduledMessageFilterOptions> GetScheduleFilterOptionsAsync(Guid botProfileId, ulong serverId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        const string scope = " FROM ScheduledMessages s LEFT JOIN MessageTemplates t ON t.Id = json_extract(s.DefinitionJson, '$.templateId') WHERE s.BotProfileId = $botId AND s.ServerId = $serverId ";
        var templates = new List<ScheduledMessageFilterOption<Guid>>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT DISTINCT json_extract(s.DefinitionJson, '$.templateId'), COALESCE(t.Name, 'Deleted or unavailable template')" + scope + "AND json_extract(s.DefinitionJson, '$.templateId') IS NOT NULL ORDER BY 2 COLLATE NOCASE;";
            command.Parameters.AddWithValue("$botId", botProfileId.ToString("D")); command.Parameters.AddWithValue("$serverId", serverId.ToString(CultureInfo.InvariantCulture));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) if (Guid.TryParse(reader.GetString(0), out var id)) templates.Add(new(id, reader.GetString(1)));
        }
        var destinations = new List<ScheduledMessageFilterOption<ulong>>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT DISTINCT json_extract(s.DefinitionJson, '$.destination.channelId'), COALESCE(NULLIF(json_extract(s.DefinitionJson, '$.destination.channelName'), ''), 'Deleted or unavailable channel')" + scope + "AND json_extract(s.DefinitionJson, '$.destination.channelId') IS NOT NULL ORDER BY 2 COLLATE NOCASE;";
            command.Parameters.AddWithValue("$botId", botProfileId.ToString("D")); command.Parameters.AddWithValue("$serverId", serverId.ToString(CultureInfo.InvariantCulture));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && ulong.TryParse(reader.GetString(0), CultureInfo.InvariantCulture, out var id)) destinations.Add(new(id, reader.GetString(1)));
        }
        return new(templates, destinations);
    }

    public async Task<IReadOnlyList<ScheduledMessageOccurrenceListItem>> ListRecentOccurrencesAsync(Guid botProfileId, ulong serverId, Guid scheduleId, int limit, CancellationToken cancellationToken)
    {
        var boundedLimit = Math.Clamp(limit, 1, 50);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT o.OccurrenceId, o.OccurrenceAt, o.ReservedAt, o.FinishedAt, o.State, o.SafeFailureCode, o.ManualDecision, o.CorrelationId, COALESCE(o.SnapshotCompatibility, 'MissingRequiredData') FROM ScheduledMessageOccurrences o INNER JOIN ScheduledMessages s ON s.Id = o.ScheduledMessageId WHERE s.BotProfileId = $botId AND s.ServerId = $serverId AND o.ScheduledMessageId = $scheduleId ORDER BY o.OccurrenceAt DESC, o.OccurrenceId ASC LIMIT $limit;";
        command.Parameters.AddWithValue("$botId", botProfileId.ToString("D")); command.Parameters.AddWithValue("$serverId", serverId.ToString(CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$scheduleId", scheduleId.ToString("D")); command.Parameters.AddWithValue("$limit", boundedLimit);
        var result = new List<ScheduledMessageOccurrenceListItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var state = Enum.TryParse<MessageOperationState>(reader.GetString(4), out var parsedState) ? parsedState : MessageOperationState.Failed;
            var compatibility = Enum.TryParse<SnapshotCompatibility>(reader.GetString(8), out var parsedCompatibility) ? parsedCompatibility : SnapshotCompatibility.MissingRequiredData;
            result.Add(new(Guid.Parse(reader.GetString(0)), 1, DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), null, reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), state, reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), Guid.Parse(reader.GetString(7)), compatibility));
        }
        return result;
    }

    private static void AddScheduleParameters(Microsoft.Data.Sqlite.SqliteCommand command, ScheduledMessageQuery query)
    {
        command.Parameters.AddWithValue("$botId", query.BotProfileId.ToString("D"));
        command.Parameters.AddWithValue("$serverId", query.ServerId.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$search", query.Search?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$recurrence", query.Recurrence?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$templateId", query.TemplateId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$channelId", query.ChannelId?.ToString(CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$missedPolicy", query.MissedPolicy?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$lifecycle", query.Lifecycle?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$hasWarning", query.HasWarning is null ? DBNull.Value : query.HasWarning.Value ? 1 : 0);
        command.Parameters.AddWithValue("$createdFrom", query.CreatedFrom?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$createdTo", query.CreatedTo?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$nextFrom", query.NextRunFrom?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$nextTo", query.NextRunTo?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
    }

    private static string GetScheduleSort(ScheduledMessageSort sort) => sort switch
    {
        ScheduledMessageSort.NextRunDescending => "json_extract(s.DefinitionJson, '$.nextRunAt') DESC, s.Id ASC",
        ScheduledMessageSort.Name => "s.ScheduleName COLLATE NOCASE ASC, s.Id ASC",
        ScheduledMessageSort.CreatedNewest => "s.CreatedAt DESC, s.Id ASC",
        ScheduledMessageSort.CreatedOldest => "s.CreatedAt ASC, s.Id ASC",
        ScheduledMessageSort.UpdatedNewest => "s.UpdatedAt DESC, s.Id ASC",
        ScheduledMessageSort.State => "s.IsEnabled DESC, s.Id ASC",
        ScheduledMessageSort.LastResult => "json_extract(s.DefinitionJson, '$.lastRunAt') DESC, s.Id ASC",
        _ => "json_extract(s.DefinitionJson, '$.nextRunAt') ASC, s.Id ASC"
    };

    public async Task SaveAsync(ScheduledMessageDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO ScheduledMessages (Id, BotProfileId, ServerId, ScheduleName, IsEnabled, DefinitionJson, CreatedAt, UpdatedAt)
            VALUES ($id, $botId, $serverId, $scheduleName, $enabled, $definition, $createdAt, $updatedAt)
            ON CONFLICT(Id) DO UPDATE SET ScheduleName = excluded.ScheduleName, IsEnabled = excluded.IsEnabled, DefinitionJson = excluded.DefinitionJson, UpdatedAt = excluded.UpdatedAt;
            """;
        command.Parameters.AddWithValue("$id", definition.Id.ToString("D"));
        command.Parameters.AddWithValue("$botId", definition.BotProfileId.ToString("D"));
        command.Parameters.AddWithValue("$serverId", definition.Destination.ServerId.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$scheduleName", string.IsNullOrWhiteSpace(definition.Name) ? "Untitled schedule" : definition.Name.Trim());
        command.Parameters.AddWithValue("$enabled", definition.IsEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$definition", JsonSerializer.Serialize(definition, JsonOptions));
        command.Parameters.AddWithValue("$createdAt", definition.StartAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TrySaveAsync(ScheduledMessageDefinition definition, int expectedRevision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (expectedRevision < 0 || definition.Revision != expectedRevision + 1)
        {
            throw new ArgumentException("The proposed schedule revision is invalid.", nameof(definition));
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO ScheduledMessages (Id, BotProfileId, ServerId, ScheduleName, IsEnabled, DefinitionJson, CreatedAt, UpdatedAt)
            VALUES ($id, $botId, $serverId, $scheduleName, $enabled, $definition, $createdAt, $updatedAt)
            ON CONFLICT(Id) DO UPDATE SET ScheduleName = excluded.ScheduleName, IsEnabled = excluded.IsEnabled, DefinitionJson = excluded.DefinitionJson, UpdatedAt = excluded.UpdatedAt
            WHERE CAST(COALESCE(json_extract(ScheduledMessages.DefinitionJson, '$.revision'), 1) AS INTEGER) = $expectedRevision;
            """;
        command.Parameters.AddWithValue("$id", definition.Id.ToString("D"));
        command.Parameters.AddWithValue("$botId", definition.BotProfileId.ToString("D"));
        command.Parameters.AddWithValue("$serverId", definition.Destination.ServerId.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$scheduleName", string.IsNullOrWhiteSpace(definition.Name) ? "Untitled schedule" : definition.Name.Trim());
        command.Parameters.AddWithValue("$enabled", definition.IsEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$definition", JsonSerializer.Serialize(definition, JsonOptions));
        command.Parameters.AddWithValue("$createdAt", definition.StartAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$expectedRevision", expectedRevision);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<bool> TryReserveOccurrenceAsync(ScheduledMessageOccurrence occurrence, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT OR IGNORE INTO ScheduledMessageOccurrences (OccurrenceId, ScheduledMessageId, OccurrenceAt, State, CorrelationId, FinishedAt, SafeFailureCode, ImmutableDeliverySnapshotJson, ManualDecision, ReservedAt, SnapshotSchemaVersion, SnapshotCompatibility, HasBroadMention, SnapshotServerName, SnapshotChannelName, SnapshotChannelId, SnapshotTemplateId, SnapshotTemplateVersion) VALUES ($id, $scheduleId, $at, $state, $correlationId, NULL, NULL, $snapshot, $decision, $reservedAt, $schemaVersion, $compatibility, $broadMention, $serverName, $channelName, $channelId, $templateId, $templateVersion);";
        var metadata = GetSnapshotMetadata(occurrence.ImmutableDeliverySnapshotJson);
        command.Parameters.AddWithValue("$id", occurrence.Id.ToString("D"));
        command.Parameters.AddWithValue("$scheduleId", occurrence.ScheduledMessageId.ToString("D"));
        command.Parameters.AddWithValue("$at", occurrence.OccurrenceAt.ToString("O"));
        command.Parameters.AddWithValue("$state", occurrence.State.ToString());
        command.Parameters.AddWithValue("$correlationId", occurrence.CorrelationId.ToString("D"));
        command.Parameters.AddWithValue("$snapshot", occurrence.ImmutableDeliverySnapshotJson ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$decision", occurrence.ManualDecision ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$reservedAt", metadata.ReservedAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$schemaVersion", metadata.SchemaVersion ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$compatibility", metadata.Compatibility.ToString());
        command.Parameters.AddWithValue("$broadMention", metadata.HasBroadMention ? 1 : 0);
        command.Parameters.AddWithValue("$serverName", metadata.ServerName ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$channelName", metadata.ChannelName ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$channelId", metadata.ChannelId?.ToString(CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$templateId", metadata.TemplateId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$templateVersion", metadata.TemplateVersion ?? (object)DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task CompleteOccurrenceAsync(ScheduledMessageOccurrence occurrence, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ScheduledMessageOccurrences SET State = $state, FinishedAt = $finishedAt, SafeFailureCode = $failure, ManualDecision = $decision WHERE OccurrenceId = $id AND State = 'Delivering';";
        command.Parameters.AddWithValue("$id", occurrence.Id.ToString("D"));
        command.Parameters.AddWithValue("$state", occurrence.State.ToString());
        command.Parameters.AddWithValue("$finishedAt", occurrence.FinishedAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$failure", occurrence.SafeFailureCode ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$decision", occurrence.ManualDecision ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ScheduledMessageApproval>> ListPendingApprovalsAsync(Guid? botProfileId, ulong? serverId, CancellationToken cancellationToken)
    {
        var results = new List<ScheduledMessageApproval>();
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT o.OccurrenceId, o.ScheduledMessageId, o.OccurrenceAt, o.State, o.CorrelationId, o.FinishedAt, o.SafeFailureCode, o.ImmutableDeliverySnapshotJson, o.ManualDecision
            FROM ScheduledMessageOccurrences o INNER JOIN ScheduledMessages s ON s.Id = o.ScheduledMessageId
            WHERE o.State = 'PendingApproval' AND ($botId IS NULL OR s.BotProfileId = $botId) AND ($serverId IS NULL OR s.ServerId = $serverId)
            ORDER BY o.OccurrenceAt ASC LIMIT 200;
            """;
        command.Parameters.AddWithValue("$botId", botProfileId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$serverId", serverId?.ToString(CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(ReadApproval(reader));
        return results;
    }

    public async Task<IReadOnlyList<ScheduledApprovalScheduleOption>> ListApprovalSchedulesAsync(Guid? botProfileId, ulong? serverId, CancellationToken cancellationToken)
    {
        var results = new List<ScheduledApprovalScheduleOption>();
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT s.Id, s.ScheduleName, 0
            FROM ScheduledMessages s
            WHERE ($botId IS NULL OR s.BotProfileId = $botId)
              AND ($serverId IS NULL OR s.ServerId = $serverId)
            UNION
            SELECT o.ScheduledMessageId, 'Deleted or unavailable schedule', 1
            FROM ScheduledMessageOccurrences o
            LEFT JOIN ScheduledMessages s ON s.Id = o.ScheduledMessageId
            WHERE s.Id IS NULL
              AND ($botId IS NULL OR (json_valid(o.ImmutableDeliverySnapshotJson) AND COALESCE(json_extract(o.ImmutableDeliverySnapshotJson, '$.schedule.botProfileId'), json_extract(o.ImmutableDeliverySnapshotJson, '$.botProfileId')) = $botId))
              AND ($serverId IS NULL OR (json_valid(o.ImmutableDeliverySnapshotJson) AND COALESCE(json_extract(o.ImmutableDeliverySnapshotJson, '$.schedule.destination.serverId'), json_extract(o.ImmutableDeliverySnapshotJson, '$.destination.serverId')) = $serverId))
            ORDER BY 2 COLLATE NOCASE, 1
            LIMIT 200;
            """;
        command.Parameters.AddWithValue("$botId", botProfileId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$serverId", serverId?.ToString(CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new ScheduledApprovalScheduleOption(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetInt32(2) != 0));
        }
        return results;
    }

    public async Task<ScheduledApprovalPage> QueryApprovalsAsync(ScheduledApprovalQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.FromDue is { } from && query.ToDue is { } to && from > to) throw new ArgumentException("The due-date range is invalid.", nameof(query));
        if (query.PageSize is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(query), "Page size must be between 1 and 200.");
        var page = Math.Max(1, query.PageNumber); var size = query.PageSize;
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var where = """
            WHERE ($botId IS NULL OR s.BotProfileId = $botId)
              AND ($serverId IS NULL OR s.ServerId = $serverId)
              AND ($scheduleId IS NULL OR o.ScheduledMessageId = $scheduleId)
              AND ($state IS NULL OR o.State = $state)
              AND ($fromDue IS NULL OR o.OccurrenceAt >= $fromDue)
              AND ($toDue IS NULL OR o.OccurrenceAt <= $toDue)
              AND ($fromDecision IS NULL OR o.FinishedAt >= $fromDecision)
              AND ($toDecision IS NULL OR o.FinishedAt <= $toDecision)
              AND ($compatibility IS NULL OR o.SnapshotCompatibility = $compatibility)
              AND ($broadMention IS NULL OR o.HasBroadMention = $broadMention)
              AND ($manualReview IS NULL OR (o.State = 'Uncertain') = $manualReview)
              AND ($historyOnly = 0 OR o.State IN ('Delivered', 'Failed', 'Uncertain', 'Skipped', 'Archived'))
              AND ($search = '' OR s.ScheduleName LIKE '%' || $search || '%'
                   OR COALESCE(b.DisplayName, '') LIKE '%' || $search || '%'
                   OR COALESCE(o.SnapshotServerName, '') LIKE '%' || $search || '%'
                   OR COALESCE(o.SnapshotChannelName, '') LIKE '%' || $search || '%'
                   OR COALESCE(t.Name, '') LIKE '%' || $search || '%'
                   OR o.CorrelationId LIKE '%' || $search || '%'
                   OR o.OccurrenceId LIKE '%' || $search || '%')
            """;
        await using var count = connection.CreateCommand();
        count.CommandText = $"SELECT COUNT(*) FROM ScheduledMessageOccurrences o INNER JOIN ScheduledMessages s ON s.Id = o.ScheduledMessageId LEFT JOIN BotProfiles b ON b.Id = s.BotProfileId LEFT JOIN MessageTemplates t ON t.Id = o.SnapshotTemplateId {where};";
        AddQueryParameters(count, query);
        var total = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        var order = GetApprovalSort(query.Sort);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT o.OccurrenceId, o.ScheduledMessageId, s.ScheduleName, s.BotProfileId, s.ServerId, COALESCE(b.DisplayName, 'Saved bot'), COALESCE(o.SnapshotServerName, 'Unknown server'), o.SnapshotChannelId, COALESCE(o.SnapshotChannelName, 'Unknown channel'), o.OccurrenceAt, o.ReservedAt, o.State, o.SnapshotTemplateId, o.SnapshotTemplateVersion, o.HasBroadMention, COALESCE(o.SnapshotSchemaVersion, 0), COALESCE(o.SnapshotCompatibility, 'MissingRequiredData'), o.CorrelationId, o.SafeFailureCode, o.FinishedAt FROM ScheduledMessageOccurrences o INNER JOIN ScheduledMessages s ON s.Id = o.ScheduledMessageId LEFT JOIN BotProfiles b ON b.Id = s.BotProfileId LEFT JOIN MessageTemplates t ON t.Id = o.SnapshotTemplateId {where} ORDER BY {order} LIMIT $limit OFFSET $offset;";
        AddQueryParameters(command, query); command.Parameters.AddWithValue("$limit", size); command.Parameters.AddWithValue("$offset", (page - 1) * size);
        var items = new List<ScheduledApprovalListItem>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(ReadApprovalListItem(reader));
        }
        return new ScheduledApprovalPage(items, total, page, size, DateTimeOffset.UtcNow);
    }

    private static void AddQueryParameters(Microsoft.Data.Sqlite.SqliteCommand command, ScheduledApprovalQuery query)
    {
        command.Parameters.AddWithValue("$botId", query.BotProfileId?.ToString("D") ?? (object)DBNull.Value); command.Parameters.AddWithValue("$serverId", query.ServerId?.ToString(CultureInfo.InvariantCulture) ?? (object)DBNull.Value); command.Parameters.AddWithValue("$scheduleId", query.ScheduleId?.ToString("D") ?? (object)DBNull.Value); command.Parameters.AddWithValue("$state", query.State?.ToString() ?? (object)DBNull.Value); command.Parameters.AddWithValue("$fromDue", query.FromDue?.ToString("O") ?? (object)DBNull.Value); command.Parameters.AddWithValue("$toDue", query.ToDue?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$fromDecision", query.FromDecision?.ToString("O") ?? (object)DBNull.Value); command.Parameters.AddWithValue("$toDecision", query.ToDecision?.ToString("O") ?? (object)DBNull.Value); command.Parameters.AddWithValue("$compatibility", query.Compatibility?.ToString() ?? (object)DBNull.Value); command.Parameters.AddWithValue("$broadMention", query.HasBroadMention is null ? DBNull.Value : query.HasBroadMention.Value ? 1 : 0); command.Parameters.AddWithValue("$manualReview", query.RequiresManualReview is null ? DBNull.Value : query.RequiresManualReview.Value ? 1 : 0); command.Parameters.AddWithValue("$historyOnly", query.HistoryOnly ? 1 : 0); command.Parameters.AddWithValue("$search", query.Search?.Trim() ?? string.Empty);
    }

    private static string GetApprovalSort(ScheduledApprovalSort sort) => sort switch
    {
        ScheduledApprovalSort.DueDescending => "o.OccurrenceAt DESC, o.OccurrenceId ASC",
        ScheduledApprovalSort.NewestReservation => "COALESCE(o.ReservedAt, o.OccurrenceAt) DESC, o.OccurrenceId ASC",
        ScheduledApprovalSort.OldestReservation => "COALESCE(o.ReservedAt, o.OccurrenceAt) ASC, o.OccurrenceId ASC",
        ScheduledApprovalSort.ScheduleName => "s.ScheduleName COLLATE NOCASE ASC, o.OccurrenceId ASC",
        ScheduledApprovalSort.ServerName => "COALESCE(o.SnapshotServerName, '') COLLATE NOCASE ASC, o.OccurrenceId ASC",
        ScheduledApprovalSort.State => "o.State COLLATE NOCASE ASC, o.OccurrenceId ASC",
        ScheduledApprovalSort.DecisionNewest => "COALESCE(o.FinishedAt, o.OccurrenceAt) DESC, o.OccurrenceId ASC",
        ScheduledApprovalSort.DecisionOldest => "COALESCE(o.FinishedAt, o.OccurrenceAt) ASC, o.OccurrenceId ASC",
        _ => "o.OccurrenceAt ASC, o.OccurrenceId ASC"
    };

    private static ScheduledApprovalListItem ReadApprovalListItem(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        var item = new ScheduledApprovalListItem(
            Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2),
            Guid.Parse(reader.GetString(3)), ulong.Parse(reader.GetString(4), CultureInfo.InvariantCulture), reader.GetString(6),
            reader.IsDBNull(7) ? null : ulong.Parse(reader.GetString(7), CultureInfo.InvariantCulture), reader.GetString(8),
            DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), "UTC",
            reader.IsDBNull(10) ? null : DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            Enum.Parse<MessageOperationState>(reader.GetString(11)), reader.IsDBNull(12) ? null : Guid.Parse(reader.GetString(12)),
            reader.IsDBNull(13) ? null : reader.GetInt32(13), false, false, reader.GetInt32(14) != 0, reader.GetInt32(15),
            Enum.Parse<SnapshotCompatibility>(reader.GetString(16)), Guid.Parse(reader.GetString(17)),
            reader.IsDBNull(18) ? null : reader.GetString(18), reader.IsDBNull(19) ? null : DateTimeOffset.Parse(reader.GetString(19), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
        return item with { BotDisplayName = reader.GetString(5) };
    }

    private static ApprovalSnapshotMetadata GetSnapshotMetadata(string? snapshotJson)
    {
        if (string.IsNullOrWhiteSpace(snapshotJson)) return new(null, null, SnapshotCompatibility.MissingRequiredData, false, null, null, null, null, null);
        try
        {
            using var document = JsonDocument.Parse(snapshotJson);
            var root = document.RootElement;
            var isEnvelope = root.TryGetProperty("schedule", out var schedule);
            if (!isEnvelope) schedule = root;
            var schema = root.TryGetProperty("schemaVersion", out var schemaElement) && schemaElement.TryGetInt32(out var value) ? value : 0;
            var content = root.TryGetProperty("content", out var contentElement) ? contentElement : schedule.TryGetProperty("inlineContent", out var inlineContent) ? inlineContent : default;
            var destination = schedule.TryGetProperty("destination", out var destinationElement) ? destinationElement : default;
            var mentions = content.ValueKind != JsonValueKind.Undefined && content.TryGetProperty("allowedMentions", out var mentionElement) ? mentionElement : default;
            var hasBroadMention = mentions.ValueKind != JsonValueKind.Undefined && ((mentions.TryGetProperty("allowEveryoneAndHere", out var everyone) && everyone.ValueKind == JsonValueKind.True) || (mentions.TryGetProperty("allowRoleMentions", out var roles) && roles.ValueKind == JsonValueKind.True));
            var compatibility = !isEnvelope ? SnapshotCompatibility.SupportedLegacy : schema > 1 ? SnapshotCompatibility.UnsupportedNewerVersion : destination.ValueKind == JsonValueKind.Undefined || content.ValueKind == JsonValueKind.Undefined || mentions.ValueKind == JsonValueKind.Undefined ? SnapshotCompatibility.MissingRequiredData : SnapshotCompatibility.Supported;
            return new(
                root.TryGetProperty("reservedAt", out var reservedAt) && DateTimeOffset.TryParse(reservedAt.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedReservedAt) ? parsedReservedAt : null,
                isEnvelope ? schema : 0, compatibility, hasBroadMention,
                destination.ValueKind != JsonValueKind.Undefined && destination.TryGetProperty("serverName", out var serverName) ? serverName.GetString() : null,
                destination.ValueKind != JsonValueKind.Undefined && destination.TryGetProperty("channelName", out var channelName) ? channelName.GetString() : null,
                destination.ValueKind != JsonValueKind.Undefined && destination.TryGetProperty("channelId", out var channelId) && channelId.TryGetUInt64(out var parsedChannelId) ? parsedChannelId : null,
                root.TryGetProperty("templateId", out var templateId) && Guid.TryParse(templateId.GetString(), out var parsedTemplateId) ? parsedTemplateId : schedule.TryGetProperty("templateId", out var scheduleTemplateId) && Guid.TryParse(scheduleTemplateId.GetString(), out var parsedScheduleTemplateId) ? parsedScheduleTemplateId : null,
                root.TryGetProperty("templateVersion", out var templateVersion) && templateVersion.TryGetInt32(out var parsedTemplateVersion) ? parsedTemplateVersion : null);
        }
        catch (JsonException)
        {
            return new(null, null, SnapshotCompatibility.Corrupt, false, null, null, null, null, null);
        }
    }

    private sealed record ApprovalSnapshotMetadata(DateTimeOffset? ReservedAt, int? SchemaVersion, SnapshotCompatibility Compatibility, bool HasBroadMention, string? ServerName, string? ChannelName, ulong? ChannelId, Guid? TemplateId, int? TemplateVersion);

    public async Task<ScheduledMessageApproval?> GetApprovalAsync(Guid occurrenceId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT OccurrenceId, ScheduledMessageId, OccurrenceAt, State, CorrelationId, FinishedAt, SafeFailureCode, ImmutableDeliverySnapshotJson, ManualDecision FROM ScheduledMessageOccurrences WHERE OccurrenceId = $id;";
        command.Parameters.AddWithValue("$id", occurrenceId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadApproval(reader) : null;
    }

    public async Task<bool> TryClaimApprovalAsync(Guid occurrenceId, Guid correlationId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ScheduledMessageOccurrences SET State = 'ApprovalProcessing', CorrelationId = $correlationId, ManualDecision = 'Approved' WHERE OccurrenceId = $id AND State = 'PendingApproval';";
        command.Parameters.AddWithValue("$id", occurrenceId.ToString("D"));
        command.Parameters.AddWithValue("$correlationId", correlationId.ToString("D"));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<bool> TryDecideApprovalAsync(Guid occurrenceId, MessageOperationState terminalState, string decision, string? safeFailureCode, CancellationToken cancellationToken)
    {
        if (terminalState is not (MessageOperationState.Delivered or MessageOperationState.Failed or MessageOperationState.Uncertain or MessageOperationState.Skipped or MessageOperationState.Archived)) throw new ArgumentOutOfRangeException(nameof(terminalState));
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var expected = terminalState is MessageOperationState.Skipped or MessageOperationState.Archived ? "PendingApproval" : "ApprovalProcessing";
        command.CommandText = "UPDATE ScheduledMessageOccurrences SET State = $state, ManualDecision = $decision, SafeFailureCode = $failure, FinishedAt = $finishedAt WHERE OccurrenceId = $id AND State = $expected;";
        command.Parameters.AddWithValue("$id", occurrenceId.ToString("D")); command.Parameters.AddWithValue("$state", terminalState.ToString()); command.Parameters.AddWithValue("$decision", decision); command.Parameters.AddWithValue("$failure", safeFailureCode ?? (object)DBNull.Value); command.Parameters.AddWithValue("$finishedAt", DateTimeOffset.UtcNow.ToString("O")); command.Parameters.AddWithValue("$expected", expected);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static ScheduledMessageApproval ReadApproval(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        var snapshotJson = reader.IsDBNull(7) ? throw new InvalidOperationException("Approval snapshot is unavailable.") : reader.GetString(7);
        var envelope = JsonSerializer.Deserialize<ScheduledDeliverySnapshot>(snapshotJson, JsonOptions);
        var snapshot = envelope?.Schedule ?? JsonSerializer.Deserialize<ScheduledMessageDefinition>(snapshotJson, JsonOptions) ?? throw new InvalidOperationException("Approval snapshot is invalid.");
        var compatibility = envelope is null ? SnapshotCompatibility.SupportedLegacy : envelope.SchemaVersion > 1 ? SnapshotCompatibility.UnsupportedNewerVersion : snapshot.Destination.ChannelId is null || envelope.Content.AllowedMentions is null ? SnapshotCompatibility.MissingRequiredData : SnapshotCompatibility.Supported;
        return new ScheduledMessageApproval(new ScheduledMessageOccurrence(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), Enum.Parse<MessageOperationState>(reader.GetString(3)), Guid.Parse(reader.GetString(4)), reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), reader.IsDBNull(6) ? null : reader.GetString(6)) { ImmutableDeliverySnapshotJson = snapshotJson, ManualDecision = reader.IsDBNull(8) ? null : reader.GetString(8) }, snapshot) { ImmutableContent = envelope?.Content ?? snapshot.InlineContent, TemplateVersion = envelope?.TemplateVersion, Compatibility = compatibility, CompatibilityMessage = compatibility == SnapshotCompatibility.SupportedLegacy ? "This occurrence uses a supported legacy snapshot." : compatibility == SnapshotCompatibility.UnsupportedNewerVersion ? "This snapshot was created by a newer application version." : compatibility == SnapshotCompatibility.MissingRequiredData ? "The saved snapshot is missing delivery data." : null };
    }
}
