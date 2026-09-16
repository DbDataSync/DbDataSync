# The state database

[DbDataSync](../README.md) · [Install](install.md) · [Configuration](configuration.md) · [Getting started](getting-started.md) · [Replication concepts](replication-concepts.md) · [Drivers and libraries](drivers-and-libraries.md) · **State database** · [Building from source](development.md)

DbDataSync keeps two entirely separate kinds of data: your **config repository** (replications, table
mappings, connections — a git-tracked directory, see [Configuration](configuration.md)) and its own
**state database** — run history, the work queue, watermarks, users and sessions. This page is about
the second one. Nothing here is a replication's source or target; it's DbDataSync's own bookkeeping
about what it has done and is about to do.

## What it stores

One schema, 16 tables, created (and kept current — see "Migrations" below) on first open regardless of
which engine backs it:

| Table | What it's for |
| --- | --- |
| `Tasks` | One row per configured replication — an `Enabled`/`Paused` mirror the scheduler checks fast, without opening the config repo |
| `TaskRuns` | One row per unit of work — a mapping's `Primary` pass, or one `BulkLoad` segment — with status, row counts, timing, and error detail |
| `ChangeWatermarks` | The current read position per mapping, plus its `ReadIntent`/`ReadHold` (see [Replication concepts](replication-concepts.md)) |
| `WorkQueue` | The durable, cross-process work queue — the API enqueues, `TaskRunner` workers claim and drain |
| `RunLocks` | One row per in-flight `(task, kind, mapping)` — the lock a unit of work holds while it runs |
| `Logs` | Log lines per run |
| `BulkLoadBatches` | Groups a bulk load's segment runs so progress can be shown as one whole |
| `VerificationResults` | Indexes a verification run's parquet result file (the comparison itself isn't stored here) |
| `ChangeCheckHistory` | The polling gate's audit trail — one row per source group per scheduler tick, also backing reader-lag reporting |
| `Notifications` | The global, append-only notification feed |
| `NotificationReadState` | One row per user: the highest notification id they've acknowledged |
| `PauseEvents` | Append-only audit trail of every pause/resume action, at the replication or table-mapping grain |
| `Users` | Accounts — display name, email (for git commit attribution), role, enabled flag |
| `UserCredentials` | One row per sign-in method per user (a Windows SID, a passkey) |
| `Sessions` | Server-side sessions — disabling a user takes effect on their next request, not at token expiry |
| `Invites` | One-time bearer codes for creating a user or adding a credential to one |

## Choosing an engine

