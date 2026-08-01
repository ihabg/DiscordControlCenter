# Architecture

## Dependency direction

The project boundary remains:

`Discord.Net gateway/REST → Discord adapter/translators → Application cache/services → ViewModels → WPF views`

- `Core` owns immutable read models, completeness/status enums, permission comparison,
  hierarchy-preflight result types, immutable channel-operation plans/results, backup
  catalog/query contracts, retention policy, recovery classifications, role mappings,
  and versioned safe export models.
- `Application` owns profile/connection use cases, the per-runtime cache, permission
  resolution, hierarchy safety policy, operation planning/preflight/scheduling/
  execution, retry, compensation, and reconciliation.
- `Discord` is the only project allowed to reference Discord.Net. It owns clients,
  gateway subscriptions, paginated member retrieval, translation, and narrow channel
  write implementations.
- `Infrastructure` owns SQLite, paged operation history/backups, retention and
  reconciliation metadata, audit persistence, and DPAPI protection.
- `App` composes services and owns WPF state, cancellation generations, filtering,
  virtualization, operation/replacement selection/configuration/confirmation,
  retention preview, manual reconciliation decisions, save dialogs, and accessible
  result presentation.

No ViewModel retains a Discord.Net entity. No Core/Application/App type exposes one.

## Production-window diagnostics

The App's WPF lifecycle logs a native-window snapshot at `SourceInitialized`,
`Loaded`, and `ContentRendered`. The snapshot records the HWND, title/class, visible
and enabled state, screen/client rectangles, owner/parent/root, style flags, and
tool-window/no-activate/layered/cloaked/minimized classifications. This makes the
top-level window contract observable without logging user, token, database, or
Discord data.

`DiscordControlCenter.UiAutomationProbe` is deliberately isolated from the App's
service composition. It can start only the repository's exact Release executable,
passes `--ui-automation-probe`, enumerates that process's native windows, and attaches
UI Automation directly to its HWND. Probe mode renders the real MainWindow while
skipping the host, persistence, and Discord initialization. It inspects only window
metadata and named controls; the only permitted UI interaction is invoking the
Messages navigation item so the Manual Approvals surface can be checked.

## Guarded write boundary

The write dependency direction is:

`WPF configuration → immutable planner → preview/confirmation → bounded scheduler → executor → IDiscordChannelWriter → Discord.Net`

`IChannelOperationDialogService` is the sole normal Channels-page submission path. It
creates a draft ViewModel, obtains an immutable `OperationPlan` from
`IChannelOperationPlanner`, builds an `OperationPreview`, requires the dedicated
confirmation window, and only then calls `IChannelOperationScheduler.EnqueueAsync`.
WPF does not receive `IDiscordChannelWriter`.

The Backup Browser uses `IRecreateStructurePlanner`, then hands its immutable plan and
derived preview to `IOperationPlanSubmissionService`. That service opens the same
confirmation window and scheduler used by Channels. There is no backup-specific
writer or bypass executor.

`IDiscordChannelWriter` exposes only create category/text/voice, modify, bulk position,
set/delete overwrite, and delete channel. `BotConnectionManager` implements that
Application contract by selecting one bot runtime, taking its lifecycle semaphore,
rechecking connection, and delegating to `IDiscordBotClient`. Discord.Net types,
`RequestOptions`, raw Discord permission wrappers, guild/channel entities, and REST
exception handling remain confined to `DiscordControlCenter.Discord`.

There is no generic REST client or arbitrary mutation method. Messages, moderation,
role writes, webhooks, emojis/stickers, server deletion, user accounts, and voice
connections are absent.

## Immutable operation model

Core models capture:

- operation/step/correlation IDs, selected bot, server ID/name, operation type, title,
  creation time, and source accepted explorer sequence;
- exact target IDs, relevant before snapshots, proposed after snapshots, and ordered
  steps;
- parent-result dependencies for category cloning and batch before/after arrays for
  bulk reorder;
- required permissions, preconditions, risk, estimated request count, explicit/typed
  confirmation, safe audit reason, and compensation capability;
- exact raw allow/deny overwrite snapshots plus readable semantic changes;
- progress, per-step attempts/cancellation/failure/compensation, final state,
  reconciliation, backup reference, and safe failure metadata.

