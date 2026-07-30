using System.Collections.Immutable;
using System.Globalization;

namespace DiscordControlCenter.Core.Explorer;

public static class Snowflake
{
    private const long DiscordEpochMilliseconds = 1_420_070_400_000;

    public static DateTimeOffset DecodeTimestamp(ulong id)
    {
        var milliseconds = checked((long)(id >> 22) + DiscordEpochMilliseconds);
        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
    }
}

public static class PermissionSynchronization
{
    public static bool? AreSynchronized(
        ulong? categoryId,
        IEnumerable<PermissionOverwriteReadModel> channelOverwrites,
        IEnumerable<PermissionOverwriteReadModel>? categoryOverwrites)
    {
        if (categoryId is null || categoryOverwrites is null)
        {
            return null;
        }

        var channel = Normalize(channelOverwrites);
        var category = Normalize(categoryOverwrites);
        return channel.SequenceEqual(category);
    }

    private static IEnumerable<(ulong Id, PermissionTargetKind Type, ulong Allow, ulong Deny)> Normalize(
        IEnumerable<PermissionOverwriteReadModel> overwrites) =>
        overwrites
            .Select(overwrite => (
                overwrite.TargetId,
                overwrite.TargetType,
                overwrite.AllowedRaw,
                overwrite.DeniedRaw))
            .OrderBy(overwrite => overwrite.TargetId)
            .ThenBy(overwrite => overwrite.TargetType)
            .ThenBy(overwrite => overwrite.AllowedRaw)
            .ThenBy(overwrite => overwrite.DeniedRaw);
}

public static class ExplorerSearch
{
    public static ImmutableArray<RoleReadModel> OrderRoles(
        IEnumerable<RoleReadModel> roles) =>
        roles
            .OrderByDescending(role => role.IsEveryone ? int.MinValue : role.Position)
            .ThenByDescending(role => role.Id)
            .ToImmutableArray();

    public static ImmutableArray<ServerReadModel> FilterServers(
        IEnumerable<ServerReadModel> servers,
        string? searchText)
    {
        var term = searchText?.Trim();
        return servers
            .Where(server =>
                string.IsNullOrEmpty(term)
                || server.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || server.Id.ToString(CultureInfo.InvariantCulture)
                    .Contains(term, StringComparison.OrdinalIgnoreCase))
            .OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(server => server.Id)
            .ToImmutableArray();
    }

    public static ImmutableArray<ChannelGroupReadModel> BuildChannelTree(
        ServerReadModel server,
        string? searchText)
    {
        var term = searchText?.Trim();
        var categories = server.Channels
            .Where(channel => channel.Kind == ChannelKind.Category)
            .OrderBy(channel => channel.Position)
            .ThenBy(channel => channel.Id)
            .ToArray();
        var childChannels = server.Channels
            .Where(channel => channel.Kind != ChannelKind.Category)
            .ToArray();
        var groups = ImmutableArray.CreateBuilder<ChannelGroupReadModel>();

        var uncategorized = FilterChannels(
            childChannels.Where(channel => channel.CategoryId is null),
            term,
            categoryMatches: false);
        if (uncategorized.Length > 0)
        {
            groups.Add(new ChannelGroupReadModel(null, "Uncategorized", int.MinValue, uncategorized));
        }

        foreach (var category in categories)
        {
            var categoryMatches = Matches(category, term);
            var children = FilterChannels(
                childChannels.Where(channel => channel.CategoryId == category.Id),
                term,
                categoryMatches);
            if (children.Length > 0 || categoryMatches || string.IsNullOrEmpty(term))
            {
                groups.Add(new ChannelGroupReadModel(
                    category.Id,
                    category.Name,
                    category.Position,
                    children));
            }
        }

        return groups.ToImmutable();
    }

    private static ImmutableArray<ChannelReadModel> FilterChannels(
        IEnumerable<ChannelReadModel> channels,
        string? term,
        bool categoryMatches) =>
        channels
            .Where(channel => categoryMatches || Matches(channel, term))
            .OrderBy(channel => channel.Position)
            .ThenBy(channel => channel.Id)
            .ToImmutableArray();

    private static bool Matches(ChannelReadModel channel, string? term) =>
        string.IsNullOrEmpty(term)
        || channel.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
        || channel.Id.ToString(CultureInfo.InvariantCulture)
            .Contains(term, StringComparison.OrdinalIgnoreCase);
}
