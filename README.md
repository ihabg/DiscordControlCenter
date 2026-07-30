# Discord Control Center

A Windows WPF control center for multiple official Discord bot accounts. The current
milestone combines the existing production-quality observability surface with a
guarded channel-operations engine:

- secure, isolated bot profiles and gateway runtimes;
- Server and Channel Explorers;
- limited or full Members Explorer;
- Roles Explorer and hierarchy safety explanations;
- member/role/channel permission simulation;
- live Voice-State Inspector;
- compact gateway and cache diagnostics;
- immutable channel-operation planning, exact preview, explicit confirmation,
  freshness preflight, bounded execution, reconciliation, and persisted results;
- a paged Backup Browser, explicit replacement-structure planning, configurable local
  retention, searchable operation history, safe export, and interrupted-operation
  recovery.

Channel creation, supported edits, bulk rename, move/reorder, structural cloning,
lock/unlock presets, category permission synchronization, and guarded deletion are the
only Discord mutations. The application still performs no messaging, member
moderation, role assignment/mutation, webhook management, user-account automation, or
voice connection/transmission.

## Requirements

- Windows 10 or later
- .NET 10 SDK
- Discord bot tokens created in the Discord Developer Portal
- A private disposable server for privileged-intent or lifecycle testing

## Run

```powershell
dotnet restore DiscordControlCenter.slnx --configfile NuGet.Config
dotnet run --project src/DiscordControlCenter.App/DiscordControlCenter.App.csproj --no-restore
```

Application data is stored under `%LOCALAPPDATA%\DiscordControlCenter`. Tokens are
protected for the current Windows user with DPAPI `CurrentUser`, are always masked in
the UI, and are never written to logs.

## Read-only explorers

Choose a saved bot and server in the toolbar after connecting the bot explicitly from
Bot Manager.

- **Servers** shows accessible server metadata, counts, the selected bot's role
  hierarchy, and server-level permissions.
- **Channels** preserves Discord category order, supports name/ID search, displays
  type-specific metadata, and resolves the bot's channel permissions.
- **Members** displays member identity, role, join, boost, screening, timeout, and
  accessible voice-state data. Search covers username, global display name, nickname,
  and ID. Filters cover human/bot, voice, boost, timeout, screening, and role.
- **Roles** lists roles from highest to lowest with `@everyone` last. It shows complete
  translated base permission names, exact or partial member counts, management tags,
  and read-only hierarchy preflight explanations.
- **Permissions** compares the selected bot, visible members, and roles in an
  accessible channel. Results explicitly distinguish both allowed, first only, second
  only, both denied, not applicable, and unknown.
- **Voice** observes accessible voice/stage occupancy and member voice flags through
  `GuildVoiceStates`. It never joins a channel or transmits audio.
- **Dashboard** includes compact per-bot connection, cache, member-completeness,
  sequence, refresh, intent, voice-activity, and safe gateway-error diagnostics.

Changing or removing the selected bot clears the server context. Disconnecting clears
that bot's explorer cache. No snapshot or Discord entity is shared between bot
profiles.

## Guarded channel operations

The Channels page has a deliberate checkbox selection mode and contextual action bar.
Create does not require a selection. Other actions explain why they are disabled when
the bot/server context, selected types, effective Manage Channels/Manage Roles result,
or active same-server operation makes the action unsafe.

Every write follows one non-bypassable lifecycle:

1. Configure the intended change in a local dialog.
2. Build an immutable plan from the current bot-scoped explorer snapshot.
3. Validate names, types, values, parents, limits, duplicates, and operation scope.
4. Display the exact before/after property and permission-overwrite diff.
5. Match the required explicit or typed confirmation exactly.
6. Persist and enqueue that unique plan.
7. Immediately recheck connection, server availability, cache sequence, target
   fingerprints, permissions, hierarchy, name conflicts, and channel limits.
8. Save the required local structure backup before a destructive request.
9. Execute ordered steps cooperatively with progress and cancellation.
10. Reconcile uncertain REST outcomes and refresh the explorer cache.
11. Persist a secret-free result and display it in Operation Center.

