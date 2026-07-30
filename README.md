# Discord Control Center

A Windows WPF control center for multiple official Discord bot accounts. The current
milestone is a production-quality, read-only Discord observability surface:

- secure, isolated bot profiles and gateway runtimes;
- Server and Channel Explorers;
- limited or full Members Explorer;
- Roles Explorer and hierarchy safety explanations;
- member/role/channel permission simulation;
- live Voice-State Inspector;
- compact gateway and cache diagnostics.

The application performs no Discord mutation, messaging, moderation, role assignment,
or voice connection operation.

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

## Discord permissions

The bot needs normal access to each server and channel it should inspect. No Discord
permission is required merely to use the local explorers beyond what Discord exposes
to that bot. Permissions such as Manage Roles, Moderate Members, Move Members, or
Manage Nicknames are displayed as read-only capability diagnostics. This phase never
uses them to perform an operation.

## Manual test checklist

Use only a disposable private test server and test accounts:

1. Verify the existing Server and Channel Explorers.
2. Confirm role order, `@everyone` placement, selected-bot highest role, and
   manageability explanations.
3. Confirm Members clearly reports limited mode with the local option disabled.
4. Enable Server Members Intent in the Developer Portal.
5. Enable members locally, confirm only that bot reconnects, and load the complete
   disposable-server member list.
6. Join with a test account and change its nickname and roles.
7. Move the test account into and between voice/stage channels; test mute, deafen,
   video, streaming, suppression, and request-to-speak where practical.
8. Remove the test account and confirm selected member/voice state clears safely.
9. Compare bot/member/role permissions against Discord.
10. Disable members locally and reconnect without `GuildMembers`.
11. If a second bot is configured, verify its runtime and selections are unaffected.
12. Review local logs and SQLite: no token, authorization header, raw payload, message
    content, or member directory should appear.
13. Confirm the application modified no Discord resource.

## Known limitations

- Full-member behavior cannot be validated without both the Developer Portal toggle
  and a disposable server.
- Presence and activity tracking are intentionally absent.
- Discord may omit optional role icons, global names, join/boost timestamps,
  screening, timeout, stage, or channel metadata.
- The 100,000-member safety cap means larger servers remain explicitly partial.
- Role/member counts are exact only when the member snapshot is complete.
- Gateway cache visibility follows the bot's actual access.
- All Phase 3 capability and hierarchy results are informational.

## Phase 4 recommendation

Phase 4 should preserve the hierarchy preflight service as a mandatory gate and add a
previewable, cancellable, rate-limit-aware write engine. Start with narrowly scoped
channel operations, explicit confirmation, audit correlation, idempotency, and safe
rollback/partial-failure reporting. Do not combine that engine with the read-model
cache or bypass Discord hierarchy and permission checks.
