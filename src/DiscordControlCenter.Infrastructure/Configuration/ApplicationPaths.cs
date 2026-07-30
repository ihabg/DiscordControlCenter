namespace DiscordControlCenter.Infrastructure.Configuration;

public sealed record ApplicationPaths(string DataDirectory)
{
    public string DatabasePath => Path.Combine(DataDirectory, "control-center.db");
    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    public static ApplicationPaths ForCurrentUser()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new ApplicationPaths(Path.Combine(localData, "DiscordControlCenter"));
    }
}