The selected normal UI action cannot call the Discord adapter. It can only open the
planner and confirmation flow. A changed relevant target makes the approved plan
stale; a newer unrelated cache sequence is allowed when captured target fingerprints
still match.

Supported operations:

- create one or up to 50 categories, text channels, or voice channels;
- edit name, parent, position, text topic/NSFW/slow mode/default archive, and voice
  bitrate/user limit/region override;
- exact, prefix, suffix, find/replace, and sequential bulk rename;
- move between categories or Uncategorized and bulk reorder within a category;
- clone an ordinary text/voice channel's modeled structure, optionally with exact
  overwrites;
- clone a category alone, with selected children, or with all supported children,
  independently choosing category overwrite copy, child overwrite copy, or child
  synchronization;
- lock/unlock only Send Messages/Add Reactions or Connect/Speak bits for a visible
  role, preserving every unrelated allow and deny bit;
- replace selected child-channel overwrites with the exact parent-category set;
- delete ordinary channels, delete a category only, or explicitly delete selected/all
  supported children before their category;
- create selected replacement categories, text channels, and voice channels from a
  structural backup through the same immutable plan, preflight, confirmation, queue,
  executor, checkpoint, and reconciliation path.

Announcement, forum, media, stage, thread, and other channels remain inspectable but
are blocked from destructive or structurally inaccurate Phase 4A plans.

## Confirmation, queue, and Operation Center

The confirmation window names the bot/server, risk text, affected count, estimated
requests, required permissions, exact changes, overwrite bit differences,
consequences, backup behavior, audit reason, stale warning, and correlation ID.
High-impact deletion requires exact text such as `DELETE 3 CHANNELS`; category-plus-
children deletion also includes the exact server name. Enter confirms only while the
requirement matches; Escape cancels. One plan cannot be submitted twice.

The bounded scheduler has two workers and capacity for 32 queued plans. A semaphore
serializes each bot/server stream, so no two plans overlap on one server while
different bot/server streams can progress independently. Operation Center queries
persisted history in pages. Search covers title, operation/correlation ID, server, and
target; filters cover bot, server, type, state, risk, date, backup presence, and manual
reconciliation. It shows progress, step counts, safe errors, timestamps, audit reason,
backup/compensation/reconciliation state, state transitions, and persisted manual
decisions. JSON and CSV exports use a normal Windows save dialog and show the record
count, included metadata, excluded private fields, and destination before writing.

Cancellation is cooperative. A request already accepted by Discord remains completed;
not-started steps are reported as cancelled. “Partially completed” is not called
rolled back. Failed, stale, cancelled, partial, or reconciliation-required work can
only return to Channels to generate and approve a new plan; it is never replayed
blindly.

## Retry, reconciliation, compensation, and backups

Discord.Net remains the primary rate-limit owner. The Application executor adds at
most three bounded exponential-backoff attempts with jitter only for a known
retryable failure. Permission, validation, stale, missing-target, cancellation, and
Discord rejection failures are not retried. Timeout, network, and server-error
outcomes are treated as uncertain and reconciled before another request.

Creation reconciliation matches server, parent, modeled type, name, and operation
timeframe. Zero matches means known not applied, one means applied, and multiple
matches require manual review. Updates compare exact before/after fingerprints;
deletes compare target presence; bulk reorder compares every captured channel.

Compensation is attempted only when accurately modeled: delete a newly created
resource, restore a captured property/parent/position, bulk order, or exact overwrite.
Deletion has no rollback. A backup can recreate some structure but cannot restore IDs,
messages, links, threads, webhooks, integrations, or every Discord-side association.

Before a high-risk/destructive plan, SQLite stores the relevant server/category/
channel structure, modeled forum metadata where present, exact raw overwrite values,
source bot, explorer sequence, timestamp, and correlation ID. It stores no tokens,
messages, DMs, voice content, authorization data, or member directory. Backup failure
blocks the first Discord request.

## Backup Browser and recreate structure

