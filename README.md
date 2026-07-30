# Discord Control Center

A Windows WPF control center for multiple official Discord bot accounts. The first
milestone provides secure bot profile storage, independent gateway connections,
live connection health, and the application foundation for later server-management
features.

## Requirements

- Windows 10 or later
- .NET 10 SDK
- Discord bot tokens created in the Discord Developer Portal

## Run

```powershell
dotnet restore DiscordControlCenter.slnx --configfile NuGet.Config
dotnet run --project src/DiscordControlCenter.App/DiscordControlCenter.App.csproj --no-restore
```

Application data is stored under `%LOCALAPPDATA%\DiscordControlCenter`. Bot tokens
are encrypted for the current Windows user with DPAPI and never written in plaintext.
