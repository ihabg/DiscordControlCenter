# Architecture

## Dependency direction

`App` composes the process and depends on `Application`, `Infrastructure`, and
`Discord`. `Application` coordinates use cases against contracts and domain models
from `Core`. `Infrastructure` implements persistence and protection contracts.
`Discord` implements authenticated REST validation, gateway clients, and translation
from Discord.Net socket entities.

Neither WPF ViewModels nor views reference Discord.Net. Socket entities are translated
inside `DiscordControlCenter.Discord` into immutable explorer read models from
`DiscordControlCenter.Core`.

## Runtime ownership

`BotConnectionManager` owns one `BotRuntime` per connected profile. A runtime owns:

- one `IDiscordBotClient`/`DiscordSocketClient`;
- one lifecycle semaphore;
- one `BotExplorerCache`.

No runtime, cache, or permission calculation is shared between bot profile IDs. A
failure or cancellation for one bot does not stop another. Connect-all remains capped
at three concurrent connection attempts.

The Discord adapter subscribes once in its constructor and explicitly unsubscribes
every handler during asynchronous disposal. `BotConnectionManager` also pairs its
client status/explorer subscriptions with removal. Discord.Net owns gateway reconnect
behavior; the application does not add a competing reconnect loop.

## Explorer cache lifecycle

The bot runtime is the cache owner.

1. `Ready` translates the socket guild cache into immutable `ServerReadModel` and
   `ChannelReadModel` values and emits a reset.
2. `BotExplorerCache` copies that reset into an immutable snapshot identified by bot
   profile ID and version.
3. Gateway updates carry a monotonically increasing adapter sequence. The cache ignores
   an older sequence, preventing stale async refresh results from overwriting a newer
   event.
4. Explicit Refresh performs the same translation off the UI thread. Cancellation
   restores the previous cache state.
5. A disconnect or profile removal clears the runtime snapshot. Reconnect/Ready
   repopulates it from current socket state.
6. Runtime removal disposes the client, handlers, semaphore, and cache ownership
   together.

Snapshots contain no Discord.Net object and use immutable arrays. The cache exposes
only snapshots and controlled `ExplorerCacheChanged` events.

## Gateway event flow

The adapter handles:

- `Ready`, `Connected`, and `Disconnected` (including the reconnect transition);
- `GuildAvailable`, `GuildUnavailable`, `JoinedGuild`, `LeftGuild`, and `GuildUpdated`;
- `ChannelCreated`, `ChannelUpdated`, and `ChannelDestroyed`;
- `RoleCreated`, `RoleUpdated`, and `RoleDeleted`;
- `UserVoiceStateUpdated` for connected voice/stage counts.

Guild joins/updates, channel events, and role events rebuild only the affected server
read model. Left-guild removes only that server. Role and overwrite changes produce a
new server/cache version, invalidating affected permission results. Voice-state events
are debounced per server for 250 ms before translation.

WPF receives cache events on arbitrary gateway threads, posts them to `UiDispatcher`,
and batches collection refreshes for 100 ms. All `ObservableCollection` mutations occur
on the UI dispatcher.

## Permission resolution

`PermissionResolutionService` is a reusable, Discord.Net-free service. It combines the
`@everyone` role, the selected bot's roles, and channel overwrites using Discord
precedence. Administrator produces an explicit `AllowedThroughAdministrator` status.
Text/voice permissions return `NotApplicable` for unrelated channel types.

The calculation cache key includes bot ID, server ID, channel ID, and explorer snapshot
version. Server/channel events create new versions, and explorer ViewModels explicitly
invalidate the affected bot/server cache entries.

`PermissionSynchronization.AreSynchronized` compares normalized overwrite tuples:
target ID, target type, raw allow value, and raw deny value. It detects missing,
additional, or changed overwrites, ignores ordering, returns `null` when there is no
parent category, and treats two empty overwrite collections as synchronized.

Permission-source labels are limited to cases the resolver can prove: Administrator,
server role, `@everyone` overwrite, role overwrite, bot-member overwrite, and
category-inherited variants.

## Selection and stale-data protection

`MainWindowViewModel` owns the toolbar bot/server context so selection survives page
navigation. Changing the bot clears the server and channel selection. The server
options collection is refreshed under a selection guard so WPF's two-way ComboBox
binding cannot temporarily clear the Channels page while items are replaced.

Server refresh captures a selection generation and discards completion work after a
bot change. Channel deletion and server removal resolve against the latest immutable
snapshot and clear missing selections. Toolbar options are populated only for the
selected connected bot.

## Gateway intents

The configured intents are deliberately non-privileged:

- `Guilds` is required for server discovery and guild/channel/role/overwrite events.
- `GuildVoiceStates` is required for accurate connected-user counts in voice/stage
  channel metadata.

`AlwaysDownloadUsers` is false and message caching is disabled. `GuildMembers`,
`GuildPresences`, and `MessageContent` are not enabled. A complete member directory,
presence data, and message content are therefore unavailable by design.

## UI and performance

Server lists and the channel tree use recycling virtualization. Search is debounced for
250 ms; channel filtering keeps parent categories for matching children. Views and
ViewModels are singleton navigation pages rather than being reconstructed on every
event. Optional metadata is represented with nullable fields so unavailable values are
not confused with zero.

Server icon `BitmapImage` instances are cached by URL. Download/URI failure returns an
empty icon without escalating to the fatal dialog. Permission results are cached by
snapshot version. No gateway event performs a database write.

The existing semantic theme brushes and reusable input/button/list styles remain the
only view color source. Both explorer pages include loading, empty, disconnected,
faulted, Retry, keyboard-focus, tooltip, trimming, scrolling, and narrow-window states.

## Errors and secrets

Recoverable explorer failures become cache/page fault states with safe messages. The
protected global exception handler remains reserved for fatal application faults and
retains its non-recursive dialog guard.

Logs use profile/server identifiers and exception type names. They do not record bot
tokens, protected-token contents, authorization headers, raw gateway payloads, private
messages, or Discord write bodies.

## Data and secrets

SQLite is opened in WAL mode and initialized through an idempotent, version-recorded
schema. Repositories open short-lived pooled connections and use parameterized SQL.

Bot tokens are authenticated before persistence, protected with Windows DPAPI using
`CurrentUser` scope and application entropy, then stored only as encrypted blobs.
Temporary UTF-8 buffers are zeroed. The UI uses a fixed mask. Phase 2 does not change
credential behavior.

## Read-only boundary and growth

Phase 2 contains no create, edit, delete, move, message, moderation, role-assignment, or
voice-connection Discord operations. Phase 3 should introduce application commands and
preview models for rate-limit-aware, cancellable writes without moving Discord.Net
types across the adapter boundary.