Plans contain no token, authorization header, Discord.Net object, raw response, or
message content. Arrays are immutable and plans cannot be modified after approval.

Risk is textually classified as Low, Moderate, High, or Irreversible. One create or
low-impact edit is Low; bulk rename/move/lock is Moderate; overwrite-heavy clone or
single deletion is High; multi-resource/category-with-children deletion is
Irreversible.

## Planning and preview

`ChannelOperationPlanner` reads only the selected bot's current immutable snapshot.
It validates server availability, supported types, names, duplicate requested and
existing reconciliation identities, parents, type/property compatibility, position,
slow mode, default archive durations, bitrate, user limit, requested child ownership,
50-item plan limit, and the determinable 500-channel server limit.

It emits no step for an unchanged edit and rejects an invalid bulk plan atomically.
Rename modes calculate every final name before approval. Moves preserve selected
relative order; same-category ordering becomes one Discord bulk-position step.
Category-with-children deletion expands explicit child-first steps. Category-only
deletion has one delete step but captures and previews every child becoming
uncategorized.

Lock/unlock uses Discord's exact raw Send Messages/Add Reactions and Connect/Speak
bits. It merges those bits into the captured role overwrite and preserves every
unrelated allow/deny bit. Synchronization computes ordering-independent add, update,
and remove steps for every role/member overwrite.

The preview is derived from the plan, never from mutable dialog fields. It lists every
property transition and overwrite transition, affected/request counts, permissions,
risk, consequences, audit reason, correlation, and strong confirmation requirement.

## Freshness and preflight

Immediately before the first write, `ChannelOperationPreflightService`:

1. verifies the selected bot remains connected;
2. verifies the server remains available and has the approved name;
3. ensures the current accepted sequence has not regressed below the approved source;
4. finds every captured target and compares a SHA-256 fingerprint of relevant modeled
   state, including parent, position, supported fields, forum metadata, and sorted raw
   overwrites;
5. detects create/clone identity conflicts and rechecks the channel limit;
6. resolves Manage Channels/Manage Roles for every current target through the existing
   `PermissionResolutionService`;
7. blocks Unknown/incomplete permission results;
8. applies `RoleHierarchySafetyService` to non-`@everyone` role overwrite targets.

A newer global sequence alone is allowed. A relevant mismatch returns a safe
property-level old→new explanation, marks the plan Stale, sends no request, and
requires a new preview.

## Queue ownership and cancellation

`ChannelOperationScheduler` owns one bounded `Channel<T>` with capacity 32 and two
long-lived workers. It does not spawn one task per plan. A semaphore keyed by
`(BotProfileId, ServerId)` creates the default ordered stream and prevents overlapping
plans on one server. Other bot/server streams can use the second worker.

Before queue insertion, a Pending history record with the unique operation ID is
inserted. The in-memory dictionary and SQLite primary key prevent duplicate starts.
Queue snapshots expose position and Pending, Running, Waiting, Cancelling, Completed,
PartiallyCompleted, Failed, Stale, Cancelled, and ReconciliationRequired states.

Cancellation is cooperative at queue-gate, backup, retry-delay, reconciliation, and
between-step boundaries. The scheduler rechecks cancellation after acquiring a server
gate. A request already accepted by Discord is not relabeled cancelled. Remaining
steps receive explicit not-started results. On disposal, the channel completes,
lifetime and item tokens are cancelled, workers get up to ten seconds to terminate,
then per-server gates are disposed.

Persisted Pending/Running/Waiting/Cancelling entries are not resumed blindly after
startup. The executor persists a safe checkpoint after every completed step.
`OperationRecoveryService` reads those checkpoints and, only when a bot/server is
currently available, calls the existing read-only reconciliation service for
incomplete steps. It classifies completed-after-reconciliation, partial, not started,
unable to inspect, unsupported schema, or manual review. It never calls a writer,
reruns a plan, or compensates at startup. Recent terminal results are hydrated into
Operation Center.

State changes are also appended to `OperationStateTransitions`. Manual decisions are
separate immutable rows and cannot invoke Discord; corrective work returns through a
new plan, preview, confirmation, and queue submission.

## Rate limits and retry

Discord.Net owns normal rate-limit scheduling through `RequestOptions` and
`RetryMode.AlwaysRetry`. Application code contains no requests-per-second loop.

