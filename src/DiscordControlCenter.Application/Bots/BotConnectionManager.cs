using System.Collections.Concurrent;
using DiscordControlCenter.Application.Common;
using DiscordControlCenter.Core.Bots;
using DiscordControlCenter.Core.Common;
using DiscordControlCenter.Core.Security;
using Microsoft.Extensions.Logging;

namespace DiscordControlCenter.Application.Bots;

public sealed class BotConnectionManager(
    IBotProfileRepository repository,
    ITokenProtector tokenProtector,
    IDiscordBotClientFactory clientFactory,
    ILogger<BotConnectionManager> logger) : IBotConnectionManager
{
    private readonly ConcurrentDictionary<Guid, BotRuntime> _runtimes = new();
    private readonly ConcurrentDictionary<Guid, BotConnectionSnapshot> _snapshots = new();
    private int _initialized;
    private int _disposed;

    public event EventHandler<BotConnectionSnapshot>? StatusChanged;

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
            _snapshots.TryAdd(profile.Id, BotConnectionSnapshot.Disconnected(profile.Id));
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

        var runtime = _runtimes.GetOrAdd(
            botProfileId,
            id =>
            {
                var client = clientFactory.Create(id);
                var created = new BotRuntime(client);
                client.StatusChanged += OnClientStatusChanged;
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
            Publish(BotConnectionSnapshot.Disconnected(botProfileId));
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
            await runtime.Client.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            await runtime.Client.DisposeAsync().ConfigureAwait(false);
            Publish(BotConnectionSnapshot.Disconnected(botProfileId));
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

    private void Publish(BotConnectionSnapshot snapshot)
    {
        _snapshots[snapshot.BotProfileId] = snapshot;
        StatusChanged?.Invoke(this, snapshot);
    }

    private sealed class BotRuntime(IDiscordBotClient client)
    {
        public IDiscordBotClient Client { get; } = client;
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
}
