# Architecture

## Dependency direction

The project boundary remains:

`Discord.Net gateway/REST → Discord adapter/translators → Application cache/services → ViewModels → WPF views`

- `Core` owns immutable read models, completeness/status enums, permission comparison,
  and hierarchy-preflight result types.
- `Application` owns profile/connection use cases, the per-runtime cache, permission
  resolution, and hierarchy safety policy.
- `Discord` is the only project allowed to reference Discord.Net. It owns clients,
  gateway subscriptions, paginated member retrieval, and translation.
- `Infrastructure` owns SQLite, audit persistence, and DPAPI protection.
- `App` composes services and owns WPF state, cancellation generations, filtering,
  virtualization, and accessible presentation.

No ViewModel retains a Discord.Net entity. No Core/Application/App type exposes one.

## Runtime and cache ownership

`BotConnectionManager` owns one `BotRuntime` per connected profile. A runtime owns:

- one `IDiscordBotClient`/`DiscordSocketClient`;
- one lifecycle semaphore;
- one `BotExplorerCache`.

The existing explorer cache is the sole coordinated read-model cache. It now contains
server, channel, role, member, and voice state. There is no competing member or voice
cache.

Each immutable `BotExplorerSnapshot` is scoped by bot profile ID. Every server contains
its own `MemberCollectionReadModel`; members can never cross a bot or server boundary.
Adapter updates carry a monotonically increasing sequence. The cache rejects a sequence
less than or equal to the last accepted sequence.

Disconnect or profile removal clears the bot runtime and every explorer entity.
Losing a server removes that server only. Failures in one server or runtime do not
affect another bot.

## Member cache and completeness

Member data is memory-only and keyed by Discord user ID inside one bot/server snapshot.
Externally visible values are immutable arrays.

Completeness is explicit:

- `Limited`: privileged access is locally disabled; only legitimately visible users
  are represented.
- `Loading`: Discord member pages are still being retrieved.
- `Partial`: some results are present but the cache cannot claim completeness.
- `Complete`: the paginated operation completed.
- `Cancelled`, `Failed`, and `Unavailable`: terminal or absent states with safe UI
  behavior.

`DiscordBotClient.LoadMembersAsync` consumes Discord.Net's asynchronous guild-member
pages with a linked cancellation token. Each page is translated, de-duplicated, and
published in UI-sized batches. Discord.Net serializes/rate-limits REST work. One
semaphore prevents duplicate parallel loads for the same server.

The cache caps members at 100,000 per server. Exceeding the cap produces `Partial` and
a safe explanation. Loading batches defer expensive role-member recounts until the
terminal snapshot. Live batches recalculate only the affected server.

Member joins, updates, role/nickname/timeout/boost changes, leaves, and download
completion are incremental. Rapid events are collected per guild for 100 ms. A
completed collection remains complete after a supported live add/update/remove.

Role updates refresh cached member highest-role labels and positions. Snapshot version
changes and explicit invalidation prevent stale permission calculations.

Members are never persisted to SQLite.

## Privileged intent selection

`BotProfile.EnableFullMemberAccess` is a persisted, per-profile Boolean. Schema version
2 adds `EnableFullMemberAccess INTEGER NOT NULL DEFAULT 0` through an idempotent,
backward-compatible migration.

New and migrated profiles default to false.

`DiscordBotClientFactory` receives the setting when constructing one runtime:

- false: `Guilds | GuildVoiceStates`;
- true: `Guilds | GuildVoiceStates | GuildMembers`.

Changing the setting requires WPF warning confirmation. `BotProfileService` disconnects
only the affected runtime, persists the option, writes a secret-free audit entry, and
reconnects that bot if it was previously active.

A gateway `WebSocketClosedException` with close code 4014 becomes an actionable
`PrivilegedIntentException` and faulted snapshot. The client marks the stop as manual,
clears explorer data, completes the pending ready task with the safe exception, and
does not add a reconnect loop. The user may correct the Portal configuration or disable
the local option.

Developer Portal authorization, local intent selection, guild permissions, and role
hierarchy are separate concepts throughout the UI and documentation.

## Gateway subscriptions and disposal

One adapter instance subscribes once to:

- ready, connected, disconnected, latency, and safe log events;
- guild available/unavailable/join/leave/update;
- channel create/update/destroy;
- role create/update/delete;
- user join/leave, guild-member update, and member-download completion;
- user voice-state update.

Every subscription has a matching removal in `DisposeAsync`. Lifetime cancellation is
signaled before stopping the socket. Member loads link to lifetime cancellation.
Current debounce tokens self-cancel and dispose. The application does not compete with
Discord.Net's normal reconnect behavior.