The executor permits at most three attempts with bounded exponential backoff and
jitter only when the adapter reports a known retryable failure. Permission,
configuration, target, stale, cancellation, and ordinary Discord rejection failures
are terminal. A timeout, network interruption, or 5xx outcome is Uncertain and cannot
be retried until reconciliation proves it was not applied. This prevents double
creation after a lost response.

## Idempotency and reconciliation

Discord channel APIs do not expose a universal application idempotency key.
Application protection therefore uses unique persisted operation/step IDs,
duplicate-start checks, captured before/after state, and reconciliation:

- create: match server, parent, type, case-insensitive name, and operation timeframe;
- update/overwrite: compare current exact fingerprint to after and before;
- delete: compare target absence/presence;
- bulk reorder: compare every channel to all batch after/before snapshots.

Zero create matches means not applied; exactly one means applied; multiple matches are
ambiguous. State matching neither before nor after is also ambiguous. Ambiguity stops
the plan as ReconciliationRequired instead of guessing.

After known completed steps, the executor refreshes the sole explorer cache. A refresh
failure retains the successful request results but marks manual reconciliation.

## Compensation and backups

Compensation is reverse ordered and only modeled where accurate:

- delete a new resource after a later step fails;
- restore captured name, parent, position, topic/slow-mode/voice properties;
- restore captured bulk positions;
- restore or remove the exact prior permission overwrite.

Compensation is not called rollback and cache reconciliation remains authoritative.
Deletion has `None` capability and cannot restore IDs, messages, links, threads,
webhooks, integrations, or all associations.

Before any High/Irreversible or otherwise destructive plan, the executor serializes a
`ServerStructureBackup` containing only relevant server/channel/category state,
supported forum metadata/tags, exact overwrite raw values, source bot, sequence,
timestamp, operation ID, and correlation ID. Backup persistence must succeed before
the first Discord request.

`RecreateStructurePlanner` consumes one validated backup and supports only category,
ordinary text, and voice snapshots. It validates selected indices, supported types,
current names/limits, category mappings, current server capabilities, and explicit
role mappings. Categories precede channels; child parent IDs and final reorder IDs are
bound from earlier create results. The plan always describes replacement resources
with null pre-create IDs and records its source backup.

Role-ID matches may be selected exactly. Name matches are suggestions only. The user
must explicitly choose another role, `@everyone`, or Skip. Member overwrites default
to Skip. Mapped overwrite targets are rechecked by permission and hierarchy preflight
immediately before execution.

Recreate compensation is plan policy, not executor guesswork:

- Keep successful resources is the default and has no cleanup capability.
- Attempt cleanup permits reverse-order best-effort deletion of newly created
  replacements.
- Stop for manual review retains successful replacements and makes a partial failure
  ReconciliationRequired.

Uncertain outcomes and failed local checkpoints never trigger automatic compensation.

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
possess, target member ownership/hierarchy, and incomplete member roles. Phase 4A
preflight calls this service for every non-`@everyone` role overwrite target.

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

Voice structure writes are validated separately by
`IVoiceChannelValidationService`. The current modeled boost tier supplies the
determinable maximum bitrate (`None` 96 kbps, Tier 1 128 kbps, Tier 2 256 kbps, Tier
3 384 kbps); user limit is 0–99. Unknown tier or unavailable region metadata produces
an explicit compatibility warning instead of false certainty. The planner validates
at preview time and preflight validates again against the current server snapshot.
Discord/library invalid-value failures are known non-retryable outcomes.

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

Channels maintains a separate ID set for deliberate operation selection; detail
selection remains independent. The set survives matching cache refreshes and removes
missing IDs, but clears on bot/server/disconnect changes. Contextual commands query
existing permission resolution and active same-server scheduler state. They never
calculate Discord precedence or call a writer.

Operation Center subscribes once to scheduler snapshots and dispatches collection
updates through `UiDispatcher`. It exposes cancel and “regenerate preview” navigation,
not direct retry. The technical section shows only exception type, safe error code,
outcome certainty, and correlation—not stack traces or exception messages.

All new views use existing semantic brushes/styles. They provide focusable controls,
tooltips, automation names, trimming, wrapping, scrolling, loading/empty/limited/
partial/error states, and retry controls. No view embeds raw foreground/background
colors.

