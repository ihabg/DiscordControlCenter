# Architecture

## Dependency direction

`App` composes the process and depends on `Application`, `Infrastructure`, and
`Discord`. `Application` coordinates use cases against contracts and domain models
from `Core`. `Infrastructure` implements persistence and protection contracts.
`Discord` implements authenticated REST validation and gateway client contracts.
Neither WPF ViewModels nor views reference Discord.Net.

## Runtime model

Each bot profile gets an isolated `DiscordSocketClient` owned by
`BotConnectionManager`. A per-bot semaphore serializes lifecycle transitions.
Connect-all is capped at three concurrent connection attempts. Client events produce
immutable `BotConnectionSnapshot` values, which are marshalled to the WPF dispatcher.
All subscriptions are paired with explicit unsubscription and asynchronous disposal.

Discord.Net owns its supported gateway reconnect behavior. The application reports
reconnecting state but does not create an additional uncontrolled reconnect loop.
The planned persistent voice watchdog will add a single cancellable worker per
bot/channel with exponential backoff and permission-aware terminal failures.

## Data and secrets

SQLite is opened in WAL mode and initialized through an idempotent, version-recorded
schema. Repositories open short-lived pooled connections and use parameterized SQL.

Bot tokens are authenticated before persistence, protected with Windows DPAPI using
`CurrentUser` scope and application entropy, then stored only as encrypted blobs.
Temporary UTF-8 buffers are zeroed. The UI uses a fixed mask, and structured logs
record identifiers and exception types rather than credentials or exception messages.

## Growth points

- Add migrations as ordered version steps rather than changing version 1 in place.
- Add application commands for server/channel operations and reusable preview models.
- Add a bounded bulk-operation channel with per-action results and rate-limit-safe workers.
- Add the persistent voice watchdog behind an `IVoiceConnectionService`.
- Add replace-token, profile enable/disable, settings, and export-safe backup workflows.