SQLite by default — a file at `StateDbPath` (`<RepoRoot>/state.db`), nothing to configure. Set
`StateEngine` to `MsSql` or `Postgres` instead to run it on infrastructure you already operate and back
up, with `StateConnectionString` saying how to reach it (never with a password in it — see
[Configuration](configuration.md#secrets)). Full flag reference, including the state store's own
retention keys, is in [Configuration](configuration.md#state-store-engine-stateenginestateconnectionstring).

Two things worth being explicit about here, beyond what Configuration already covers:

- **An unrecognized `StateEngine` value refuses to start** — `Unknown state engine '<value>'. Built-in
  engines are 'Sqlite', 'MsSql' and 'Postgres'...` — rather than silently landing on SQLite.
- **`MsSql`/`Postgres` resolve their connection the same way a driver does** — through a
  [library](drivers-and-libraries.md), not a package this build carries. `DbDataSync.State`'s own project
  file references no `Microsoft.Data.SqlClient`/`Npgsql` package at all; it resolves
  `microsoft-data-sqlclient`/`npgsql` through `LibraryRegistry` at connect time, exactly like the
  built-in `MsSql`/`Postgres` replication drivers do. Run
  `dbdatasync config library install microsoft-data-sqlclient` (or `npgsql`) once; a `StateEngine` set
  to either without its library installed fails at startup naming that exact command. See
  [Drivers and libraries](drivers-and-libraries.md) for the general mechanism.

**No cross-engine migration exists.** Pointing an existing deployment at a different `StateEngine`
starts an empty store — it does not move anything. Choose once, at stand-up.

## Migrations

Schema changes are a numbered list of DDL scripts, applied incrementally — SQLite tracks the applied
count in `PRAGMA user_version`; SQL Server and Postgres use a small `SchemaVersion` table, since neither
has SQLite's built-in counter. **Migrations run on every open, not just once** — a deliberate,
self-healing choice, not merely an unoptimized one — so there's nothing to run by hand after an upgrade:
stop the process, update it, start it again.

All three engines are meant to behave identically. The DDL is written once, as one script per migration
with small engine-specific tokens (how an auto-assigned key is declared, what bounded/unbounded text is
called, what an integer is called) substituted per engine; anything that differs *structurally* — upsert
syntax, row-limiting, identity handling — lives in a separate per-engine implementation rather than a
token, on the same principle [Drivers and libraries](drivers-and-libraries.md#descriptor-drivers)
documents for a descriptor's own dialect.

## Retention

| key | default | prunes |
| --- | --- | --- |
| `DbDataSync:RunRetentionDays` | `90` | finished runs older than this (`0` = keep forever) |
| `DbDataSync:RunRetentionMaxPerMapping` | `1000` | most recent N finished runs kept, per table mapping (`0` = no cap) |
| `DbDataSync:RunPruningIntervalMinutes` | `60` | how often the sweep runs |
| `DbDataSync:ChangeCheckRetentionDays` | `7` | how long `ChangeCheckHistory` rows live — this table writes constantly (one row per source group per scheduler tick) regardless of replication activity, so it has its own, much shorter window |

One sweep, run by the API process itself (never `TaskRunner` — see "Single writer" below), prunes
`TaskRuns` (and its `Logs`), `ChangeCheckHistory`, and `Notifications` together — a notification can't
outlive the run it's about. It runs once immediately at startup, then on the configured interval, so an
API that was down for a while doesn't wait an hour to catch up. **A run that hasn't finished is never
pruned, regardless of age.**

One known gap: a verification run's `TaskRuns` row is pruned on the same schedule as everything else,
but its parquet result file and `VerificationResults` index entry are not — nothing currently cleans
those up.

## One process owns the store

**`DbDataSync.TaskRunner` never opens the state database directly, even for `Sqlite`.** It reaches the
API's own state over a loopback-only HTTP endpoint (`127.0.0.1` bound, defended again by a per-process
token passed through the environment, never the command line). This is deliberate, not a missing
optimization: with several workers spawned per install, having every one of them write the same SQLite
file directly was a real design that needed retries and lock timeouts to survive; the actual fix was
making it structurally impossible rather than tuned to be rare. If the API becomes unreachable, a
worker retries for a grace period, then spills its outcomes to a local JSON-Lines journal and exits
cleanly — the API replays that journal (idempotently) the next time it starts.

This single-writer design is why there's no supported way to run two API instances against one shared
`MsSql`/`Postgres`-backed store today — pointing a server engine at "infrastructure you already operate
and back up" is about deployment preference, not about enabling more than one API process to share it.

## Operating it

There's no dedicated backup/restore/migrate command. For the SQLite default, the state database is
just a file beside your config repository — [Install](install.md)'s container section notes that one
volume covering both is a complete backup of everything that isn't the image itself. For a server-engine
deployment, back it up the way you already back up that database; DbDataSync doesn't do this for you.

What does exist:

- **`dbdatasync config check`** opens the state database as part of its readiness checks (which also
  runs the full migration set) and reports `State store: OK — <engine> — schema current`, or a `FAIL`
  naming the fix — a writable-directory check for SQLite, or a connection-string/secret check for
  `MsSql`/`Postgres`.
- **`dbdatasync invite`** opens the state database directly, the one deliberate exception to "only the
  API touches this" — its whole purpose is recovering access when nobody can sign in, so it can't depend
  on a running, reachable API.

## Next: Building from source

If you're modifying DbDataSync itself rather than configuring a deployment of it, see
[Building from source](development.md).