Backups is a dedicated, virtualized, paged page. It searches backup/server/correlation
IDs and server names; filters bot, server, date, source operation, and compatibility;
and sorts by time, server, or resource count. The catalog displays pin state, schema,
size, source sequence, resource/overwrite counts, live server access, referenced-role
availability, and fully supported, partially supported, unsupported, newer-schema, or
corrupt status. Selecting a row shows modeled channels, types, parents, positions,
overwrites, unsupported fields, missing roles, source identifiers, warnings, and the
exact data recreation cannot recover. Formatted structural JSON is technical data,
not the primary interface.

“Recreate structure” never means undo deletion or restore the original channel. The
user explicitly chooses supported resources, edits proposed names, maps a backed-up
category to a current category or creates its replacement, chooses Uncategorized
where appropriate, and opts into permission overwrites. Role targets may be confirmed
by exact ID, explicitly mapped (including `@everyone`), or skipped; a same-name role is
only labeled as a suggestion. Member-specific overwrites default to Skip. Unresolved
critical mappings block the plan.

Replacement categories are created before children and final positions are reconciled
after Discord returns each new resource ID. Reused categories are fingerprinted by
preflight. Existing-name conflicts, duplicate planned names, missing categories,
unsupported types, channel limits, permission/hierarchy changes, and current voice
capabilities block execution. Three or more replacement channel creations require
the exact text `RECREATE N CHANNELS`.

Replacement resources always receive new Discord IDs. Messages, threads, pins,
message links, webhook identities, invites, message history, external references,
and voice history/content are not recovered. The default partial-failure policy keeps
successful replacements. The alternatives are explicit safe cleanup of newly created
resources or stop for manual review.

## Retention, recovery, reconciliation, and export

Local backup retention defaults to Keep indefinitely and never deletes in the
background. Optional age, newest-per-server, failed/partial preservation, and maximum
storage rules run only as a dry preview. The exact candidate IDs, reasons, and
estimated bytes are shown before confirmation. Pinned backups are always protected.
Confirmed cleanup and individual deletion remove only local SQLite records, create a
local cleanup audit entry, and never call Discord.

The executor journals a safe result after every completed step. At startup,
Pending/Running/Waiting/Cancelling records are never resumed. Compatible plans use
read-only current-state reconciliation when the bot/server is available, then become
completed-after-reconciliation, partial, not-started, unable-to-inspect, or manual
review. No compensating or corrective mutation occurs without a newly previewed and
confirmed plan.

The Operation Center reconciliation section records a timestamp, correlation/step ID,
resolution, safe explanation, and relevant resource IDs. Recording a decision is
local metadata only. History JSON, history CSV, and backup-metadata JSON use versioned
safe models and exclude credentials, authorization, raw Discord payloads, messages,
DMs, voice content, member directories, stack traces, and Windows user paths.

Voice create/edit/recreate validation uses the current modeled boost tier
(`None`, `Tier1`, `Tier2`, or `Tier3`) plus Discord.Net validation. Unknown future
tiers are labeled uncertain instead of claiming a false maximum; Discord invalid
value rejection remains non-retryable. Voice capabilities are rechecked immediately
before execution.

## Guild Members privileged intent

Full member enumeration requires Discord's privileged **Server Members Intent**
(`GuildMembers`). It is disabled locally for all existing and new profiles unless the
user explicitly enables it.

There are four separate concepts:

1. **Developer Portal configuration** authorizes the bot application to request the
   privileged intent.
2. **Local per-bot intent selection** determines whether this application requests it
   for one saved bot. Enabling it requires a warning confirmation and reconnects only
   that bot.
3. **Discord server permissions** determine what the bot can view or potentially
   manage inside a server. They do not enable a gateway intent.
4. **Role hierarchy** determines whether a target is below the bot. Possessing a
   permission alone does not bypass hierarchy or managed-role restrictions.

To enable full members:

1. Open the bot application in the Discord Developer Portal.
2. Open **Bot** and enable **Server Members Intent**.
3. In Discord Control Center, open **Bots**.
4. Choose **Enable members** for that profile and confirm the warning.
5. The application reconnects only that bot with `GuildMembers`.
6. Open **Members** and choose **Refresh members**.