## Phase 5A messaging and automation safety boundary

The messaging UI cannot call Discord.Net directly. It constructs immutable
`MessageDraft` and `MessageOperationPlan` records, then delegates to a single
`IDiscordMessageWriter` boundary owned by the connection manager. Preflight runs again
immediately before that boundary and blocks disconnected bots, stale destinations,
unknown/denied channel permissions, unavailable recipients, and unsupported target
types. A delivery attempt has one bounded retry only for explicitly retryable and
non-uncertain failures; uncertain outcomes are never retried automatically.

`AllowedMentions.None` is applied to all current Discord writes. Broad mention-like
content therefore receives an additional typed confirmation but is still sent with
Discord mention parsing disabled. Template rendering uses only an explicit variables
dictionary and inserts a zero-width separator after member-controlled `@` characters.

Schema 5 adds versioned message templates, scheduled-message definitions and unique
occurrences, versioned automation rules, unique automation executions, circuit-breaker
state, and delivery history. Delivery history stores operation/correlation IDs,
destination IDs, safe state/failure metadata, and timestamps only; message content and
template values are never stored. Scheduled delivery and join automation execution are
not yet active: scheduled recurrence is calculation-only and automation remains
draft/preflight-only until a later release provides an explicit enablement workflow and
  event subscriber.

### Manual-approval preflight and immutable usage

The Manual Approvals read path is separate from delivery. `IScheduledApprovalPreflightService`
receives only the reserved immutable occurrence snapshot and produces fourteen ordered,
application-facing checks. Snapshot compatibility, message limits, embed limits, and
mention-policy validity are evaluated without a bot connection. Bot profile, connection,
server, channel, capability, and permission checks describe only current sendability.
When a dependency cannot be resolved, dependent required checks are `Unavailable` or
`Unknown`; they are never shown as allowed. Optional Embed Links, Attach Files, and Mention
Everyone checks are always represented as `Not required` when the snapshot does not require
them.

WPF consumes safe check/usage records rather than Discord.Net objects, raw exceptions, or
human-error parsing. Selection and refresh have independent generations so an old snapshot
or old status refresh cannot enable a newer selection. Approval uses the current granular
result as advisory UI state, refreshes it before confirmation, and the guarded executor
still runs its authoritative aggregate preflight after atomic claim and immediately before
the Discord adapter boundary.

`MessageLimits` is the single source for message/embed maxima and the shared 90% near-limit
threshold. It emits immutable per-component rows for body, title, description, author,
footer, ordered field names/values, field count, and total embed characters. Near-limit
warnings are non-blocking; over-limit rows block approval. These read models are transient
and are not persisted with tokens, authorization data, raw payloads, or exception bodies.

## SQLite, auditing, retention, and privacy

SQLite schema 4 is one backward-compatible transaction over schema 3. It preserves
existing history and backup rows, then adds:

- `BackupCatalogMetadata` for reason/type/counts/schema/pin/size/corrupt status;
- `BackupRetentionSettings`, defaulting to Keep indefinitely;
- `OperationStateTransitions`;
- `ManualReconciliationDecisions`;
- `BackupCleanupAudit`;
- server/time and bot/time backup indexes.

Existing schema-3 backup JSON is parsed during migration to backfill bounded metadata.
A malformed row is retained and marked corrupt; a newer schema is retained and
classified rather than deserialized into a plan. The schema-version row is committed
only with the migration transaction.

History includes operation/correlation/type/bot/server/target IDs, safe display names,
timestamps, state/counts, compensation summary, backup identifier, safe error codes,
duration, sanitized audit reason, immutable plan JSON, and safe result JSON. Initial
insert uses the operation-ID primary key and cannot overwrite a duplicate; subsequent
state transitions use explicit update/upsert behavior. Operation Center loads at most
100 recent records. Backups have a unique operation ID.

No operational table stores tokens, authorization headers, raw Discord payloads, messages,
DMs, voice data, member directories, or full exception messages. Automatic retention
deletion is not scheduled. Cleanup is an explicit dry run and confirmation, preserves
pinned and configured failed/partial backups, deletes backup+metadata rows in one
transaction, and appends the exact local cleanup audit. It never reaches the Discord
adapter.

