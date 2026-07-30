namespace DiscordControlCenter.Application.Common;

internal static class SafeError
{
    public static string FromException(Exception exception) =>
        exception switch
        {
            OperationCanceledException => "The operation was canceled.",
            BotAuthenticationException => exception.Message,
            PrivilegedIntentException => exception.Message,
            TimeoutException => "Discord did not respond in time.",
            _ => "The operation failed. Review the application log for diagnostic details."
        };
}

public sealed class PrivilegedIntentException : Exception
{
    public PrivilegedIntentException(string message)
        : base(message)
    {
    }
}

public sealed class BotAuthenticationException : Exception
{
    public BotAuthenticationException(string message)
        : base(message)
    {
    }

    public BotAuthenticationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
