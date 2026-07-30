using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using DiscordControlCenter.Application.Bots;
using DiscordControlCenter.Application.Explorer;
using DiscordControlCenter.Core.Bots;
using Microsoft.Extensions.Logging;

namespace DiscordControlCenter.Discord;

public sealed class DiscordBotClient : IDiscordBotClient
{
    private readonly Guid _botProfileId;
    private readonly DiscordSocketClient _client;
    private readonly ILogger<DiscordBotClient> _logger;
    private readonly object _snapshotLock = new();
    private readonly object _explorerBuildLock = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly ConcurrentDictionary<ulong, CancellationTokenSource> _voiceUpdateDebounce = new();
    private TaskCompletionSource _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private BotConnectionSnapshot _snapshot;
    private int _manualStop;
    private int _disposed;
    private long _explorerSequence;

    public DiscordBotClient(Guid botProfileId, ILogger<DiscordBotClient> logger)
    {
        _botProfileId = botProfileId;
        _logger = logger;
        _snapshot = BotConnectionSnapshot.Disconnected(botProfileId);
        _client = new DiscordSocketClient(
            new DiscordSocketConfig
            {
                GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates,
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
        Publish(BotConnectionSnapshot.Disconnected(_botProfileId));
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
                ErrorMessage = null
            });
        }
    }

    private Task HandleDisconnectedAsync(Exception exception)
    {
        var isManual = Volatile.Read(ref _manualStop) != 0;
        Publish(Snapshot with
        {
            State = isManual
                ? BotConnectionState.Disconnected
                : BotConnectionState.Reconnecting,
            GatewayLatencyMilliseconds = null,
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

    private Task HandleVoiceStateUpdatedAsync(
        SocketUser user,
        SocketVoiceState before,
        SocketVoiceState after)
    {
        _ = user;
        var guildId = after.VoiceChannel?.Guild.Id ?? before.VoiceChannel?.Guild.Id;
        if (guildId is ulong id)
        {
            QueueVoiceUpdate(id);
        }

        return Task.CompletedTask;
    }

    private Task HandleLogAsync(LogMessage message)
    {
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
        write(_logger, message.Source, message.Message ?? string.Empty, exceptionType, null);
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
                .Select(guild => DiscordExplorerTranslator.TranslateServer(guild, now))
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
                var server = DiscordExplorerTranslator.TranslateServer(guild, now);
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

    private void QueueVoiceUpdate(ulong guildId)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        _voiceUpdateDebounce.AddOrUpdate(
            guildId,
            cancellation,
            (_, existing) =>
            {
                existing.Cancel();
                existing.Dispose();
                return cancellation;
            });
        _ = PublishDebouncedVoiceUpdateAsync(guildId, cancellation);
    }

    private async Task PublishDebouncedVoiceUpdateAsync(
        ulong guildId,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellation.Token)
                .ConfigureAwait(false);
            var guild = _client.GetGuild(guildId);
            if (guild is not null)
            {
                PublishServer(guild);
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
            "Discord gateway [{Source}] {Message} ({ExceptionType})");
}