History and backup catalog queries validate page sizes (1–200), execute SQL
search/filter/sort/count with `LIMIT/OFFSET`, and load detail on selection. Startup
does not load every history/backup JSON document. Live compatibility filters scan
bounded pages and retain only the requested output page.

Safe exports page through repository results, honor cancellation, and serialize a
versioned DTO rather than persisted plan/result JSON. History supports JSON and CSV;
backup metadata supports JSON. Excluded fields are named in the export model.

Intent changes write safe audit descriptions using profile IDs/names and Boolean state.
Tokens, protected-token bytes, fingerprints, authorization headers, message content,
raw payloads, and unnecessary personal information are excluded from diagnostics and
logs.

DPAPI remains `CurrentUser` with application entropy. Token buffers retain the existing
protection/zeroing behavior and masked UI.

## Phase 4B boundary and known limits

Phase 4B extends the guarded channel engine with replacement planning and local
operational recovery. Read-model cache updates remain observations and never trigger
implicit writes. Role/member explorers, permission simulation, hierarchy explanations,
and voice inspection remain read-only.

Direct announcement/forum/media/stage/thread deletion or cloning is blocked because
the installed modeled surface cannot reproduce them accurately. Category deletion
with such children is blocked unless category-only semantics leave children intact.
Backup recreation also excludes those types and all message/thread/webhook/invite
content. Existing same-name creation identities are blocked to keep timeout
reconciliation unambiguous.

There is no automatic “undo delete,” same-ID restoration, blind persistent retry,
background backup deletion, cross-server identity proof, or universal Discord
idempotency key. Startup recovery can inspect only what the currently connected bot
can see. Multiple plausible matches remain manual review. Corrective-plan generation
from a recorded manual decision is a recommended later recovery enhancement.

Member moderation, role mutation/assignment, messaging/DMs, auto-role behavior,
webhook/emoji/sticker mutation, bot voice connection/audio, server deletion, and
user-token/self-bot behavior remain outside the architecture.

## WPF startup and UI-harness isolation

### Manual approval queue and history

Scheduled-approval list rows are projected from normalized safe metadata on the occurrence:
reservation time, compatibility, broad-mention flag, destination names/ID, template reference, and
terminal result. The immutable snapshot JSON is selected only by explicit detail inspection. SQLite
performs search, filters, count, stable sort, and `LIMIT`/`OFFSET` paging; searches intentionally
exclude the immutable message body and embeds. Every ordering ends with the occurrence ID to prevent
movement between pages.

History is a filtered view of the same occurrence lifecycle, rather than a second content-bearing
store. Delivered, failed, uncertain/manual-review-required, skipped, and archived states retain safe
decision metadata and correlation information. Uncertain occurrences are terminal for this workflow:
there is no automatic resend or retry action. WPF exposes history content only behind an explicit
"immutable historical outbound content" action and never renders it in a list row.

The selected bot/server in the application toolbar forms the only Manual Approvals scope. Queue and
terminal history do not run an unbounded cross-bot query when that context is absent. Safe schedule
options are loaded independently for that scope, include All schedules, and represent an orphaned
history source as Deleted or unavailable schedule. Queue and history keep separate query, filter,
sort, page, loading/error, selection, and generation state so an action in one area cannot reset the
other. The UI harness uses only in-memory safe metadata and does not access SoundPad or any unrelated
application.

`App.OnStartup` records safe milestones, creates the dependency-injection host, resolves the
main window, assigns `Application.MainWindow`, and invokes `Show` before completing local
startup work. Host/database/recovery/scheduler initialization then runs without blocking the
WPF dispatcher; UI view-model loading is dispatched only after that local work succeeds.
`Loaded`, `ContentRendered`, and native-handle milestones make a startup that created a
window diagnosable. Fatal startup failures are logged safely and shown to the user instead of
leaving a background process. Logs are JSONL files in the application local-data log folder.

`DiscordControlCenter.UiHarness` references only the reusable App views/presentation models
and constructs no production dependency-injection host. It owns deterministic scenario data
in memory, provides test-only layout/state controls, and opens the same confirmation window
used by Manual Approvals. Therefore it cannot resolve a production token protector or Discord
writer, open the production SQLite database, initiate gateway/REST traffic, or persist a
decision. It is a separate non-packable project and is not included in the production app's
publish output.
