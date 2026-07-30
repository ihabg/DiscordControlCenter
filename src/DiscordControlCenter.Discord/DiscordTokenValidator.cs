using System.Net;
using Discord;
using Discord.Net;
using Discord.Rest;
using DiscordControlCenter.Application.Bots;
using DiscordControlCenter.Application.Common;
using DiscordControlCenter.Core.Bots;
using Microsoft.Extensions.Logging;

namespace DiscordControlCenter.Discord;

public sealed class DiscordTokenValidator(ILogger<DiscordTokenValidator> logger)
    : IDiscordTokenValidator
{
    public async Task<BotIdentity> ValidateAsync(
        string token,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        using var client = new DiscordRestClient();
        try
        {
            await client.LoginAsync(TokenType.Bot, token)
                .WaitAsync(TimeSpan.FromSeconds(20), cancellationToken)
                .ConfigureAwait(false);
            var user = await client.GetCurrentUserAsync()
                .WaitAsync(TimeSpan.FromSeconds(20), cancellationToken)
                .ConfigureAwait(false);
            return new BotIdentity(
                user.Id,
                user.Username,
                user.GetAvatarUrl(ImageFormat.Auto, 128) ?? user.GetDefaultAvatarUrl());
        }
        catch (HttpException exception)
            when (exception.HttpCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new BotAuthenticationException(
                "Discord rejected the bot token. Verify the token in the Developer Portal.",
                exception);
        }
        catch (TimeoutException exception)
        {
            throw new BotAuthenticationException(
                "Discord did not respond while validating the bot token.",
                exception);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ValidationFailedLog(logger, exception.GetType().Name, null);
            throw new BotAuthenticationException(
                "The bot token could not be validated with Discord.",
                exception);
        }
    }

    private static readonly Action<ILogger, string, Exception?> ValidationFailedLog =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(3001, nameof(ValidationFailedLog)),
            "Discord token validation failed with {ExceptionType}");
}