If Discord closes the gateway with code 4014, the bot enters a safe faulted state with
an actionable explanation. There is no custom reconnect loop. Disable the local option
and connect again without `GuildMembers`, or correct the Developer Portal toggle.

When full access is disabled, the existing `Guilds` and `GuildVoiceStates` behavior is
unchanged. The Members page clearly labels limited mode and shows only legitimately
available users, such as the bot and users represented by accessible voice state. It
never calls that partial set a complete member list.

## Member completeness and loading

Each bot/server member snapshot has one explicit state:

- `Limited`: privileged access is disabled.
- `Loading`: a cancellable paginated load is in progress.
- `Partial`: some data is available but completeness cannot be guaranteed.
- `Complete`: the load completed successfully.
- `Cancelled`: loading was cancelled by the user, selection change, disconnect, or
  shutdown.
- `Failed`: a recoverable member load failed.
- `Unavailable`: no useful snapshot exists.

Member pages from Discord.Net are translated and published incrementally in batches.
Discord.Net owns REST rate-limit handling. Bot/server changes use cancellation and
generation guards, IDs suppress duplicates, and stale cache sequences are rejected.
The in-memory member cache is capped at 100,000 members per server; larger guilds are
explicitly reported as partial.

Member records are not written to SQLite. Disconnect and profile removal clear them.

## Permission resolution and comparison

Member permissions apply Discord precedence:

1. base `@everyone` permissions;
2. aggregated assigned-role permissions;
3. Administrator override;
4. channel `@everyone` overwrite;
5. aggregated role overwrites;
6. member-specific overwrite.

Role subjects combine `@everyone` with the selected role. The selected bot uses its
captured role IDs and member overwrite. Text, voice, stage, general, and moderation
results are marked not applicable where appropriate.

Partial member-role data yields **Unknown due to incomplete data**. The application
does not manufacture a confident source explanation.

The reusable role hierarchy preflight service reports Allowed, Denied, or Unknown with
a reason code, required permission, relevant role positions, and completeness. It
checks Manage Roles, target hierarchy, `@everyone`, managed roles, permissions the bot
does not possess, member hierarchy, and server ownership. It executes no action.

## Gateway intents and events

Always enabled:

- `Guilds`
- `GuildVoiceStates`

Enabled only for profiles whose local option is on:

- `GuildMembers`

Never enabled in this phase:

- `GuildPresences`
- `MessageContent`

The adapter handles ready/reconnect/disconnect, guild availability/lifecycle,
channel/role changes, member joins/leaves/updates/download completion, and voice-state
changes. Member and voice event bursts are debounced and applied incrementally.
Subscriptions are paired with explicit asynchronous disposal.

## Performance, privacy, and resilience

- One isolated client and cache per bot profile.
- Immutable Core read models; no Discord.Net objects in Application or WPF.
- Monotonic adapter sequences and stale-event rejection.
- Cancellable member paging and bot/server selection generations.
- Incremental member and voice changes rather than server-wide rebuilds.
- Batched dispatcher notifications, debounced search, and recycling virtualization.
- Bounded member memory and in-process image/permission caches.
- No member-directory persistence.
- No presence, activity/game, message, raw payload, authorization-header, or token
  logging.
- Recoverable page errors and retries do not invoke the global fatal dialog.
- Recoverable operation errors remain in Operation Center and do not invoke the global
  fatal dialog.

## Discord permissions

Read-only explorers still need only the access Discord exposes to the selected bot.
All channel mutations require **Manage Channels** for every target. Copying, creating,
locking, removing, or synchronizing permission overwrites also requires **Manage
Roles** and passes existing role-hierarchy safety checks for non-`@everyone` roles.
Administrator is resolved through the existing permission service. Incomplete or
unknown permission data blocks execution.

The selected official bot account performs every request. Developer Portal intent
configuration is unrelated to these guild permissions.

## Manual test checklist

Use only harmless, clearly named resources in a disposable private server:

1. Connect the test bot and verify Servers, Channels, Members, Roles, Permissions, and
   Voice still load.