Member-loading errors publish a page-level `Failed` state and do not break unrelated
servers or the bot connection. Selection changes cancel work and stale generations
discard completion callbacks.

## Roles and hierarchy preflight

Roles are translated without member intent and include:

- ID/name/position and `@everyone`;
- raw and modeled permission bits plus the complete Discord permission-name list;
- primary color, icon/Unicode emoji, hoist, mentionable, managed, bot-managed, and safe
  tag metadata;
- exact, partial, or unavailable member counts.

`ExplorerSearch.OrderRoles` places highest roles first and `@everyone` last.

`RoleHierarchySafetyService` is a pure, read-only Application service. It returns:

- Allowed, Denied, or Unknown;
- stable reason code;
- safe explanation;
- required permission;
- bot and target role positions;
- data completeness.

It checks Manage Roles or the relevant moderation/nickname permission, server ownership,
managed roles, `@everyone`, equal/above hierarchy, permissions the bot does not
possess, target member ownership/hierarchy, and incomplete member roles. Phase 4 must
call this service before any future write preview or execution.

## Permission simulator

`PermissionResolutionService` now supports selected-bot, member, and role subjects. Its
cache key includes bot ID, server ID, channel ID, snapshot version, subject kind, and
subject ID.

Member precedence:

1. base `@everyone`;
2. aggregated assigned roles;
3. Administrator;
4. channel/category `@everyone` overwrite;
5. aggregated role overwrites;
6. member-specific overwrite.

Role subjects use `@everyone` plus the selected role and its channel overwrite.
Incomplete member-role data produces `Unknown`; it never yields a confident source.

Comparison aligns modeled permissions and produces:

- both allowed;
- first only;
- second only;
- both denied;
- unknown;
- not applicable.

General, text, voice, and moderation results include text labels and icons so color is
not the sole indicator.

## Voice-state flow

`GuildVoiceStates` is always enabled. Initial channel translation includes immutable
visible `VoiceStateReadModel` values. A voice event translates only the affected user
and destination state, then stores the latest change per guild/user for 250 ms.

The cache removes that user from previous accessible voice/stage channels, inserts the
latest state into the destination, updates occupancy, and updates/removes the limited
member projection. It does not rebuild the server or unrelated members.

The Voice view is observational only. No `ConnectAsync` on a voice channel, audio
client, voice server request, or transmission code exists.

## Diagnostics

`IBotExplorerService.GetDiagnostics` joins safe connection state with immutable cache
metadata:

- latency and ready/disconnect/reconnect timestamps;
- cached server/channel/role/member counts;
- aggregate member completeness;
- last sequence and explorer refresh;
- pending refresh and cache age;
- local GuildMembers choice and operational status;
- voice event count/time;
- recent safe gateway error summary.

The Dashboard renders this as compact cards, not a raw log console. Discord gateway log
events record only source, severity, and exception type. Raw Discord messages/payloads
are not forwarded to Serilog.

## WPF state and performance

`MainWindowViewModel` owns global bot/server selection and propagates it to all explorer
ViewModels. Bot changes clear server context. The toolbar selection guard prevents WPF
collection replacement from transiently clearing a restored server.

Members uses ID-based incremental collection synchronization, debounced filtering, a
collection view, selected-item removal handling, recycling virtualization, and command
cancellation. Other new views preserve selection by Discord ID and clear it when the
entity disappears.

All new views use existing semantic brushes/styles. They provide focusable controls,
tooltips, automation names, trimming, wrapping, scrolling, loading/empty/limited/
partial/error states, and retry controls. No view embeds raw foreground/background
colors.

## SQLite, auditing, and privacy

SQLite schema 2 stores only the local Boolean intent selection in addition to existing
profile metadata and the DPAPI-protected credential. It does not store member
directories, voice state, role membership, presence, messages, or raw gateway data.

Intent changes write safe audit descriptions using profile IDs/names and Boolean state.
Tokens, protected-token bytes, fingerprints, authorization headers, message content,
raw payloads, and unnecessary personal information are excluded from diagnostics and
logs.

DPAPI remains `CurrentUser` with application entropy. Token buffers retain the existing
protection/zeroing behavior and masked UI.

## Read-only boundary

Phase 3 contains no Discord create, edit, delete, move, role assignment, moderation,
nickname, message, direct-message, bulk, voice-connect, or audio operation. REST usage
is limited to authentication/read validation and member retrieval.

Phase 4 should introduce a separate previewable command engine with cancellation,
bounded concurrency, rate-limit awareness, confirmation, correlated audit entries, and
mandatory hierarchy/permission preflight. It must not turn read-model cache updates
into implicit writes.
