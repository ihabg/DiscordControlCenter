using System.Collections.Concurrent;
using DiscordControlCenter.Application.Common;
using DiscordControlCenter.Application.Explorer;
using DiscordControlCenter.Application.Messaging;
using DiscordControlCenter.Application.Operations;
using DiscordControlCenter.Core.Bots;
using DiscordControlCenter.Core.Common;
using DiscordControlCenter.Core.Explorer;
using DiscordControlCenter.Core.Operations;
using DiscordControlCenter.Core.Messaging;
using DiscordControlCenter.Core.Security;
using Microsoft.Extensions.Logging;

namespace DiscordControlCenter.Application.Bots;

public sealed class BotConnectionManager(
    IBotProfileRepository repository,
    ITokenProtector tokenProtector,
    IDiscordBotClientFactory clientFactory,
    IPermissionResolutionService permissionService,
    ILogger<BotConnectionManager> logger) : IBotConnectionManager
    , IBotExplorerService
    , IDiscordChannelWriter
    , IDiscordMessageWriter
    , ILiveValidationMessageVerifier
{
    private readonly ConcurrentDictionary<Guid, BotRuntime> _runtimes = new();
    private readonly ConcurrentDictionary<Guid, BotConnectionSnapshot> _snapshots = new();
    private readonly ConcurrentDictionary<Guid, string> _profileNames = new();
    private int _initialized;
    private int _disposed;

    public event EventHandler<BotConnectionSnapshot>? StatusChanged;
    public event EventHandler<ExplorerCacheChanged>? CacheChanged;

    public IReadOnlyCollection<BotConnectionSnapshot> Snapshots => _snapshots.Values.ToArray();

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
        {
            return;
        }

        var profiles = await repository.GetAllAsync(cancellationToken).ConfigureAwait(false);
        foreach (var profile in profiles)
        {
            _profileNames[profile.Id] = profile.DisplayName;
            _snapshots.TryAdd(
                profile.Id,
                BotConnectionSnapshot.Disconnected(profile.Id) with
                {
                    FullMemberAccessEnabled = profile.EnableFullMemberAccess
                });
        }
    }

    public async Task<OperationResult> ConnectAsync(
        Guid botProfileId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var profile = await repository.GetAsync(botProfileId, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            return OperationResult.Failure("The bot profile no longer exists.");
        }

        _profileNames[profile.Id] = profile.DisplayName;
        var runtime = _runtimes.GetOrAdd(
            botProfileId,
            id =>
            {
                var client = clientFactory.Create(id, profile.EnableFullMemberAccess);
                var created = new BotRuntime(id, client);
                client.StatusChanged += OnClientStatusChanged;
                client.ExplorerChanged += OnClientExplorerChanged;
                return created;
            });

        await runtime.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (runtime.Client.Snapshot.State is BotConnectionState.Connected
                or BotConnectionState.Connecting
                or BotConnectionState.Reconnecting)
            {
                return OperationResult.Success();
            }

            try
            {
                var token = tokenProtector.Unprotect(profile.ProtectedToken);
                await runtime.Client.ConnectAsync(token, cancellationToken).ConfigureAwait(false);
                var connectedSnapshot = runtime.Client.Snapshot;
                if (connectedSnapshot.Identity is not null)
                {
                    profile = profile.WithIdentity(
                        connectedSnapshot.Identity,
                        connectedSnapshot.LastConnectedAt ?? DateTimeOffset.UtcNow);
                    await repository.UpdateAsync(profile, cancellationToken).ConfigureAwait(false);
                }

                return OperationResult.Success();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                ConnectFailedLog(logger, botProfileId, exception.GetType().Name, null);
                var failed = runtime.Client.Snapshot with
                {
                    State = BotConnectionState.Faulted,
                    ErrorMessage = SafeError.FromException(exception)
                };
                var cacheSnapshot = runtime.Cache.MarkFaulted(failed.ErrorMessage);
                CacheChanged?.Invoke(
                    this,
                    new ExplorerCacheChanged(
                        botProfileId,
                        ExplorerCacheUpdateKind.Faulted,
                        null,
                        cacheSnapshot));
                Publish(failed);
                return OperationResult.Failure(failed.ErrorMessage);
            }
        }
        finally
        {
            runtime.Gate.Release();
        }
    }

    public async Task<OperationResult> DisconnectAsync(
        Guid botProfileId,
        CancellationToken cancellationToken)
    {
        if (!_runtimes.TryRemove(botProfileId, out var runtime))
        {
            var existing = _snapshots.GetValueOrDefault(
                botProfileId,
                BotConnectionSnapshot.Disconnected(botProfileId));
            Publish(existing with
            {
                State = BotConnectionState.Disconnected,
                GatewayLatencyMilliseconds = null,
                ServerCount = 0
            });
            return OperationResult.Success();
        }

        try
        {
            await runtime.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _runtimes.TryAdd(botProfileId, runtime);
            throw;
        }

        try
        {
            runtime.Client.StatusChanged -= OnClientStatusChanged;
            runtime.Client.ExplorerChanged -= OnClientExplorerChanged;
            await runtime.Client.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            await runtime.Client.DisposeAsync().ConfigureAwait(false);
            var cacheSnapshot = runtime.Cache.MarkDisconnected();
            CacheChanged?.Invoke(
                this,
                new ExplorerCacheChanged(
                    botProfileId,
                    ExplorerCacheUpdateKind.Cleared,
                    null,
                    cacheSnapshot));
            Publish(runtime.Client.Snapshot);
            return OperationResult.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            DisconnectFailedLog(logger, botProfileId, exception.GetType().Name, null);
            return OperationResult.Failure(SafeError.FromException(exception));
        }
        finally
        {
            runtime.Gate.Release();
            runtime.Gate.Dispose();
        }
    }

    public async Task ConnectAllAsync(CancellationToken cancellationToken)
    {
        var profiles = await repository.GetAllAsync(cancellationToken).ConfigureAwait(false);
        using var concurrency = new SemaphoreSlim(3, 3);
        var tasks = profiles
            .Where(profile => profile.IsEnabled)
            .Select(async profile =>
            {
                await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await ConnectAsync(profile.Id, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    concurrency.Release();
                }
            });
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public Task DisconnectAllAsync(CancellationToken cancellationToken)
    {
        var ids = _runtimes.Keys.ToArray();
        return Task.WhenAll(ids.Select(id => DisconnectAsync(id, cancellationToken)));
    }

    public BotExplorerSnapshot GetSnapshot(Guid botProfileId) =>
        _runtimes.TryGetValue(botProfileId, out var runtime)
            ? runtime.Cache.Snapshot
            : BotExplorerSnapshot.Disconnected(botProfileId);

    public async Task<OperationResult> RefreshAsync(
        Guid botProfileId,
        CancellationToken cancellationToken)
    {
        if (!_runtimes.TryGetValue(botProfileId, out var runtime)
            || runtime.Client.Snapshot.State != BotConnectionState.Connected)
        {
            return OperationResult.Failure("Connect the selected bot before refreshing servers.");
        }

        var previousState = runtime.Cache.Snapshot.State;
        var loading = runtime.Cache.MarkLoading();
        CacheChanged?.Invoke(
            this,
            new ExplorerCacheChanged(
                botProfileId,
                ExplorerCacheUpdateKind.Reset,
                null,
                loading));
        try
        {
            var update = await runtime.Client
                .RefreshExplorerAsync(cancellationToken)
                .ConfigureAwait(false);
            ApplyExplorerUpdate(runtime, update);
            return OperationResult.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var snapshot = runtime.Cache.CancelLoading(previousState);
            CacheChanged?.Invoke(
                this,
                new ExplorerCacheChanged(
                    botProfileId,
                    ExplorerCacheUpdateKind.Reset,
                    null,
                    snapshot));
            throw;
        }
        catch (Exception exception)
        {
            RefreshFailedLog(logger, botProfileId, exception.GetType().Name, null);
            var snapshot = runtime.Cache.MarkFaulted(
                "Server and channel information could not be refreshed.");
            CacheChanged?.Invoke(
                this,
                new ExplorerCacheChanged(
                    botProfileId,
                    ExplorerCacheUpdateKind.Faulted,
                    null,
                    snapshot));
            return OperationResult.Failure(snapshot.ErrorMessage!);
        }
    }

    public async Task<OperationResult> LoadMembersAsync(
        Guid botProfileId,
        ulong serverId,
        CancellationToken cancellationToken)
    {
        if (!_runtimes.TryGetValue(botProfileId, out var runtime)
            || runtime.Client.Snapshot.State != BotConnectionState.Connected)
        {
            return OperationResult.Failure("Connect the selected bot before loading members.");
        }

        if (!runtime.Client.Snapshot.FullMemberAccessEnabled)
        {
            return OperationResult.Failure(
                "Full member access is disabled for this bot profile.");
        }

        try
        {
            await runtime.Client.LoadMembersAsync(serverId, cancellationToken).ConfigureAwait(false);
            return OperationResult.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            MemberLoadFailedLog(logger, botProfileId, serverId, exception.GetType().Name, null);
            return OperationResult.Failure(
                "Members could not be loaded. Confirm the Developer Portal Server Members Intent toggle and retry.");
        }
    }

    public IReadOnlyList<BotDiagnosticsReadModel> GetDiagnostics()
    {
        var now = DateTimeOffset.UtcNow;
        return _snapshots.Values
            .OrderBy(snapshot => _profileNames.GetValueOrDefault(snapshot.BotProfileId), StringComparer.OrdinalIgnoreCase)
            .Select(
                connection =>
                {
                    var cache = GetSnapshot(connection.BotProfileId);
                    var members = cache.Servers.Select(server => server.Members).ToArray();
                    var completeness = AggregateCompleteness(
                        members.Select(member => member.Completeness),
                        connection.FullMemberAccessEnabled);
                    return new BotDiagnosticsReadModel(
                        connection.BotProfileId,
                        _profileNames.GetValueOrDefault(connection.BotProfileId, "Saved bot"),
                        connection.State.ToString(),
                        connection.GatewayLatencyMilliseconds,
                        connection.LastReadyAt,
                        connection.LastDisconnectedAt,
                        connection.LastReconnectedAt,
                        cache.Servers.Length,
                        cache.Servers.Sum(server => server.Channels.Length),
                        cache.Servers.Sum(server => server.Roles.Length),
                        members.Sum(member => member.LoadedMemberCount),
                        completeness,
                        cache.LastAcceptedSequence,
                        cache.LastSuccessfulRefreshAt,
                        cache.IsRefreshPending,
                        connection.RecentGatewayError,
                        connection.FullMemberAccessEnabled,
                        connection.State == BotConnectionState.Connected
                            && connection.FullMemberAccessEnabled,
                        connection.VoiceStateEventCount,
                        connection.LastVoiceStateEventAt,
                        cache.RefreshedAt is DateTimeOffset refreshedAt
                            ? now - refreshedAt
                            : null);
                })
            .ToArray();
    }

    public Task<ChannelWriteOutcome> CreateCategoryAsync(
        Guid botProfileId,
        ulong serverId,
        ChannelOperationStateSnapshot after,
        string? auditReason,
        CancellationToken cancellationToken) =>
        ExecuteWriteAsync(
            botProfileId,
            client => client.CreateCategoryAsync(
                serverId,
                after,
                auditReason,
                cancellationToken),
            cancellationToken);

    public Task<ChannelWriteOutcome> CreateTextChannelAsync(
        Guid botProfileId,
        ulong serverId,
        ChannelOperationStateSnapshot after,
        string? auditReason,
        CancellationToken cancellationToken) =>
        ExecuteWriteAsync(
            botProfileId,
            client => client.CreateTextChannelAsync(
                serverId,
                after,
                auditReason,
                cancellationToken),
            cancellationToken);

    public Task<ChannelWriteOutcome> CreateVoiceChannelAsync(
        Guid botProfileId,
        ulong serverId,
        ChannelOperationStateSnapshot after,
        string? auditReason,
        CancellationToken cancellationToken) =>
        ExecuteWriteAsync(
            botProfileId,
            client => client.CreateVoiceChannelAsync(
                serverId,
                after,
                auditReason,
                cancellationToken),
            cancellationToken);

    public Task<ChannelWriteOutcome> ModifyChannelAsync(
        Guid botProfileId,
        ulong serverId,
        ulong channelId,
        ChannelOperationStateSnapshot before,
        ChannelOperationStateSnapshot after,
        string? auditReason,
        CancellationToken cancellationToken) =>
        ExecuteWriteAsync(
            botProfileId,
            client => client.ModifyChannelAsync(
                serverId,
                channelId,
                before,
                after,
                auditReason,
                cancellationToken),
            cancellationToken);

    public Task<ChannelWriteOutcome> ReorderChannelsAsync(
        Guid botProfileId,
        ulong serverId,
        IReadOnlyList<ChannelPositionUpdate> positions,
        string? auditReason,
        CancellationToken cancellationToken) =>
        ExecuteWriteAsync(
            botProfileId,
            client => client.ReorderChannelsAsync(
                serverId,
                positions,
                auditReason,
                cancellationToken),
            cancellationToken);

    public Task<ChannelWriteOutcome> SetPermissionOverwriteAsync(
        Guid botProfileId,
        ulong serverId,
        ulong channelId,
        ChannelPermissionOverwriteSnapshot overwrite,
        string? auditReason,
        CancellationToken cancellationToken) =>
        ExecuteWriteAsync(
            botProfileId,
            client => client.SetPermissionOverwriteAsync(
                serverId,
                channelId,
                overwrite,
                auditReason,
                cancellationToken),
            cancellationToken);

    public Task<ChannelWriteOutcome> DeletePermissionOverwriteAsync(
        Guid botProfileId,
        ulong serverId,
        ulong channelId,
        ulong targetId,
        PermissionTargetKind targetType,
        string? auditReason,
        CancellationToken cancellationToken) =>
        ExecuteWriteAsync(
            botProfileId,
            client => client.DeletePermissionOverwriteAsync(
                serverId,
                channelId,
                targetId,
                targetType,
                auditReason,
                cancellationToken),
            cancellationToken);

    public Task<ChannelWriteOutcome> DeleteChannelAsync(
        Guid botProfileId,
        ulong serverId,
        ulong channelId,
        string? auditReason,
        CancellationToken cancellationToken) =>
        ExecuteWriteAsync(
            botProfileId,
            client => client.DeleteChannelAsync(
                serverId,
                channelId,
                auditReason,
                cancellationToken),
            cancellationToken);

    public Task<MessageWriteOutcome> SendChannelMessageAsync(
        MessageOperationPlan plan,
        CancellationToken cancellationToken) =>
        ExecuteMessageWriteAsync(
            plan.BotProfileId,
            client => client.SendChannelMessageAsync(plan, cancellationToken),
            cancellationToken);

    public Task<MessageWriteOutcome> SendDirectMessageAsync(
        MessageOperationPlan plan,
        CancellationToken cancellationToken) =>
        ExecuteMessageWriteAsync(
            plan.BotProfileId,
            client => client.SendDirectMessageAsync(plan, cancellationToken),
            cancellationToken);

    public async Task<bool> MessageExistsAsync(Guid botProfileId, ulong channelId, ulong messageId, CancellationToken cancellationToken)
    {
        if (!_runtimes.TryGetValue(botProfileId, out var runtime)) return false;
        await runtime.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return runtime.Client.Snapshot.State == BotConnectionState.Connected && await runtime.Client.MessageExistsAsync(channelId, messageId, cancellationToken).ConfigureAwait(false); }
        finally { runtime.Gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await DisconnectAllAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ShutdownTimeoutLog(logger, null);
        }
    }

    private void OnClientStatusChanged(object? sender, BotConnectionSnapshot snapshot) => Publish(snapshot);

    private void OnClientExplorerChanged(object? sender, ExplorerCacheUpdate update)
    {
        _ = sender;
        if (_runtimes.TryGetValue(update.BotProfileId, out var runtime))
        {
            ApplyExplorerUpdate(runtime, update);
        }
    }

    private void ApplyExplorerUpdate(BotRuntime runtime, ExplorerCacheUpdate update)
    {
        var snapshot = runtime.Cache.Apply(update);
        permissionService.Invalidate(update.BotProfileId, update.ServerId);
        CacheChanged?.Invoke(
            this,
            new ExplorerCacheChanged(
                update.BotProfileId,
                update.Kind,
                update.ServerId,
                snapshot));
    }

    private void Publish(BotConnectionSnapshot snapshot)
    {
        _snapshots[snapshot.BotProfileId] = snapshot;
        StatusChanged?.Invoke(this, snapshot);
    }

    private async Task<ChannelWriteOutcome> ExecuteWriteAsync(
        Guid botProfileId,
        Func<IDiscordBotClient, Task<ChannelWriteOutcome>> operation,
        CancellationToken cancellationToken)
    {
        if (!_runtimes.TryGetValue(botProfileId, out var runtime))
        {
            return WriteUnavailable("BOT_DISCONNECTED", "The selected bot is not connected.");
        }

        await runtime.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (runtime.Client.Snapshot.State != BotConnectionState.Connected)
            {
                return WriteUnavailable("BOT_DISCONNECTED", "The selected bot is not connected.");
            }

            return await operation(runtime.Client).ConfigureAwait(false);
        }
        finally
        {
            runtime.Gate.Release();
        }
    }

    private async Task<MessageWriteOutcome> ExecuteMessageWriteAsync(
        Guid botProfileId,
        Func<IDiscordBotClient, Task<MessageWriteOutcome>> operation,
        CancellationToken cancellationToken)
    {
        if (!_runtimes.TryGetValue(botProfileId, out var runtime))
        {
            return MessageWriteUnavailable();
        }

        await runtime.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (runtime.Client.Snapshot.State != BotConnectionState.Connected)
            {
                return MessageWriteUnavailable();
            }

            return await operation(runtime.Client).ConfigureAwait(false);
        }
        finally
        {
            runtime.Gate.Release();
        }
    }

    private static ChannelWriteOutcome WriteUnavailable(string code, string message) =>
        new(
            false,
            null,
            new OperationFailure(
                OperationFailureKind.Validation,
                code,
                message,
                null,
                false,
                OperationOutcomeCertainty.KnownFailed),
            OperationOutcomeCertainty.KnownFailed);

    private static MessageWriteOutcome MessageWriteUnavailable() =>
        new(
            false,
            null,
            new(
                MessageDeliveryFailureKind.BotDisconnected,
                "BOT_DISCONNECTED",
                "The selected bot is not connected.",
                false,
                false));

    private static DataCompleteness AggregateCompleteness(
        IEnumerable<DataCompleteness> values,
        bool fullMemberAccessEnabled)
    {
        var states = values.ToArray();
        if (states.Length == 0)
        {
            return fullMemberAccessEnabled
                ? DataCompleteness.Unavailable
                : DataCompleteness.Limited;
        }

        if (states.Any(state => state == DataCompleteness.Failed))
        {
            return DataCompleteness.Failed;
        }

        if (states.Any(state => state == DataCompleteness.Loading))
        {
            return DataCompleteness.Loading;
        }

        if (states.All(state => state == DataCompleteness.Complete))
        {
            return DataCompleteness.Complete;
        }

        return fullMemberAccessEnabled
            ? DataCompleteness.Partial
            : DataCompleteness.Limited;
    }

    private sealed class BotRuntime(Guid botProfileId, IDiscordBotClient client)
    {
        public IDiscordBotClient Client { get; } = client;
        public BotExplorerCache Cache { get; } = new(botProfileId);
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    private static readonly Action<ILogger, Guid, string, Exception?> ConnectFailedLog =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Warning,
            new EventId(2101, nameof(ConnectFailedLog)),
            "Bot {BotProfileId} connect failed with {ExceptionType}");

    private static readonly Action<ILogger, Guid, string, Exception?> DisconnectFailedLog =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Warning,
            new EventId(2102, nameof(DisconnectFailedLog)),
            "Bot {BotProfileId} disconnect failed with {ExceptionType}");

    private static readonly Action<ILogger, Exception?> ShutdownTimeoutLog =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(2103, nameof(ShutdownTimeoutLog)),
            "Timed out while disconnecting bot clients during shutdown");

    private static readonly Action<ILogger, Guid, string, Exception?> RefreshFailedLog =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Warning,
            new EventId(2104, nameof(RefreshFailedLog)),
            "Explorer refresh for bot {BotProfileId} failed with {ExceptionType}");

    private static readonly Action<ILogger, Guid, ulong, string, Exception?> MemberLoadFailedLog =
        LoggerMessage.Define<Guid, ulong, string>(
            LogLevel.Warning,
            new EventId(2105, nameof(MemberLoadFailedLog)),
            "Member load for bot {BotProfileId}, server {ServerId} failed with {ExceptionType}");
}