2. Create a temporary category, three text channels, and one voice channel through
   separate exact previews.
3. Sequentially rename, edit topic/slow mode, move, and bulk reorder those channels.
4. Clone one text channel and clone the temporary category with selected/all children.
5. Lock then unlock a text channel for `@everyone`; compare raw unrelated overwrite
   bits before and after.
6. Synchronize one child channel with its category and inspect every add/update/remove
   overwrite in the preview.
7. Cancel a harmless multi-step create/rename operation and verify completed,
   not-started, and uncertain counts are honest.
8. Temporarily remove Manage Channels and Manage Roles separately and verify preflight
   rejects the relevant plan before a request.
9. Disconnect/reconnect during harmless work and inspect reconciliation.
10. Delete one temporary ordinary channel, then test category-only deletion and verify
    children become uncategorized.
11. Recreate temporary structure and test explicit children-first category deletion.
12. Verify a backup row predates each destructive result.
13. Open Backups, inspect the Phase 4A deletion backup and its overwrites, and create
    one harmless replacement channel with a modified name; verify its ID is new.
14. Recreate a temporary category with two children; verify category dependency,
    relative ordering, and explicitly selected role mappings.
15. Cancel a multi-step replacement plan and verify the selected keep-successful
    policy is reported honestly.
16. Close during a harmless queued/running operation, restart, and verify startup
    recovery inspects without automatically resuming a Discord mutation.
17. Record a manual reconciliation decision, then search/filter the persisted
    Operation Center timeline.
18. Export history JSON/CSV and backup-metadata JSON; inspect them for prohibited
    private fields.
19. Pin a backup, preview a finite retention policy, and verify the pinned row is not
    selected.
20. Delete an unneeded local backup and verify no Discord resource changes.
21. Create one temporary voice channel at a value supported by the current server tier
    and verify invalid tier values are blocked before execution.
22. Inspect SQLite/logs for tokens, authorization data, raw payloads, message content,
    DMs, member directories, or unsafe exception messages.
23. Verify clean application shutdown terminates queue workers and gateway clients.

## Known limitations

- Full-member behavior cannot be validated without both the Developer Portal toggle
  and a disposable server.
- Presence and activity tracking are intentionally absent.
- Discord may omit optional role icons, global names, join/boost timestamps,
  screening, timeout, stage, or channel metadata.
- The 100,000-member safety cap means larger servers remain explicitly partial.
- Role/member counts are exact only when the member snapshot is complete.
- Gateway cache visibility follows the bot's actual access.
- Discord channel endpoints have no universal application idempotency key; ambiguous
  reconciliation deliberately requires manual review.
- Existing same-name Discord resources are blocked for create/clone because they make
  safe timeout reconciliation ambiguous.
- Unknown or future server tiers cannot be assigned a certain bitrate maximum from the
  current read model; the UI warns and Discord.Net/Discord remain authoritative.
- Category clone supports only ordinary text and voice children in Phase 4A.
- Backup recreation supports only safely modeled category, ordinary text, and voice
  resources. Forum, media, announcement, stage, thread, webhook, invite, and message
  reconstruction remains unsupported.
- Role-name matches are suggestions requiring an explicit user mapping; member
  overwrites are excluded unless a future complete member-identity workflow can prove
  the same ID and the user opts in.
- Startup reconciliation depends on a connected bot and visible current server state.
  Unavailable or multiply matching resources remain manual review.
- Cleanup runs on user command rather than a background timer, so retention can never
  unexpectedly delete an existing backup.
- Recreating structure from backup is partial recovery, not undo deletion.

## Phase 4B status and recommended next phase

Phase 4B provides the backup/replacement, retention, history/export, startup recovery,
manual reconciliation, server-aware voice validation, and per-step durability
described above. A next phase should first deepen recovery ergonomics (side-by-side
Discord match inspection and corrective-plan generation) and operational telemetry.
Member moderation, role writes, messaging, webhooks, auto-role behavior, and voice
connections remain separate future milestones with their own immutable plans and
stricter safety policies.
