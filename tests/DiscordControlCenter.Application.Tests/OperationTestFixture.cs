using System.Collections.Immutable;
using DiscordControlCenter.Application.Explorer;
using DiscordControlCenter.Core.Common;
using DiscordControlCenter.Core.Explorer;

namespace DiscordControlCenter.Application.Tests;

internal static class OperationTestFixture
{
    internal static readonly Guid BotId =
        Guid.Parse("1745a7d2-469b-42df-a2cf-81ce5cb95768");
    internal const ulong ServerId = 100;
    internal const ulong EveryoneRoleId = ServerId;
    internal const ulong BotRoleId = 200;
    internal const ulong TargetRoleId = 201;
    internal const ulong CategoryId = 300;
    internal const ulong TextId = 301;
    internal const ulong VoiceId = 302;
    internal const ulong OtherTextId = 303;
    internal const ulong ForumId = 304;

    internal static FakeOperationExplorer Explorer(
        PermissionBits botPermissions = PermissionBits.Administrator,
        long sequence = 10) =>
        new(Snapshot(Server(botPermissions), sequence));

    internal static BotExplorerSnapshot Snapshot(ServerReadModel server, long sequence = 10) =>
        new(
            BotId,
            sequence,
            ExplorerCacheState.Ready,
            [server],
            DateTimeOffset.UtcNow,
            null)
        {
            LastAcceptedSequence = sequence,
            LastSuccessfulRefreshAt = DateTimeOffset.UtcNow
        };

    internal static ServerReadModel Server(
        PermissionBits botPermissions = PermissionBits.Administrator,
        ServerAvailability availability = ServerAvailability.Available,
        ImmutableArray<ChannelReadModel>? channels = null)
    {
        var roles = ImmutableArray.Create(
            new RoleReadModel(
                EveryoneRoleId,
                "@everyone",
                0,
                PermissionBits.ViewChannel,
                true),
            new RoleReadModel(
                TargetRoleId,
                "Operators",
                5,
                PermissionBits.ViewChannel,
                false),
            new RoleReadModel(
                BotRoleId,
                "Control Bot",
                10,
                botPermissions,
                false));
        var channelModels = channels ??
        [
            Channel(
                CategoryId,
                "Operations",
                ChannelKind.Category,
                0,
                null,
                [
                    Overwrite(EveryoneRoleId, PermissionBits.ViewChannel, PermissionBits.None),
                    Overwrite(TargetRoleId, PermissionBits.SendMessages, PermissionBits.AddReactions)
                ]),
            Channel(
                TextId,
                "general",
                ChannelKind.Text,
                0,
                CategoryId,
                [
                    Overwrite(
                        EveryoneRoleId,
                        PermissionBits.ViewChannel | PermissionBits.AddReactions,
                        PermissionBits.AttachFiles)
                ]),
            Channel(
                VoiceId,
                "voice",
                ChannelKind.Voice,
                1,
                CategoryId,
                [
                    Overwrite(
                        EveryoneRoleId,
                        PermissionBits.ViewChannel,
                        PermissionBits.Stream)
                ]),
            Channel(
                OtherTextId,
                "random",
                ChannelKind.Text,
                2,
                CategoryId,
                [
                    Overwrite(TargetRoleId, PermissionBits.None, PermissionBits.SendMessages)
                ]),
            Channel(ForumId, "forum", ChannelKind.Forum, 3, CategoryId, [])
        ];
        return new ServerReadModel(
            ServerId,
            "Disposable Test Server",
            null,
            "Test",
            999,
            DateTimeOffset.UtcNow.AddYears(-1),
            5,
            1,
            2,
            1,
            1,
            0,
            roles.Length,
            0,
            "None",
            0,
            "Control Bot",
            "Control Bot",
            10,
            500,
            [BotRoleId],
            roles,
            channelModels,
            availability,
            DateTimeOffset.UtcNow);
    }

    internal static ChannelReadModel Channel(
        ulong id,
        string name,
        ChannelKind kind,
        int position,
        ulong? categoryId,
        ImmutableArray<PermissionOverwriteReadModel> overwrites,
        string? topic = null) =>
        new(
            id,
            name,
            kind,
            kind.ToString(),
            position,
            DateTimeOffset.UtcNow.AddMonths(-1),
            categoryId,
            categoryId is null ? null : "Operations",
            categoryId is null ? null : false,
            overwrites,
            topic ?? (kind == ChannelKind.Text ? "Original topic" : null),
            kind == ChannelKind.Text ? false : null,
            kind == ChannelKind.Text ? 0 : null,
            kind == ChannelKind.Text ? 60 : null,
            kind == ChannelKind.Voice ? 64_000 : null,
            kind == ChannelKind.Voice ? 0 : null,
            null,
            kind == ChannelKind.Voice ? 0 : null,
            [],
            null,
            null,
            null);

    internal static PermissionOverwriteReadModel Overwrite(
        ulong targetId,
        PermissionBits allowed,
        PermissionBits denied,
        PermissionTargetKind targetType = PermissionTargetKind.Role) =>
        new(
            targetId,
            targetType,
            ToDiscordRaw(allowed),
            ToDiscordRaw(denied),
            allowed,
            denied);

    private static ulong ToDiscordRaw(PermissionBits permissions)
    {
        var raw = 0UL;
        Add(PermissionBits.ViewChannel, 1UL << 10);
        Add(PermissionBits.SendMessages, 1UL << 11);
        Add(PermissionBits.AddReactions, 1UL << 6);
        Add(PermissionBits.AttachFiles, 1UL << 15);
        Add(PermissionBits.Connect, 1UL << 20);
        Add(PermissionBits.Speak, 1UL << 21);
        Add(PermissionBits.Stream, 1UL << 9);
        return raw;

        void Add(PermissionBits bit, ulong discordRaw)
        {
            if (permissions.Has(bit))
            {
                raw |= discordRaw;
            }
        }
    }
}

internal sealed class FakeOperationExplorer(BotExplorerSnapshot snapshot) : IBotExplorerService
{
    public event EventHandler<ExplorerCacheChanged>? CacheChanged;

    public BotExplorerSnapshot Snapshot { get; private set; } = snapshot;

    public BotExplorerSnapshot GetSnapshot(Guid botProfileId) =>
        botProfileId == Snapshot.BotProfileId
            ? Snapshot
            : BotExplorerSnapshot.Disconnected(botProfileId);

    public Task<OperationResult> RefreshAsync(
        Guid botProfileId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(OperationResult.Success());
    }

    public Task<OperationResult> LoadMembersAsync(
        Guid botProfileId,
        ulong serverId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(OperationResult.Success());
    }

    public IReadOnlyList<BotDiagnosticsReadModel> GetDiagnostics() => [];

    public void SetSnapshot(BotExplorerSnapshot value, ulong? serverId = null)
    {
        Snapshot = value;
        CacheChanged?.Invoke(
            this,
            new ExplorerCacheChanged(
                value.BotProfileId,
                ExplorerCacheUpdateKind.ServerUpserted,
                serverId,
                value));
    }
}
