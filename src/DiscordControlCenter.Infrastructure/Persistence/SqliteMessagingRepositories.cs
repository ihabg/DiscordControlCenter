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
            SELECT Id, Name, Description, ContentJson, VariablesJson, TagsJson, Version, CreatedAt, UpdatedAt, LastUsedAt
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
            "SELECT Id, Name, Description, ContentJson, VariablesJson, TagsJson, Version, CreatedAt, UpdatedAt, LastUsedAt FROM MessageTemplates WHERE Id = $id;";
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
            INSERT INTO MessageTemplates (Id, Name, Description, ContentJson, VariablesJson, TagsJson, Version, CreatedAt, UpdatedAt, LastUsedAt)
            VALUES ($id, $name, $description, $content, $variables, $tags, $version, $createdAt, $updatedAt, $lastUsedAt)
            ON CONFLICT(Id) DO UPDATE SET
                Name = excluded.Name, Description = excluded.Description, ContentJson = excluded.ContentJson,
                VariablesJson = excluded.VariablesJson, TagsJson = excluded.TagsJson, Version = excluded.Version,
                UpdatedAt = excluded.UpdatedAt, LastUsedAt = excluded.LastUsedAt;
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
            Version = reader.GetInt32(6)
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
