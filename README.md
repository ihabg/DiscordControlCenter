# Discord Control Center

A Windows WPF control center for multiple official Discord bot accounts. The current
read-only milestone provides secure bot profile storage, isolated gateway connections,
live connection health, and Server and Channel Explorers.

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

## Read-only explorer

Choose a saved bot in the toolbar. The application never connects it automatically;
use Bot Manager to connect it explicitly.

- **Server Explorer** lists every cached server for the connected bot, searches by
  name or ID, and shows server metadata, counts, bot role information, refresh time,
  availability, and effective server permissions.
- **Channel Explorer** displays categories and channels in Discord position order,
  keeps category context while filtering by name or ID, and shows type-specific
  metadata, overwrite synchronization, and effective bot permissions.
- The toolbar server selector contains only servers belonging to the selected bot.
  Changing or removing the bot clears the server selection.
- Gateway events update the explorers while they are open. Disconnecting clears that
  bot runtime's cache so Discord entities and cross-bot data cannot become stale.

This milestone performs no Discord channel, role, member, message, moderation, or
voice-connection write operations.

## Permission resolution

Permission calculation is performed against immutable role and overwrite read models.
It applies Discord's precedence in this order:

1. `@everyone` role and bot roles
2. Administrator override
3. `@everyone` channel overwrite
4. Aggregate bot-role overwrites
5. Bot-member overwrite

Results are displayed as Allowed, Denied, Not applicable, or Allowed through
Administrator. A source explanation is shown only when the resolver can identify it
from the captured role/overwrite data. Channel/category synchronization compares
target ID, target type, raw allowed bits, and raw denied bits without depending on
overwrite order.

## Gateway intents

Only non-privileged intents required by the explorer are enabled:

- `Guilds`: server discovery plus server, channel, role, and overwrite updates.
- `GuildVoiceStates`: connected-user counts for voice/stage channel metadata. Rapid
  voice-state changes are debounced before updating the UI cache.

`GuildMembers`, `GuildPresences`, and `MessageContent` are not enabled. Consequently,
the explorer does not provide a complete member directory, presence data, or message
content. Approximate member count comes from the guild gateway payload; the bot's own
member and role data remain available for permission calculation.

No elevated Discord permission is required to list servers delivered to the bot.
Channel visibility and the available management capabilities naturally follow the
bot's configured roles and channel overwrites.

## Performance and resilience

- Each bot has an isolated Discord client and immutable, per-runtime explorer cache.
- Gateway updates replace only the affected server model; they do not rebuild every
  bot or every server.
- Monotonic update sequences prevent an older async refresh from replacing newer
  gateway data.
- Bot/server changes cancel refresh work and guard against stale UI selections.
- Search is debounced, list/tree containers are virtualized, and rapid cache updates
  are batched on the WPF dispatcher.
- Permission results and server icons are cached in memory.
- Disconnect, server/channel removal, and role changes clear selections or invalidate
  affected permission results safely.
- Recoverable explorer errors use page states and Retry actions, not the fatal global
  exception dialog.

## Manual test checklist

1. Open Bot Manager and connect a saved test bot.
2. Confirm the toolbar and Server Explorer show only that bot's servers.
3. Compare server metadata, counts, category/channel order, and permissions with
   Discord.
4. Search server and channel names and numeric IDs.
5. Create, rename, move, and delete a temporary channel in Discord; confirm the tree
   and selected details update without restarting the application.
6. Change a bot-role or channel-overwrite permission and confirm permission results
   update.
7. Disconnect while viewing a server; confirm server/channel selection clears.
8. Reconnect; confirm the cache repopulates.
9. If another saved bot is available, switch rapidly between bots and confirm no
   server from the previous bot remains visible.
10. Review `%LOCALAPPDATA%\DiscordControlCenter\logs` and confirm no token, authorization
    header, raw gateway payload, or message content is present.

## Known limitations

- A complete member list and presence information require privileged intents and are
  intentionally unavailable.
- Discord may omit optional descriptions, topics, region overrides, boost data, forum
  defaults, or fields unsupported by the installed Discord.Net version; the UI labels
  these as unavailable.
- Icon caching is in-process and resets when the application exits.
- Live create/rename/move/delete validation requires a Discord test server and a user
  with permission to make those test changes outside this read-only application.

Phase 3 should add previewable, cancellable, rate-limit-aware channel writes only after
this explorer remains stable under large servers and multiple connected bots.
