using System.Globalization;
using DiscordControlCenter.Core.Bots;
using Microsoft.Data.Sqlite;

namespace DiscordControlCenter.Infrastructure.Persistence;

public sealed class SqliteBotProfileRepository(SqliteConnectionFactory connectionFactory)
    : IBotProfileRepository
{
    public async Task<IReadOnlyList<BotProfile>> GetAllAsync(CancellationToken cancellationToken)
    {
        var profiles = new List<BotProfile>();
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, DisplayName, ProtectedToken, TokenFingerprint, IsEnabled, CreatedAt,
                   DiscordUserId, DiscordUsername, AvatarUrl, LastConnectedAt
            FROM BotProfiles
            ORDER BY DisplayName COLLATE NOCASE;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            profiles.Add(ReadProfile(reader));
        }

        return profiles;
    }

    public async Task<BotProfile?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, DisplayName, ProtectedToken, TokenFingerprint, IsEnabled, CreatedAt,
                   DiscordUserId, DiscordUsername, AvatarUrl, LastConnectedAt
            FROM BotProfiles
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadProfile(reader)
            : null;
    }

    public async Task AddAsync(BotProfile profile, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO BotProfiles
                (Id, DisplayName, ProtectedToken, TokenFingerprint, IsEnabled, CreatedAt,
                 DiscordUserId, DiscordUsername, AvatarUrl, LastConnectedAt)
            VALUES
                ($id, $displayName, $protectedToken, $tokenFingerprint, $isEnabled, $createdAt,
                 $discordUserId, $discordUsername, $avatarUrl, $lastConnectedAt);
            """;
        AddParameters(command, profile);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAsync(BotProfile profile, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE BotProfiles
            SET DisplayName = $displayName,
                ProtectedToken = $protectedToken,
                TokenFingerprint = $tokenFingerprint,
                IsEnabled = $isEnabled,
                CreatedAt = $createdAt,
                DiscordUserId = $discordUserId,
                DiscordUsername = $discordUsername,
                AvatarUrl = $avatarUrl,
                LastConnectedAt = $lastConnectedAt
            WHERE Id = $id;
            """;
        AddParameters(command, profile);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM BotProfiles WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static BotProfile ReadProfile(SqliteDataReader reader)
    {
        var userIdText = reader.IsDBNull(6) ? null : reader.GetString(6);
        return new BotProfile(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            (byte[])reader["ProtectedToken"],
            reader.GetString(3),
            reader.GetBoolean(4),
            ParseDate(reader.GetString(5)),
            userIdText is null ? null : ulong.Parse(userIdText, CultureInfo.InvariantCulture),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : ParseDate(reader.GetString(9)));
    }

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static void AddParameters(SqliteCommand command, BotProfile profile)
    {
        command.Parameters.AddWithValue("$id", profile.Id.ToString("D"));
        command.Parameters.AddWithValue("$displayName", profile.DisplayName);
        command.Parameters.Add("$protectedToken", SqliteType.Blob).Value = profile.ProtectedToken;
        command.Parameters.AddWithValue("$tokenFingerprint", profile.TokenFingerprint);
        command.Parameters.AddWithValue("$isEnabled", profile.IsEnabled);
        command.Parameters.AddWithValue("$createdAt", profile.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue(
            "$discordUserId",
            profile.DiscordUserId?.ToString(CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$discordUsername", profile.DiscordUsername ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$avatarUrl", profile.AvatarUrl ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$lastConnectedAt",
            profile.LastConnectedAt?.ToString("O") ?? (object)DBNull.Value);
    }
}
