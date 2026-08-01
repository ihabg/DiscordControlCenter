using System.Collections.Concurrent;
using System.Collections.Immutable;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using DiscordControlCenter.Application.Bots;
using DiscordControlCenter.Application.Common;
using DiscordControlCenter.Application.Explorer;
using DiscordControlCenter.Application.Messaging;
using DiscordControlCenter.Application.Operations;
using DiscordControlCenter.Core.Bots;
using DiscordControlCenter.Core.Explorer;
using DiscordControlCenter.Core.Operations;
using DiscordControlCenter.Core.Messaging;
using Microsoft.Extensions.Logging;

namespace DiscordControlCenter.Discord;

public sealed class DiscordBotClient : IDiscordBotClient
{
    private const int MaximumCachedMembersPerServer = 100_000;
    private readonly Guid _botProfileId;
    private readonly bool _fullMemberAccessEnabled;
    private readonly DiscordSocketClient _client;
    private readonly ILogger<DiscordBotClient> _logger;
    private readonly object _snapshotLock = new();
    private readonly object _explorerBuildLock = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly ConcurrentDictionary<ulong, CancellationTokenSource> _voiceUpdateDebounce = new();
    private readonly ConcurrentDictionary<ulong, ConcurrentDictionary<ulong, VoiceStateCacheChange>>
        _pendingVoiceChanges = new();
    private readonly ConcurrentDictionary<ulong, CancellationTokenSource> _memberUpdateDebounce = new();
    private readonly ConcurrentDictionary<ulong, ConcurrentDictionary<ulong, PendingMemberChange>>
        _pendingMemberChanges = new();
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _memberLoadGates = new();
    private TaskCompletionSource _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private BotConnectionSnapshot _snapshot;
    private int _manualStop;
    private int _disposed;
    private long _explorerSequence;

    public DiscordBotClient(
        Guid botProfileId,
        bool enableFullMemberAccess,
        ILogger<DiscordBotClient> logger)
    {
        _botProfileId = botProfileId;
        _fullMemberAccessEnabled = enableFullMemberAccess;
        _logger = logger;
        _snapshot = BotConnectionSnapshot.Disconnected(botProfileId) with
        {
            FullMemberAccessEnabled = enableFullMemberAccess
        };
        _client = new DiscordSocketClient(
            new DiscordSocketConfig
            {
                GatewayIntents = GatewayIntents.Guilds
                    | GatewayIntents.GuildVoiceStates
                    | (enableFullMemberAccess ? GatewayIntents.GuildMembers : GatewayIntents.None),
                AlwaysDownloadUsers = false,
                LogGatewayIntentWarnings = true,
                MessageCacheSize = 0
            });

        _client.Ready += HandleReadyAsync;
        _client.Connected += HandleConnectedAsync;
        _client.Disconnected += HandleDisconnectedAsync;
        _client.LatencyUpdated += HandleLatencyUpdatedAsync;
        _client.GuildAvailable += HandleGuildChangedAsync;
        _client.GuildUnavailable += HandleGuildChangedAsync;
        _client.JoinedGuild += HandleGuildChangedAsync;
        _client.LeftGuild += HandleLeftGuildAsync;
        _client.GuildUpdated += HandleGuildUpdatedAsync;
        _client.ChannelCreated += HandleChannelCreatedAsync;
        _client.ChannelUpdated += HandleChannelUpdatedAsync;
        _client.ChannelDestroyed += HandleChannelDestroyedAsync;
        _client.RoleCreated += HandleRoleCreatedAsync;
        _client.RoleUpdated += HandleRoleUpdatedAsync;
        _client.RoleDeleted += HandleRoleDeletedAsync;
        _client.UserJoined += HandleUserJoinedAsync;
        _client.UserLeft += HandleUserLeftAsync;
        _client.GuildMemberUpdated += HandleGuildMemberUpdatedAsync;
        _client.GuildMembersDownloaded += HandleGuildMembersDownloadedAsync;
        _client.UserVoiceStateUpdated += HandleVoiceStateUpdatedAsync;
        _client.Log += HandleLogAsync;
    }

    public event EventHandler<BotConnectionSnapshot>? StatusChanged;
    public event EventHandler<ExplorerCacheUpdate>? ExplorerChanged;

    public BotConnectionSnapshot Snapshot
    {
        get
        {
            lock (_snapshotLock)
            {
                return _snapshot;
            }
        }
    }

