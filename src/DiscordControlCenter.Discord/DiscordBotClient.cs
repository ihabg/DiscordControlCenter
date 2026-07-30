using Discord;
using Discord.WebSocket;
using DiscordControlCenter.Application.Bots;
using DiscordControlCenter.Core.Bots;
using Microsoft.Extensions.Logging;

namespace DiscordControlCenter.Discord;

public sealed class DiscordBotClient : IDiscordBotClient
{
    private readonly Guid _botProfileId;
    private readonly DiscordSocketClient _client;
    private readonly ILogger<DiscordBotClient> _logger;
    private readonly object _snapshotLock = new();
    private TaskCompletionSource _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private BotConnectionSnapshot _snapshot;
    private int _manualStop;
    private int _disposed;

    public DiscordBotClient(Guid botProfileId, ILogger<DiscordBotClient> logger)
    {
        _botProfileId = botProfileId;
        _logger = logger;
        _snapshot = BotConnectionSnapshot.Disconnected(botProfileId);
        _client = new DiscordSocketClient(
            new DiscordSocketConfig
            {
                GatewayIntents = GatewayIntents.Guilds,
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
        _client.Log += HandleLogAsync;
    }

    public event EventHandler<BotConnectionSnapshot>? StatusChanged;

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
        Publish(Snapshot with { State = BotConnectionState.Disconnecting, ErrorMessage = null });
        await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        Publish(BotConnectionSnapshot.Disconnected(_botProfileId));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _manualStop, 1);
        _client.Ready -= HandleReadyAsync;
        _client.Connected -= HandleConnectedAsync;
        _client.Disconnected -= HandleDisconnectedAsync;
        _client.LatencyUpdated -= HandleLatencyUpdatedAsync;
        _client.GuildAvailable -= HandleGuildChangedAsync;
        _client.GuildUnavailable -= HandleGuildChangedAsync;
        _client.Log -= HandleLogAsync;
        await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
        _client.Dispose();
    }

    private Task HandleReadyAsync()
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
        _ready.TrySetResult();
        return Task.CompletedTask;
    }

    private Task HandleConnectedAsync()
    {
        if (Snapshot.State == BotConnectionState.Reconnecting)
        {
            Publish(Snapshot with { State = BotConnectionState.Connecting });
        }

        return Task.CompletedTask;
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
        _ = guild;
        Publish(Snapshot with { ServerCount = _client.Guilds.Count });
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
