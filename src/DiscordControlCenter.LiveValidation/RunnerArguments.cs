namespace DiscordControlCenter.LiveValidation;

public sealed record RunnerArguments(string ServerName)
{
    public const string RequiredServerName = "teast";
    public const string RequiredConfirmation = "VALIDATE TEAST";

    public static bool TryParse(string[] args, out RunnerArguments? result, out string error)
    {
        result = null;
        error = "";
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                error = "Live validation requires named arguments.";
                return false;
            }

            values[args[index]] = args[index + 1];
        }

        if (!values.TryGetValue("--server-name", out var serverName) || serverName != RequiredServerName)
        {
            error = "Live validation is restricted to the exact server name teast.";
            return false;
        }

        if (!values.TryGetValue("--confirm", out var confirmation) || confirmation != RequiredConfirmation)
        {
            error = "The exact --confirm value VALIDATE TEAST is required.";
            return false;
        }

        if (!values.TryGetValue("--allow-discord-write", out var writeFlag) || writeFlag != "true")
        {
            error = "The explicit --allow-discord-write true flag is required.";
            return false;
        }

        if (values.Keys.Any(key => key.Contains("soundpad", StringComparison.OrdinalIgnoreCase))
            || values.Values.Any(value => value.Contains("soundpad", StringComparison.OrdinalIgnoreCase)))
        {
            error = "Unrelated application paths and process names are rejected.";
            return false;
        }

        result = new RunnerArguments(serverName);
        return true;
    }
}