    public async Task ConnectAsync(string token, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        Interlocked.Exchange(ref _manualStop, 0);
        _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Publish(Snapshot with
        {
            State = BotConnectionState.Connecting,
            ErrorMessage = null
        });

        try
        {
            await _client.LoginAsync(TokenType.Bot, token)
                .WaitAsync(TimeSpan.FromSeconds(20), cancellationToken)
                .ConfigureAwait(false);
            await _client.StartAsync()
                .WaitAsync(TimeSpan.FromSeconds(20), cancellationToken)
                .ConfigureAwait(false);
            await _ready.Task
                .WaitAsync(TimeSpan.FromSeconds(30), cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _manualStop, 1);
        _lifetimeCancellation.Cancel();
        Publish(Snapshot with { State = BotConnectionState.Disconnecting, ErrorMessage = null });
        await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        Publish(Snapshot with
        {
            State = BotConnectionState.Disconnected,
            GatewayLatencyMilliseconds = null,
            ServerCount = 0,
            LastDisconnectedAt = DateTimeOffset.UtcNow,
            ErrorMessage = null
        });
    }

    public Task<ExplorerCacheUpdate> RefreshExplorerAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return BuildResetUpdate();
            },
            cancellationToken);
    }

    public async Task<bool> MessageExistsAsync(ulong channelId, ulong messageId, CancellationToken cancellationToken)
    {
        var channel = _client.GetChannel(channelId) as IMessageChannel;
        if (channel is null) return false;
        var message = await channel.GetMessageAsync(messageId, CacheMode.AllowDownload, new RequestOptions { CancelToken = cancellationToken }).ConfigureAwait(false);
        return message is not null;
    }

    public async Task LoadMembersAsync(ulong serverId, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_fullMemberAccessEnabled)
        {
            throw new PrivilegedIntentException(
                "Enable full member access locally after enabling Server Members Intent in the Discord Developer Portal.");
        }

        var guild = _client.GetGuild(serverId)
            ?? throw new InvalidOperationException("The selected server is no longer available.");
        var gate = _memberLoadGates.GetOrAdd(serverId, _ => new SemaphoreSlim(1, 1));
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        var memberLoadToken = linkedCancellation.Token;
        await gate.WaitAsync(memberLoadToken).ConfigureAwait(false);
        try
        {
            PublishMemberState(
                guild,
                ExplorerCacheUpdateKind.MembersLoading,
                DataCompleteness.Loading,
                [],
                null,
                null);
            try
            {
                var options = new RequestOptions
                {
                    CancelToken = memberLoadToken
                };
                var translated = new Dictionary<ulong, MemberReadModel>();
                var fetchedMemberCount = 0;
                await foreach (var page in guild
                                   .GetUsersAsync(options)
                                   .WithCancellation(memberLoadToken)
                                   .ConfigureAwait(false))
                {
                    memberLoadToken.ThrowIfCancellationRequested();
                    fetchedMemberCount += page.Count;
                    var remainingCapacity = MaximumCachedMembersPerServer - translated.Count;
                    if (remainingCapacity <= 0)
                    {
                        continue;
                    }

                    var batch = page
                        .Take(remainingCapacity)
                        .Select(user => DiscordExplorerTranslator.TranslateMember(user, guild))
                        .Where(member => !translated.ContainsKey(member.Id))
                        .ToArray();
                    foreach (var member in batch)
                    {
                        translated[member.Id] = member;
                    }

                    foreach (var visibleBatch in batch.Chunk(250))
                    {
                        PublishMemberState(
                            guild,
                            ExplorerCacheUpdateKind.MembersBatchUpserted,
                            DataCompleteness.Loading,
                            visibleBatch,
                            null,
                            null);
                    }
                }

                var wasBounded = fetchedMemberCount > MaximumCachedMembersPerServer;
                PublishMemberState(
                    guild,
                    ExplorerCacheUpdateKind.MembersStateChanged,
                    wasBounded ? DataCompleteness.Partial : DataCompleteness.Complete,
                    translated.Values,
                    DateTimeOffset.UtcNow,
                    wasBounded
                        ? $"Member caching is limited to {MaximumCachedMembersPerServer:N0} entries per server."
                        : null);
            }
            catch (OperationCanceledException) when (memberLoadToken.IsCancellationRequested)
            {
                PublishMemberState(
                    guild,
                    ExplorerCacheUpdateKind.MembersStateChanged,
                    DataCompleteness.Cancelled,
                    [],
                    DateTimeOffset.UtcNow,
                    null);
                throw;
            }
            catch (Exception exception)
            {
                MemberDownloadFailedLog(
                    _logger,
                    _botProfileId,
                    serverId,
                    exception.GetType().Name,
                    null);
                PublishMemberState(
                    guild,
                    ExplorerCacheUpdateKind.MembersStateChanged,
                    DataCompleteness.Failed,
                    [],
                    DateTimeOffset.UtcNow,
                    "Member loading failed. Confirm the Developer Portal Server Members Intent toggle and retry.");
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<ChannelWriteOutcome> CreateCategoryAsync(
        ulong serverId,
        ChannelOperationStateSnapshot after,
        string? auditReason,
        CancellationToken cancellationToken) =>
        ExecuteWriteAsync(
            async () =>
            {
                var guild = GetWritableGuild(serverId);
                var channel = await ((IGuild)guild)
                    .CreateCategoryAsync(
                        after.Name,
                        properties =>
                        {
                            ApplyCommonCreateProperties(properties, after);
                            ApplyPermissionOverwrites(properties, after.PermissionOverwrites);
                        },
                        CreateRequestOptions(auditReason, cancellationToken))
                    .ConfigureAwait(false);
                return channel.Id;
            },
            cancellationToken);

    public Task<ChannelWriteOutcome> CreateTextChannelAsync(
        ulong serverId,
        ChannelOperationStateSnapshot after,
        string? auditReason,
        CancellationToken cancellationToken) =>
        ExecuteWriteAsync(
            async () =>
            {
                var guild = GetWritableGuild(serverId);
                var channel = await guild
                    .CreateTextChannelAsync(
                        after.Name,
                        properties =>
                        {
                            ApplyCommonCreateProperties(properties, after);
                            ApplyPermissionOverwrites(properties, after.PermissionOverwrites);
                            if (after.Topic is not null)
                            {
                                properties.Topic = after.Topic;
                            }

                            if (after.IsNsfw is bool isNsfw)
                            {
                                properties.IsNsfw = isNsfw;
                            }

                            if (after.SlowModeSeconds is int slowMode)
                            {
                                properties.SlowModeInterval = slowMode;
                            }

                            if (ToArchiveDuration(after.DefaultAutoArchiveMinutes)
                                is ThreadArchiveDuration archiveDuration)
                            {
                                properties.AutoArchiveDuration = archiveDuration;
                            }
                        },
                        CreateRequestOptions(auditReason, cancellationToken))
                    .ConfigureAwait(false);
                return channel.Id;
            },
            cancellationToken);

    public Task<ChannelWriteOutcome> CreateVoiceChannelAsync(
        ulong serverId,
        ChannelOperationStateSnapshot after,
        string? auditReason,
        CancellationToken cancellationToken) =>
        ExecuteWriteAsync(
            async () =>
            {
                var guild = GetWritableGuild(serverId);
                var channel = await guild
                    .CreateVoiceChannelAsync(
                        after.Name,
                        properties =>
                        {
                            ApplyCommonCreateProperties(properties, after);
                            ApplyPermissionOverwrites(properties, after.PermissionOverwrites);
                            if (after.Bitrate is int bitrate)
                            {
                                properties.Bitrate = bitrate;
                            }

                            if (after.UserLimit is int userLimit)
                            {
                                properties.UserLimit = userLimit;
                            }

                            if (after.RegionOverride is not null)
                            {
                                properties.RTCRegion = after.RegionOverride;
                            }
                        },
                        CreateRequestOptions(auditReason, cancellationToken))
                    .ConfigureAwait(false);
                return channel.Id;
            },
            cancellationToken);

    public Task<ChannelWriteOutcome> ModifyChannelAsync(
        ulong serverId,
        ulong channelId,
        ChannelOperationStateSnapshot before,
        ChannelOperationStateSnapshot after,
        string? auditReason,
        CancellationToken cancellationToken) =>
        ExecuteWriteAsync(
            async () =>
            {
                var guild = GetWritableGuild(serverId);
                var channel = guild.GetChannel(channelId)
                    ?? throw new ChannelWriteValidationException(
                        "TARGET_NOT_FOUND",
                        "The target channel no longer exists.");
                var options = CreateRequestOptions(auditReason, cancellationToken);
                switch (channel)
                {
                    case SocketTextChannel textChannel when after.Kind == ChannelKind.Text:
                        await textChannel
                            .ModifyAsync(
                                properties =>
                                {
                                    ApplyCommonModifiedProperties(properties, before, after);
                                    if (before.Topic != after.Topic)
                                    {
                                        properties.Topic = after.Topic;
                                    }

                                    if (before.IsNsfw != after.IsNsfw && after.IsNsfw is bool isNsfw)
                                    {
                                        properties.IsNsfw = isNsfw;
                                    }

                                    if (before.SlowModeSeconds != after.SlowModeSeconds
                                        && after.SlowModeSeconds is int slowMode)
                                    {
                                        properties.SlowModeInterval = slowMode;
                                    }

                                    if (before.DefaultAutoArchiveMinutes != after.DefaultAutoArchiveMinutes
                                        && ToArchiveDuration(after.DefaultAutoArchiveMinutes)
                                            is ThreadArchiveDuration archiveDuration)
                                    {
                                        properties.AutoArchiveDuration = archiveDuration;
                                    }
                                },
                                options)
                            .ConfigureAwait(false);
                        break;
                    case SocketVoiceChannel voiceChannel when after.Kind == ChannelKind.Voice:
                        await voiceChannel
                            .ModifyAsync(
                                properties =>
                                {
                                    ApplyCommonModifiedProperties(properties, before, after);
                                    if (before.Bitrate != after.Bitrate && after.Bitrate is int bitrate)
                                    {
                                        properties.Bitrate = bitrate;
                                    }

                                    if (before.UserLimit != after.UserLimit && after.UserLimit is int userLimit)
                                    {
                                        properties.UserLimit = userLimit;
                                    }

                                    if (before.RegionOverride != after.RegionOverride)
                                    {
                                        properties.RTCRegion = after.RegionOverride;
                                    }
                                },
                                options)
                            .ConfigureAwait(false);
                        break;
                    case SocketCategoryChannel categoryChannel when after.Kind == ChannelKind.Category:
                        await categoryChannel
                            .ModifyAsync(
                                properties => ApplyCommonModifiedProperties(properties, before, after),
                                options)
                            .ConfigureAwait(false);
                        break;
                    default:
                        throw new ChannelWriteValidationException(
                            "CHANNEL_TYPE_UNSUPPORTED",
                            "The target channel type changed or is unsupported.");
                }

                return channelId;
            },
            cancellationToken);

    public Task<ChannelWriteOutcome> ReorderChannelsAsync(
        ulong serverId,
        IReadOnlyList<ChannelPositionUpdate> positions,
        string? auditReason,
        CancellationToken cancellationToken) =>
        ExecuteWriteAsync(
            async () =>
            {
                var guild = GetWritableGuild(serverId);
                await guild
                    .ReorderChannelsAsync(
                        positions.Select(position =>
                            new ReorderChannelProperties(position.ChannelId, position.Position)),
                        CreateRequestOptions(auditReason, cancellationToken))
                    .ConfigureAwait(false);
                return (ulong?)null;
            },
            cancellationToken);

    public Task<ChannelWriteOutcome> SetPermissionOverwriteAsync(
        ulong serverId,
        ulong channelId,
        ChannelPermissionOverwriteSnapshot overwrite,
        string? auditReason,
        CancellationToken cancellationToken) =>
        ExecuteWriteAsync(
            async () =>
            {
                var guild = GetWritableGuild(serverId);
                var channel = guild.GetChannel(channelId)
                    ?? throw new ChannelWriteValidationException(
                        "TARGET_NOT_FOUND",
                        "The target channel no longer exists.");
                var permissions = new OverwritePermissions(
                    overwrite.AllowedRaw,
                    overwrite.DeniedRaw);
                var options = CreateRequestOptions(auditReason, cancellationToken);
                if (overwrite.TargetType == PermissionTargetKind.Role)
                {
                    var role = guild.GetRole(overwrite.TargetId)
                        ?? throw new ChannelWriteValidationException(
                            "OVERWRITE_ROLE_MISSING",
                            "The target overwrite role no longer exists.");
                    await channel.AddPermissionOverwriteAsync(role, permissions, options)
                        .ConfigureAwait(false);
                }
                else
                {
                    var user = guild.GetUser(overwrite.TargetId)
                        ?? await ((IGuild)guild)
                            .GetUserAsync(overwrite.TargetId, CacheMode.AllowDownload, options)
                            .ConfigureAwait(false)
                        ?? throw new ChannelWriteValidationException(
                            "OVERWRITE_MEMBER_MISSING",
                            "The target overwrite member is unavailable.");
                    await channel.AddPermissionOverwriteAsync(user, permissions, options)
                        .ConfigureAwait(false);
                }

                return channelId;
            },
            cancellationToken);

    public Task<ChannelWriteOutcome> DeletePermissionOverwriteAsync(
        ulong serverId,
        ulong channelId,
        ulong targetId,
        PermissionTargetKind targetType,
        string? auditReason,
        CancellationToken cancellationToken) =>
        ExecuteWriteAsync(
            async () =>
            {
                var guild = GetWritableGuild(serverId);
                var channel = guild.GetChannel(channelId)
                    ?? throw new ChannelWriteValidationException(
                        "TARGET_NOT_FOUND",
                        "The target channel no longer exists.");
                var options = CreateRequestOptions(auditReason, cancellationToken);
                if (targetType == PermissionTargetKind.Role)
                {
                    var role = guild.GetRole(targetId)
                        ?? throw new ChannelWriteValidationException(
                            "OVERWRITE_ROLE_MISSING",
                            "The target overwrite role no longer exists.");
                    await channel.RemovePermissionOverwriteAsync(role, options).ConfigureAwait(false);
                }
                else
                {
                    var user = guild.GetUser(targetId)
                        ?? await ((IGuild)guild)
                            .GetUserAsync(targetId, CacheMode.AllowDownload, options)
                            .ConfigureAwait(false)
                        ?? throw new ChannelWriteValidationException(
                            "OVERWRITE_MEMBER_MISSING",
                            "The target overwrite member is unavailable.");
                    await channel.RemovePermissionOverwriteAsync(user, options).ConfigureAwait(false);
                }

                return channelId;
            },
            cancellationToken);

    public Task<ChannelWriteOutcome> DeleteChannelAsync(
        ulong serverId,
        ulong channelId,
        string? auditReason,
        CancellationToken cancellationToken) =>
        ExecuteWriteAsync(
            async () =>
            {
                var guild = GetWritableGuild(serverId);
                var channel = guild.GetChannel(channelId)
                    ?? throw new ChannelWriteValidationException(
                        "TARGET_NOT_FOUND",
                        "The target channel no longer exists.");
                await channel
                    .DeleteAsync(CreateRequestOptions(auditReason, cancellationToken))
                    .ConfigureAwait(false);
                return channelId;
            },
            cancellationToken);

    public Task<MessageWriteOutcome> SendChannelMessageAsync(
        MessageOperationPlan plan,
        CancellationToken cancellationToken) =>
        ExecuteMessageWriteAsync(
            async () =>
            {
                var guild = GetWritableGuild(plan.Destination.ServerId);
                var channel = guild.GetChannel(plan.Destination.ChannelId ?? 0) as IMessageChannel
                    ?? throw new ChannelWriteValidationException(
                        "CHANNEL_UNAVAILABLE",
                        "The destination channel is unavailable.");
                var message = await channel.SendMessageAsync(
                    plan.Content.Body,
                    false,
                    ToEmbed(plan.Content.Embed),
                    CreateRequestOptions(plan.SafeAuditContext, cancellationToken),
                    AllowedMentions.None).ConfigureAwait(false);
                return message.Id;
            },
            cancellationToken);

    public Task<MessageWriteOutcome> SendDirectMessageAsync(
        MessageOperationPlan plan,
        CancellationToken cancellationToken) =>
        ExecuteMessageWriteAsync(
            async () =>
            {
                _ = GetWritableGuild(plan.Destination.ServerId);
                var user = _client.GetUser(plan.Destination.RecipientUserId ?? 0)
                    ?? throw new ChannelWriteValidationException(
                        "MEMBER_UNAVAILABLE",
                        "The selected direct-message recipient is unavailable in the current bot cache.");
                var channel = await user.CreateDMChannelAsync(
                    CreateRequestOptions(plan.SafeAuditContext, cancellationToken)).ConfigureAwait(false);
                var message = await channel.SendMessageAsync(
                    plan.Content.Body,
                    false,
                    ToEmbed(plan.Content.Embed),
                    CreateRequestOptions(plan.SafeAuditContext, cancellationToken),
                    AllowedMentions.None).ConfigureAwait(false);
                return message.Id;
            },
            cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _manualStop, 1);
        _lifetimeCancellation.Cancel();
        _client.Ready -= HandleReadyAsync;
        _client.Connected -= HandleConnectedAsync;
        _client.Disconnected -= HandleDisconnectedAsync;
        _client.LatencyUpdated -= HandleLatencyUpdatedAsync;
        _client.GuildAvailable -= HandleGuildChangedAsync;
        _client.GuildUnavailable -= HandleGuildChangedAsync;
        _client.JoinedGuild -= HandleGuildChangedAsync;
        _client.LeftGuild -= HandleLeftGuildAsync;
        _client.GuildUpdated -= HandleGuildUpdatedAsync;
        _client.ChannelCreated -= HandleChannelCreatedAsync;
        _client.ChannelUpdated -= HandleChannelUpdatedAsync;
        _client.ChannelDestroyed -= HandleChannelDestroyedAsync;
        _client.RoleCreated -= HandleRoleCreatedAsync;
        _client.RoleUpdated -= HandleRoleUpdatedAsync;
        _client.RoleDeleted -= HandleRoleDeletedAsync;
        _client.UserJoined -= HandleUserJoinedAsync;
        _client.UserLeft -= HandleUserLeftAsync;
        _client.GuildMemberUpdated -= HandleGuildMemberUpdatedAsync;
        _client.GuildMembersDownloaded -= HandleGuildMembersDownloadedAsync;
        _client.UserVoiceStateUpdated -= HandleVoiceStateUpdatedAsync;
        _client.Log -= HandleLogAsync;
        await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
        _client.Dispose();
        _lifetimeCancellation.Dispose();
    }

    private async Task HandleReadyAsync()
    {
        var currentUser = _client.CurrentUser;
        var identity = new BotIdentity(
            currentUser.Id,
            currentUser.Username,
            currentUser.GetAvatarUrl(ImageFormat.Auto, 128) ?? currentUser.GetDefaultAvatarUrl());
        var now = DateTimeOffset.UtcNow;
        Publish(new BotConnectionSnapshot(
            _botProfileId,
            BotConnectionState.Connected,
            _client.Latency,
            _client.Guilds.Count,
            identity,
            now,
            null,
            now,
            Snapshot.LastDisconnectedAt,
            Snapshot.LastReconnectedAt,
            _fullMemberAccessEnabled,
            Snapshot.VoiceStateEventCount,
            Snapshot.LastVoiceStateEventAt,
            null));
        await PublishResetAsync().ConfigureAwait(false);
        _ready.TrySetResult();
    }

    private async Task HandleConnectedAsync()
    {
        if (Snapshot.State == BotConnectionState.Reconnecting)
        {
            Publish(Snapshot with { State = BotConnectionState.Connecting });
            await PublishResetAsync().ConfigureAwait(false);
            Publish(Snapshot with
            {
                State = BotConnectionState.Connected,
                ServerCount = _client.Guilds.Count,
                GatewayLatencyMilliseconds = _client.Latency,
                LastConnectedAt = DateTimeOffset.UtcNow,
                LastReconnectedAt = DateTimeOffset.UtcNow,
                ErrorMessage = null
            });
        }
    }

    private Task HandleDisconnectedAsync(Exception exception)
    {
        if (exception is WebSocketClosedException { CloseCode: 4014 })
        {
            const string message =
                "Discord rejected GuildMembers (close code 4014). Enable Server Members Intent in the Developer Portal, or disable full member access locally.";
            Interlocked.Exchange(ref _manualStop, 1);
            Publish(Snapshot with
            {
                State = BotConnectionState.Faulted,
                GatewayLatencyMilliseconds = null,
                LastDisconnectedAt = DateTimeOffset.UtcNow,
                ErrorMessage = message,
                RecentGatewayError = "GuildMembers was rejected with gateway close code 4014."
            });
            _ready.TrySetException(new PrivilegedIntentException(message));
            PublishExplorer(
                ExplorerCacheUpdate.Clear(
                    _botProfileId,
                    NextExplorerSequence(),
                    DateTimeOffset.UtcNow));
            PrivilegedIntentRejectedLog(_logger, _botProfileId, null);
            return Task.CompletedTask;
        }

        var isManual = Volatile.Read(ref _manualStop) != 0;
        Publish(Snapshot with
        {
            State = isManual
                ? BotConnectionState.Disconnected
                : BotConnectionState.Reconnecting,
            GatewayLatencyMilliseconds = null,
            LastDisconnectedAt = DateTimeOffset.UtcNow,
            RecentGatewayError = isManual
                ? Snapshot.RecentGatewayError
                : "The gateway connection was interrupted.",
            ErrorMessage = isManual ? null : "The gateway connection was interrupted; Discord.Net is reconnecting."
        });
        if (!isManual)
        {
            GatewayDisconnectedLog(_logger, _botProfileId, exception.GetType().Name, null);
        }

        PublishExplorer(
            ExplorerCacheUpdate.Clear(
                _botProfileId,
                NextExplorerSequence(),
                DateTimeOffset.UtcNow));
        return Task.CompletedTask;
    }

    private Task HandleLatencyUpdatedAsync(int oldLatency, int newLatency)
    {
        _ = oldLatency;
        Publish(Snapshot with { GatewayLatencyMilliseconds = newLatency });
        return Task.CompletedTask;
    }

    private Task HandleGuildChangedAsync(SocketGuild guild)
    {
        Publish(Snapshot with { ServerCount = _client.Guilds.Count });
        PublishServer(guild);
        return Task.CompletedTask;
    }

    private Task HandleLeftGuildAsync(SocketGuild guild)
    {
        Publish(Snapshot with { ServerCount = _client.Guilds.Count });
        PublishExplorer(
            ExplorerCacheUpdate.Remove(
                _botProfileId,
                NextExplorerSequence(),
                guild.Id,
                DateTimeOffset.UtcNow));
        return Task.CompletedTask;
    }

    private Task HandleGuildUpdatedAsync(SocketGuild before, SocketGuild after)
    {
        _ = before;
        PublishServer(after);
        return Task.CompletedTask;
    }

    private Task HandleChannelCreatedAsync(SocketChannel channel) =>
        PublishContainingGuildAsync(channel);

    private Task HandleChannelUpdatedAsync(SocketChannel before, SocketChannel after)
    {
        _ = before;
        return PublishContainingGuildAsync(after);
    }

    private Task HandleChannelDestroyedAsync(SocketChannel channel) =>
        PublishContainingGuildAsync(channel);

    private Task HandleRoleCreatedAsync(SocketRole role)
    {
        PublishServer(role.Guild);
        return Task.CompletedTask;
    }

    private Task HandleRoleUpdatedAsync(SocketRole before, SocketRole after)
    {
        _ = before;
        PublishServer(after.Guild);
        return Task.CompletedTask;
    }

    private Task HandleRoleDeletedAsync(SocketRole role)
    {
        PublishServer(role.Guild);
        return Task.CompletedTask;
    }

    private Task HandleUserJoinedAsync(SocketGuildUser user)
    {
        if (_fullMemberAccessEnabled)
        {
            QueueMemberChange(
                user.Guild,
                user.Id,
                DiscordExplorerTranslator.TranslateMember(user, user.Guild),
                removed: false);
        }

        return Task.CompletedTask;
    }

    private Task HandleUserLeftAsync(SocketGuild guild, SocketUser user)
    {
        if (_fullMemberAccessEnabled)
        {
            QueueMemberChange(guild, user.Id, null, removed: true);
        }

        return Task.CompletedTask;
    }

    private Task HandleGuildMemberUpdatedAsync(
        Cacheable<SocketGuildUser, ulong> before,
        SocketGuildUser after)
    {
        _ = before;
        if (_fullMemberAccessEnabled)
        {
            QueueMemberChange(
                after.Guild,
                after.Id,
                DiscordExplorerTranslator.TranslateMember(after, after.Guild),
                removed: false);
        }

        if (after.Id == _client.CurrentUser.Id)
        {
            PublishServer(after.Guild);
        }

        return Task.CompletedTask;
    }

    private Task HandleGuildMembersDownloadedAsync(SocketGuild guild)
    {
        if (!_fullMemberAccessEnabled)
        {
            return Task.CompletedTask;
        }

        var members = guild.Users
            .Take(MaximumCachedMembersPerServer)
            .Select(user => DiscordExplorerTranslator.TranslateMember(user, guild))
            .ToArray();
        PublishMemberState(
            guild,
            ExplorerCacheUpdateKind.MembersStateChanged,
            guild.Users.Count > MaximumCachedMembersPerServer
                ? DataCompleteness.Partial
                : DataCompleteness.Complete,
            members,
            DateTimeOffset.UtcNow,
            guild.Users.Count > MaximumCachedMembersPerServer
                ? $"Member caching is limited to {MaximumCachedMembersPerServer:N0} entries per server."
                : null);
        return Task.CompletedTask;
    }

    private Task HandleVoiceStateUpdatedAsync(
        SocketUser user,
        SocketVoiceState before,
        SocketVoiceState after)
    {
        var guildId = after.VoiceChannel?.Guild.Id ?? before.VoiceChannel?.Guild.Id;
        if (guildId is ulong id)
        {
            var guild = _client.GetGuild(id);
            var guildUser = guild?.GetUser(user.Id);
            if (guild is not null && guildUser is not null)
            {
                var voiceState = DiscordExplorerTranslator.TranslateVoiceState(guildUser, after);
                var member = DiscordExplorerTranslator.TranslateMember(guildUser, guild);
                QueueVoiceUpdate(
                    new VoiceStateCacheChange(id, user.Id, member, voiceState));
                Publish(Snapshot with
                {
                    VoiceStateEventCount = Snapshot.VoiceStateEventCount + 1,
                    LastVoiceStateEventAt = DateTimeOffset.UtcNow
                });
            }
        }

        return Task.CompletedTask;
    }

    private Task HandleLogAsync(LogMessage message)
    {
        if (Volatile.Read(ref _manualStop) != 0
            && message.Exception is OperationCanceledException)
        {
            return Task.CompletedTask;
        }

        var level = message.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Trace,
            _ => LogLevel.Debug
        };
        var exceptionType = message.Exception?.GetType().Name ?? "None";
        var write = level switch
        {
            LogLevel.Critical => DiscordCriticalLog,
            LogLevel.Error => DiscordErrorLog,
            LogLevel.Warning => DiscordWarningLog,
            LogLevel.Information => DiscordInformationLog,
            LogLevel.Trace => DiscordTraceLog,
            _ => DiscordDebugLog
        };
        write(_logger, message.Source, message.Severity.ToString(), exceptionType, null);
        return Task.CompletedTask;
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        if (_client.ConnectionState != ConnectionState.Disconnected)
        {
            await _client.StopAsync()
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
                .ConfigureAwait(false);
        }

        if (_client.LoginState != LoginState.LoggedOut)
        {
            await _client.LogoutAsync()
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private Task PublishResetAsync() =>
        Task.Run(
            () =>
            {
                try
                {
                    PublishExplorer(BuildResetUpdate());
                }
                catch (Exception exception)
                {
                    PublishExplorerFault(exception);
                }
            });

    private ExplorerCacheUpdate BuildResetUpdate()
    {
        lock (_explorerBuildLock)
        {
            var now = DateTimeOffset.UtcNow;
            var servers = _client.Guilds
                .Select(guild => DiscordExplorerTranslator.TranslateServer(
                    guild,
                    now,
                    _fullMemberAccessEnabled))
                .ToArray();
            return ExplorerCacheUpdate.Reset(
                _botProfileId,
                NextExplorerSequence(),
                servers,
                now);
        }
    }

    private Task PublishContainingGuildAsync(SocketChannel channel)
    {
        if (channel is SocketGuildChannel guildChannel)
        {
            var guild = _client.GetGuild(guildChannel.Guild.Id);
            if (guild is not null)
            {
                PublishServer(guild);
            }
        }

        return Task.CompletedTask;
    }

    private void PublishServer(SocketGuild guild)
    {
        if (Volatile.Read(ref _manualStop) != 0 || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            lock (_explorerBuildLock)
            {
                var now = DateTimeOffset.UtcNow;
                var server = DiscordExplorerTranslator.TranslateServer(
                    guild,
                    now,
                    _fullMemberAccessEnabled);
                PublishExplorer(
                    ExplorerCacheUpdate.Upsert(
                        _botProfileId,
                        NextExplorerSequence(),
                        server,
                        now));
            }
        }
        catch (Exception exception)
        {
            PublishExplorerFault(exception);
        }
    }

    private void QueueVoiceUpdate(VoiceStateCacheChange change)
    {
        var pending = _pendingVoiceChanges.GetOrAdd(
            change.ServerId,
            _ => new ConcurrentDictionary<ulong, VoiceStateCacheChange>());
        pending[change.UserId] = change;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        _voiceUpdateDebounce.AddOrUpdate(
            change.ServerId,
            cancellation,
            (_, existing) =>
            {
                existing.Cancel();
                existing.Dispose();
                return cancellation;
            });
        _ = PublishDebouncedVoiceUpdateAsync(change.ServerId, cancellation);
    }

    private async Task PublishDebouncedVoiceUpdateAsync(
        ulong guildId,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellation.Token)
                .ConfigureAwait(false);
            if (_pendingVoiceChanges.TryRemove(guildId, out var pending)
                && !pending.IsEmpty)
            {
                PublishExplorer(
                    ExplorerCacheUpdate.Voice(
                        _botProfileId,
                        NextExplorerSequence(),
                        pending.Values,
                        DateTimeOffset.UtcNow));
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (_voiceUpdateDebounce.TryGetValue(guildId, out var current)
                && ReferenceEquals(current, cancellation))
            {
                _voiceUpdateDebounce.TryRemove(guildId, out _);
                cancellation.Dispose();
            }
        }
    }

    private void QueueMemberChange(
        SocketGuild guild,
        ulong memberId,
        MemberReadModel? member,
        bool removed)
    {
        var pending = _pendingMemberChanges.GetOrAdd(
            guild.Id,
            _ => new ConcurrentDictionary<ulong, PendingMemberChange>());
        pending[memberId] = new PendingMemberChange(memberId, member, removed);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        _memberUpdateDebounce.AddOrUpdate(
            guild.Id,
            cancellation,
            (_, existing) =>
            {
                existing.Cancel();
                existing.Dispose();
                return cancellation;
            });
        _ = PublishDebouncedMemberUpdatesAsync(guild, cancellation);
    }

    private async Task PublishDebouncedMemberUpdatesAsync(
        SocketGuild guild,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellation.Token)
                .ConfigureAwait(false);
            if (!_pendingMemberChanges.TryRemove(guild.Id, out var pending))
            {
                return;
            }

            var upserts = pending.Values
                .Where(change => !change.Removed && change.Member is not null)
                .Select(change => change.Member!)
                .ToArray();
            if (upserts.Length > 0)
            {
                PublishMemberState(
                    guild,
                    ExplorerCacheUpdateKind.MembersBatchUpserted,
                    DataCompleteness.Partial,
                    upserts,
                    DateTimeOffset.UtcNow,
                    null);
            }

            foreach (var removed in pending.Values.Where(change => change.Removed))
            {
                PublishExplorer(
                    ExplorerCacheUpdate.Members(
                        _botProfileId,
                        NextExplorerSequence(),
                        ExplorerCacheUpdateKind.MemberRemoved,
                        new MemberCacheStateChange(
                            guild.Id,
                            removed.MemberId,
                            DataCompleteness.Partial,
                            _fullMemberAccessEnabled,
                            [],
                            guild.MemberCount,
                            DateTimeOffset.UtcNow,
                            null),
                        DateTimeOffset.UtcNow));
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (_memberUpdateDebounce.TryGetValue(guild.Id, out var current)
                && ReferenceEquals(current, cancellation))
            {
                _memberUpdateDebounce.TryRemove(guild.Id, out _);
                cancellation.Dispose();
            }
        }
    }

    private void PublishMemberState(
        SocketGuild guild,
        ExplorerCacheUpdateKind kind,
        DataCompleteness completeness,
        IEnumerable<MemberReadModel> members,
        DateTimeOffset? refreshedAt,
        string? errorMessage)
    {
        var now = DateTimeOffset.UtcNow;
        PublishExplorer(
            ExplorerCacheUpdate.Members(
                _botProfileId,
                NextExplorerSequence(),
                kind,
                new MemberCacheStateChange(
                    guild.Id,
                    null,
                    completeness,
                    _fullMemberAccessEnabled,
                    members.ToImmutableArray(),
                    guild.MemberCount,
                    refreshedAt,
                    errorMessage),
                now));
    }

    private void PublishExplorerFault(Exception exception)
    {
        ExplorerUpdateFailedLog(_logger, _botProfileId, exception.GetType().Name, null);
        PublishExplorer(
            ExplorerCacheUpdate.Fault(
                _botProfileId,
                NextExplorerSequence(),
                "Discord server information could not be updated.",
                DateTimeOffset.UtcNow));
    }

    private long NextExplorerSequence() => Interlocked.Increment(ref _explorerSequence);

    private void PublishExplorer(ExplorerCacheUpdate update) =>
        ExplorerChanged?.Invoke(this, update);

    private void Publish(BotConnectionSnapshot snapshot)
    {
        lock (_snapshotLock)
        {
            _snapshot = snapshot;
        }

        StatusChanged?.Invoke(this, snapshot);
    }

    private SocketGuild GetWritableGuild(ulong serverId)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Snapshot.State != BotConnectionState.Connected)
        {
            throw new ChannelWriteValidationException(
                "BOT_DISCONNECTED",
                "The selected bot is not connected.");
        }

        return _client.GetGuild(serverId)
            ?? throw new ChannelWriteValidationException(
                "SERVER_UNAVAILABLE",
                "The selected server is unavailable.");
    }

    private static RequestOptions CreateRequestOptions(
        string? auditReason,
        CancellationToken cancellationToken) =>
        new()
        {
            AuditLogReason = AuditReasonSanitizer.Sanitize(auditReason),
            CancelToken = cancellationToken,
            RetryMode = RetryMode.AlwaysRetry
        };

    private static void ApplyCommonCreateProperties(
        GuildChannelProperties properties,
        ChannelOperationStateSnapshot after)
    {
        properties.Position = after.Position;
        properties.CategoryId = after.ParentCategoryId;
    }

    private static void ApplyCommonModifiedProperties(
        GuildChannelProperties properties,
        ChannelOperationStateSnapshot before,
        ChannelOperationStateSnapshot after)
    {
        if (!string.Equals(before.Name, after.Name, StringComparison.Ordinal))
        {
            properties.Name = after.Name;
        }

        if (before.Position != after.Position)
        {
            properties.Position = after.Position;
        }

        if (before.ParentCategoryId != after.ParentCategoryId)
        {
            properties.CategoryId = after.ParentCategoryId;
        }
    }

    private static void ApplyPermissionOverwrites(
        GuildChannelProperties properties,
        IEnumerable<ChannelPermissionOverwriteSnapshot> overwrites)
    {
        var values = overwrites
            .Select(
                overwrite => new Overwrite(
                    overwrite.TargetId,
                    overwrite.TargetType == PermissionTargetKind.Role
                        ? PermissionTarget.Role
                        : PermissionTarget.User,
                    new OverwritePermissions(overwrite.AllowedRaw, overwrite.DeniedRaw)))
            .ToArray();
        if (values.Length > 0)
        {
            properties.PermissionOverwrites = values;
        }
    }

    private static ThreadArchiveDuration? ToArchiveDuration(int? minutes) =>
        minutes switch
        {
            60 => ThreadArchiveDuration.OneHour,
            1440 => ThreadArchiveDuration.OneDay,
            4320 => ThreadArchiveDuration.ThreeDays,
            10080 => ThreadArchiveDuration.OneWeek,
            _ => null
        };

    private static Embed? ToEmbed(EmbedDraft? draft)
    {
        if (draft is null)
        {
            return null;
        }

        var builder = new EmbedBuilder
        {
            Title = draft.Title,
            Description = draft.Description,
            Url = draft.Url,
            Color = draft.Color is uint color ? new Color(color) : null,
            ThumbnailUrl = draft.ThumbnailUrl,
            ImageUrl = draft.ImageUrl,
            Timestamp = draft.Timestamp
        };
        if (!string.IsNullOrWhiteSpace(draft.AuthorName))
        {
            builder.Author = new EmbedAuthorBuilder
            {
                Name = draft.AuthorName,
                Url = draft.AuthorUrl,
                IconUrl = draft.AuthorIconUrl
            };
        }

        if (!string.IsNullOrWhiteSpace(draft.FooterText))
        {
            builder.Footer = new EmbedFooterBuilder
            {
                Text = draft.FooterText,
                IconUrl = draft.FooterIconUrl
            };
        }

        foreach (var field in draft.Fields)
        {
            builder.AddField(field.Name, field.Value, field.Inline);
        }

        return builder.Build();
    }

    private static async Task<MessageWriteOutcome> ExecuteMessageWriteAsync(
        Func<Task<ulong>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return new(true, await operation().ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ChannelWriteValidationException exception)
        {
            return new(false, null, new(
                MessageDeliveryFailureKind.DestinationUnavailable,
                exception.SafeCode,
                exception.SafeMessage,
                false,
                false));
        }
        catch (HttpException exception) when (exception.HttpCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound)
        {
            return new(false, null, new(
                MessageDeliveryFailureKind.MissingPermission,
                "DISCORD_MESSAGE_REJECTED",
                "Discord rejected delivery because the destination is unavailable or permissions are missing.",
                false,
                false));
        }
        catch (HttpException)
        {
            return new(false, null, new(
                MessageDeliveryFailureKind.Transient,
                "DISCORD_MESSAGE_TRANSIENT",
                "Discord did not confirm message delivery.",
                true,
                true));
        }
        catch (Exception)
        {
            return new(false, null, new(
                MessageDeliveryFailureKind.UncertainOutcome,
                "MESSAGE_DELIVERY_UNCERTAIN",
                "Discord did not confirm message delivery. The application will not repeat it automatically.",
                false,
                true));
        }
    }

    private static async Task<ChannelWriteOutcome> ExecuteWriteAsync(
        Func<Task<ulong?>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            var resourceId = await operation().ConfigureAwait(false);
            return new ChannelWriteOutcome(
                true,
                resourceId,
                null,
                OperationOutcomeCertainty.KnownSucceeded);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ChannelWriteValidationException exception)
        {
            return KnownFailure(
                OperationFailureKind.Validation,
                exception.SafeCode,
                exception.SafeMessage,
                exception.GetType().Name,
                retryable: false);
        }
        catch (RateLimitedException exception)
        {
            return KnownFailure(
                OperationFailureKind.RateLimited,
                "RATE_LIMIT_WAIT_FAILED",
                "Discord.Net could not complete its rate-limit wait.",
                exception.GetType().Name,
                retryable: true);
        }
        catch (HttpException exception)
        {
            var status = (int)exception.HttpCode;
            return status switch
            {
                400 => KnownFailure(
                    OperationFailureKind.Validation,
                    "DISCORD_INVALID_REQUEST",
                    "Discord rejected one or more channel property values.",
                    exception.GetType().Name,
                    retryable: false),
                401 or 403 => KnownFailure(
                    OperationFailureKind.PermissionDenied,
                    "DISCORD_PERMISSION_DENIED",
                    "Discord rejected the request because the bot lacks permission.",
                    exception.GetType().Name,
                    retryable: false),
                404 => KnownFailure(
                    OperationFailureKind.TargetNotFound,
                    "DISCORD_TARGET_NOT_FOUND",
                    "Discord reports that the target no longer exists.",
                    exception.GetType().Name,
                    retryable: false),
                429 => KnownFailure(
                    OperationFailureKind.RateLimited,
                    "DISCORD_RATE_LIMITED",
                    "Discord.Net exhausted the allowed rate-limit wait.",
                    exception.GetType().Name,
                    retryable: true),
                >= 500 => UncertainFailure(
                    OperationFailureKind.Transport,
                    "DISCORD_SERVER_ERROR_UNCERTAIN",
                    "Discord returned a server error and the request outcome requires reconciliation.",
                    exception.GetType().Name,
                    retryable: true),
                _ => KnownFailure(
                    OperationFailureKind.DiscordRejected,
                    "DISCORD_REQUEST_REJECTED",
                    "Discord rejected the channel operation.",
                    exception.GetType().Name,
                    retryable: false)
            };
        }
        catch (TimeoutException exception)
        {
            return UncertainFailure(
                OperationFailureKind.UncertainOutcome,
                "REQUEST_TIMEOUT_UNCERTAIN",
                "The request timed out and its outcome requires reconciliation.",
                exception.GetType().Name,
                retryable: true);
        }
        catch (HttpRequestException exception)
        {
            return UncertainFailure(
                OperationFailureKind.Transport,
                "NETWORK_OUTCOME_UNCERTAIN",
                "A network interruption left the request outcome uncertain.",
                exception.GetType().Name,
                retryable: true);
        }
        catch (Exception exception)
        {
            return KnownFailure(
                OperationFailureKind.Internal,
                "CHANNEL_WRITE_FAILED",
                "The channel operation failed before a successful outcome was confirmed.",
                exception.GetType().Name,
                retryable: false);
        }
    }

    private static ChannelWriteOutcome KnownFailure(
        OperationFailureKind kind,
        string code,
        string message,
        string exceptionType,
        bool retryable) =>
        new(
            false,
            null,
            new OperationFailure(
                kind,
                code,
                message,
                exceptionType,
                retryable,
                OperationOutcomeCertainty.KnownFailed),
            OperationOutcomeCertainty.KnownFailed);

    private static ChannelWriteOutcome UncertainFailure(
        OperationFailureKind kind,
        string code,
        string message,
        string exceptionType,
        bool retryable) =>
        new(
            false,
            null,
            new OperationFailure(
                kind,
                code,
                message,
                exceptionType,
                retryable,
                OperationOutcomeCertainty.Uncertain),
            OperationOutcomeCertainty.Uncertain);

    private static readonly Action<ILogger, Guid, string, Exception?> GatewayDisconnectedLog =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Warning,
            new EventId(3101, nameof(GatewayDisconnectedLog)),
            "Gateway for bot {BotProfileId} disconnected with {ExceptionType}; reconnecting");

    private static readonly Action<ILogger, Guid, string, Exception?> ExplorerUpdateFailedLog =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Warning,
            new EventId(3102, nameof(ExplorerUpdateFailedLog)),
            "Explorer cache translation for bot {BotProfileId} failed with {ExceptionType}");

    private static readonly Action<ILogger, Guid, ulong, string, Exception?> MemberDownloadFailedLog =
        LoggerMessage.Define<Guid, ulong, string>(
            LogLevel.Warning,
            new EventId(3103, nameof(MemberDownloadFailedLog)),
            "Member download for bot {BotProfileId}, server {ServerId} failed with {ExceptionType}");

    private static readonly Action<ILogger, Guid, Exception?> PrivilegedIntentRejectedLog =
        LoggerMessage.Define<Guid>(
            LogLevel.Warning,
            new EventId(3104, nameof(PrivilegedIntentRejectedLog)),
            "Discord rejected GuildMembers for bot {BotProfileId} with close code 4014");

    private static readonly Action<ILogger, string, string, string, Exception?> DiscordCriticalLog =
        CreateDiscordLog(LogLevel.Critical, 3110, nameof(DiscordCriticalLog));
    private static readonly Action<ILogger, string, string, string, Exception?> DiscordErrorLog =
        CreateDiscordLog(LogLevel.Error, 3111, nameof(DiscordErrorLog));
    private static readonly Action<ILogger, string, string, string, Exception?> DiscordWarningLog =
        CreateDiscordLog(LogLevel.Warning, 3112, nameof(DiscordWarningLog));
    private static readonly Action<ILogger, string, string, string, Exception?> DiscordInformationLog =
        CreateDiscordLog(LogLevel.Information, 3113, nameof(DiscordInformationLog));
    private static readonly Action<ILogger, string, string, string, Exception?> DiscordDebugLog =
        CreateDiscordLog(LogLevel.Debug, 3114, nameof(DiscordDebugLog));
    private static readonly Action<ILogger, string, string, string, Exception?> DiscordTraceLog =
        CreateDiscordLog(LogLevel.Trace, 3115, nameof(DiscordTraceLog));

    private static Action<ILogger, string, string, string, Exception?> CreateDiscordLog(
        LogLevel level,
        int id,
        string name) =>
        LoggerMessage.Define<string, string, string>(
            level,
            new EventId(id, name),
            "Discord gateway event [{Source}] severity {Severity} ({ExceptionType})");

    private sealed record PendingMemberChange(
        ulong MemberId,
        MemberReadModel? Member,
        bool Removed);

    private sealed class ChannelWriteValidationException(
        string safeCode,
        string safeMessage) : Exception
    {
        public string SafeCode { get; } = safeCode;
        public string SafeMessage { get; } = safeMessage;
    }
}
