using System.Diagnostics;
using DiscordControlCenter.Application.Common;
using DiscordControlCenter.Core.Auditing;
using DiscordControlCenter.Core.Bots;
using DiscordControlCenter.Core.Common;
using DiscordControlCenter.Core.Security;
using Microsoft.Extensions.Logging;

namespace DiscordControlCenter.Application.Bots;

public sealed class BotProfileService(
    IBotProfileRepository repository,
    ITokenProtector tokenProtector,
    IDiscordTokenValidator tokenValidator,
    IBotConnectionManager connectionManager,
    IAuditRepository auditRepository,
    IClock clock,
    ILogger<BotProfileService> logger) : IBotProfileService
{
    public Task<IReadOnlyList<BotProfile>> GetAllAsync(CancellationToken cancellationToken) =>
        repository.GetAllAsync(cancellationToken);

    public async Task<OperationResult<BotProfile>> AddAsync(
        AddBotRequest request,
        CancellationToken cancellationToken)
    {
        var validationError = Validate(request);
        if (validationError is not null)
        {
            return OperationResult.Failure<BotProfile>(validationError);
        }

        var correlationId = Guid.NewGuid();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var trimmedName = request.DisplayName.Trim();
            var token = request.Token.Trim();
            var identity = await tokenValidator.ValidateAsync(token, cancellationToken).ConfigureAwait(false);
            var now = clock.UtcNow;
            var profile = new BotProfile(
                Guid.NewGuid(),
                trimmedName,
                tokenProtector.Protect(token),
                tokenProtector.CreateFingerprint(token),
                true,
                now,
                identity.UserId,
                identity.Username,
                identity.AvatarUrl);

            await repository.AddAsync(profile, cancellationToken).ConfigureAwait(false);
            await WriteAuditAsync(
                profile.Id,
                "BotProfile.Add",
                trimmedName,
                "Succeeded",
                "Bot profile validated and saved.",
                null,
                stopwatch.ElapsedMilliseconds,
                correlationId,
                cancellationToken).ConfigureAwait(false);

            BotAddedLog(logger, profile.Id, identity.UserId, null);
            return OperationResult.Success(profile);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var safeError = SafeError.FromException(exception);
            BotAddFailedLog(logger, correlationId, exception.GetType().Name, null);
            await WriteAuditAsync(
                null,
                "BotProfile.Add",
                request.DisplayName.Trim(),
                "Failed",
                "Bot profile could not be added.",
                safeError,
                stopwatch.ElapsedMilliseconds,
                correlationId,
                cancellationToken).ConfigureAwait(false);
            return OperationResult.Failure<BotProfile>(safeError);
        }
    }

    public async Task<OperationResult> RemoveAsync(Guid botProfileId, CancellationToken cancellationToken)
    {
        var profile = await repository.GetAsync(botProfileId, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            return OperationResult.Failure("The bot profile no longer exists.");
        }

        var correlationId = Guid.NewGuid();
        var stopwatch = Stopwatch.StartNew();
        var disconnectResult = await connectionManager
            .DisconnectAsync(botProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (!disconnectResult.IsSuccess)
        {
            return disconnectResult;
        }

        await repository.RemoveAsync(botProfileId, cancellationToken).ConfigureAwait(false);
        await WriteAuditAsync(
            botProfileId,
            "BotProfile.Remove",
            profile.DisplayName,
            "Succeeded",
            "Bot profile and protected credential were removed.",
            null,
            stopwatch.ElapsedMilliseconds,
            correlationId,
            cancellationToken).ConfigureAwait(false);
        BotRemovedLog(logger, botProfileId, null);
        return OperationResult.Success();
    }

    public async Task<OperationResult<BotProfile>> ReplaceTokenAsync(
        Guid botProfileId,
        string newToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(newToken) || newToken.Any(char.IsWhiteSpace))
        {
            return OperationResult.Failure<BotProfile>(
                "A bot token without whitespace is required.");
        }

        var profile = await repository.GetAsync(botProfileId, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            return OperationResult.Failure<BotProfile>("The bot profile no longer exists.");
        }

        var activeSnapshot = connectionManager.Snapshots.FirstOrDefault(
            snapshot => snapshot.BotProfileId == botProfileId);
        if (activeSnapshot is not null
            && activeSnapshot.State is not BotConnectionState.Disconnected and not BotConnectionState.Faulted)
        {
            return OperationResult.Failure<BotProfile>(
                "Disconnect the bot before replacing its token.");
        }

        var correlationId = Guid.NewGuid();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var token = newToken.Trim();
            var identity = await tokenValidator.ValidateAsync(token, cancellationToken).ConfigureAwait(false);
            if (profile.DiscordUserId is ulong expectedUserId && expectedUserId != identity.UserId)
            {
                return OperationResult.Failure<BotProfile>(
                    "The new token belongs to a different Discord bot. Add it as a separate profile.");
            }

            var updated = profile with
            {
                ProtectedToken = tokenProtector.Protect(token),
                TokenFingerprint = tokenProtector.CreateFingerprint(token),
                DiscordUserId = identity.UserId,
                DiscordUsername = identity.Username,
                AvatarUrl = identity.AvatarUrl
            };
            await repository.UpdateAsync(updated, cancellationToken).ConfigureAwait(false);
            await WriteAuditAsync(
                botProfileId,
                "BotProfile.ReplaceToken",
                profile.DisplayName,
                "Succeeded",
                "Bot credential was revalidated and replaced.",
                null,
                stopwatch.ElapsedMilliseconds,
                correlationId,
                cancellationToken).ConfigureAwait(false);
            TokenReplacedLog(logger, botProfileId, null);
            return OperationResult.Success(updated);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var safeError = SafeError.FromException(exception);
            TokenReplaceFailedLog(logger, botProfileId, exception.GetType().Name, null);
            await WriteAuditAsync(
                botProfileId,
                "BotProfile.ReplaceToken",
                profile.DisplayName,
                "Failed",
                "Bot credential could not be replaced.",
                safeError,
                stopwatch.ElapsedMilliseconds,
                correlationId,
                cancellationToken).ConfigureAwait(false);
            return OperationResult.Failure<BotProfile>(safeError);
        }
    }

    private static string? Validate(AddBotRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return "Display name is required.";
        }

        if (request.DisplayName.Trim().Length > 80)
        {
            return "Display name must be 80 characters or fewer.";
        }

        if (string.IsNullOrWhiteSpace(request.Token))
        {
            return "Bot token is required.";
        }

        if (request.Token.Any(char.IsWhiteSpace))
        {
            return "Bot token cannot contain whitespace.";
        }

        return null;
    }

    private Task WriteAuditAsync(
        Guid? botProfileId,
        string action,
        string target,
        string status,
        string description,
        string? error,
        long duration,
        Guid correlationId,
        CancellationToken cancellationToken) =>
        auditRepository.AddAsync(
            new AuditEntry(
                Guid.NewGuid(),
                clock.UtcNow,
                botProfileId,
                action,
                target,
                status,
                description,
                error,
                duration,
                correlationId),
            cancellationToken);

    private static readonly Action<ILogger, Guid, ulong, Exception?> BotAddedLog =
        LoggerMessage.Define<Guid, ulong>(
            LogLevel.Information,
            new EventId(2001, nameof(BotAddedLog)),
            "Bot profile {BotProfileId} was added for Discord user {DiscordUserId}");

    private static readonly Action<ILogger, Guid, string, Exception?> BotAddFailedLog =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Warning,
            new EventId(2002, nameof(BotAddFailedLog)),
            "Bot profile add failed. CorrelationId {CorrelationId}, exception type {ExceptionType}");

    private static readonly Action<ILogger, Guid, Exception?> BotRemovedLog =
        LoggerMessage.Define<Guid>(
            LogLevel.Information,
            new EventId(2003, nameof(BotRemovedLog)),
            "Bot profile {BotProfileId} was removed");

    private static readonly Action<ILogger, Guid, Exception?> TokenReplacedLog =
        LoggerMessage.Define<Guid>(
            LogLevel.Information,
            new EventId(2004, nameof(TokenReplacedLog)),
            "Protected credential for bot profile {BotProfileId} was replaced");

    private static readonly Action<ILogger, Guid, string, Exception?> TokenReplaceFailedLog =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Warning,
            new EventId(2005, nameof(TokenReplaceFailedLog)),
            "Credential replacement for bot {BotProfileId} failed with {ExceptionType}");
}
