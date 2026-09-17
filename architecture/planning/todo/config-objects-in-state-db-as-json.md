# All current config objects mirrored into the state database as JSON

## The problem / motivation

Config — connections, replications, table mappings, scripts — lives exclusively in the git-tracked file
repository today. Nothing about it is queryable from the state database, which is where everything else
(runs, watermarks, the work queue) already lives. The one existing precedent for "a piece of config
mirrored into the state db for fast reads" is narrow and later-triggered than it might sound: the
`Tasks` table's `Enabled`/`Paused` columns, refreshed opportunistically at `TaskRunner` worker startup and
on pause/resume actions — **not** synchronously on every config save, and not read universally either
(`ReplicationsController.Status` still reads `Enabled` live from `ConfigRepository.LoadReplicationTask`,
bypassing its own mirror). `docs/state-database.md`'s own words for why it exists: "an `Enabled`/`Paused`
mirror the scheduler checks fast, without opening the config repo."

This plan is that same idea, generalized: every current config object, mirrored as JSON, kept in sync
with every save — not two booleans refreshed on a schedule of convenience.

No existing motivating consumer is documented for this specifically. The closest real precedent for
*wanting* config queryable outside the filesystem is `architecture/planning/done/state-store-concurrency.md`,
whose resolved design is about **run/metrics** data contending with `TaskRunner` writes ("a dashboard or
metrics query run against the state store stalling"), not config. Worth stating honestly: the "a
reporting/BI read wants config queryable alongside run data, without touching the git working tree, and
this matters more once the state store is `MsSql`/`Postgres`-backed infrastructure someone else can
query" framing below is an extrapolation from that precedent, not a stated need for config found anywhere
in this repo's own docs.

## What "all current config objects" means, precisely

Exactly four independently-saved types exist — confirmed by reading every `Save*`/`Load*`/`Delete*`
method on `ConfigRepository`, the only class with any:

| Type | Keyed by | Save method |
| --- | --- | --- |
| Connection | `name` | `SaveConnection` |
| Script | `name` | `SaveScript` |
| Replication task | `name` | `SaveReplicationTask` |
| Table mapping | `replicationName` + `mapping.Name` | `SaveTableMapping` |

Segmenting strategies, hooks, and script bindings are **not** separately saved objects — they're fields
embedded inside a replication task or table mapping's own config, and ride along inside that object's
JSON automatically; nothing separate to mirror for them.

**Two things this repo tracks that are explicitly out of this inventory**, both worth naming rather than
silently omitting:
- `dbdatasync.config.yaml` (app-level settings) — a real, pre-existing gap even in git history reading
  today (`GitCommitService.GetHistory`'s prefix match never covers it, per phase-035's own note); not
  blocking this plan, but a natural fifth object type to add later.
- Users/sessions/invites — already live in the state database directly; nothing to mirror.

## Design proposal (leaning, not agreed)

1. **One generic table**, tentatively `ConfigObjects` — `ObjectType`, `Key` (the name, or the compound
   `replicationName/mappingName` key), `Json`, `UpdatedAtUtc`, and (see open questions) possibly
   `CommitSha`. A generic type+key+blob shape over four (soon possibly five) near-identical per-type
   tables, so a new config object type later is a new `ObjectType` value, not a migration.
2. **One hook point.** Every one of the four `Save*` methods converges on exactly one place —
   `GitCommitService.CommitChanges`, the single choke point every config write passes through, per its
   own doc comment: "Every config write goes through `CommitChanges` immediately after the file(s) hit
   disk." That is where the mirror write belongs — once, not duplicated by hand across four save paths
   (and, separately, whatever the equivalent delete-side call is — see open questions).
3. **Serialization is less new work than it first looks.** The obvious plan is "reuse `System.Text.Json`
   against the same POCOs the API already returns" (`ConnectionConfig`, `ReplicationTaskConfig`,
   `TableMappingConfig`, `ScriptDefinition`) rather than `YamlConfigSerializer`, which is a genuinely
   separate pipeline (YamlDotNet, with three hand-written converters for the sealed-hierarchy types disk
   YAML can't express natively). The good news, checked directly rather than assumed: `BatchReloadSegment`,
   `DeleteGuard`, and `AfterChangeStrategy` already carry `[JsonPolymorphic]`/`[JsonDerivedType]`
   attributes — `System.Text.Json`'s own polymorphism support, already in place for the API's existing
   JSON responses. No new converter work is needed for the JSON side; the hand-written converters that
   exist today are YAML-only.
4. **This is a mirror, not a history.** The user's own framing is "all *current* config objects" — leaning
   firmly toward overwrite-in-place (one row per object, replaced on every save), not a row per commit.
   Keeping this a pure current-state mirror keeps it cleanly out of `phase-035-config-history-diff-and-revert.md`'s
   own, still-unstarted scope, rather than half-duplicating it. Whether a JSON snapshot per commit could
   later become part of *that* phase's own storage is a real, related question — explicitly not this
   plan's to answer.
5. **Engine-neutral by construction.** A large-text column is exactly what `{{text}}` already tokenizes
   to per state engine (`Migrations.cs`'s existing convention) — no new per-engine mechanism, and the
   table works identically on SQLite/MsSql/Postgres the same way every other state table already does.
6. **Respects the single-writer architecture for free.** Only the API process ever calls a `Save*`/`Delete*`
   method — the SPA and CLI both mutate config exclusively through the API's own controllers, and
   `TaskRunner` never does (it *reads* config directly off the shared filesystem, but never writes it).
   So a mirror-write hooked at `GitCommitService.CommitChanges` only ever fires inside the one process
   already allowed to write the state database — worth confirming directly before building, not merely
   assumed here.
7. **No new secrets exposure.** Confirmed directly: no config object embeds a secret value anywhere.
   `ConnectionConfig.CredentialSecretRef` is a reference into the OS-native secret store, never a
   plaintext credential, and `ConfigValidation.RejectEmbeddedCredential` actively refuses a save whose
   `ConnectionString` looks credential-shaped specifically because config is already git-committed and
   diffed in the UI. A JSON mirror of these same objects carries exactly the same (already-accepted)
   exposure as the git-tracked YAML already does — no more. One pre-existing, unrelated gap worth naming
   for completeness: `ConnectionConfig.Properties` (a free-form dictionary) isn't covered by that same
   credential check — not a new risk this plan introduces, just an existing one it would carry forward
   unchanged.

## What this does not do

- **Does not replace git as config's source of truth.** The git commit is still what actually happened;
  this mirror is a read-optimization, and is only ever as current as the last successful save.
- **Does not build config diff or revert.** That is `phase-035`'s own, separate, still-unstarted scope.
  This plan's storage *could* end up useful to that phase later, but building toward that isn't this
  plan's goal.
- **Does not cover `dbdatasync.config.yaml`** in a first version — a real gap, named above, deferred
  rather than silently dropped.
- **Does not change what's considered sensitive.** See point 7 above — nothing new to protect that git
  doesn't already carry.

## Open questions (UNDECIDED)

- **One generic table vs. one per object type.** Leaning generic for extensibility (a new object type is
  a new `ObjectType` value, not a migration), but a per-type table would let a common lookup (e.g. "every
  enabled replication") filter on a real typed column instead of parsing JSON per row. Real tension,
  not resolved here.
- **Track `CommitSha` alongside the JSON?** Leaning yes — cheap, and it lets a reader confirm exactly
  which commit a given mirror row reflects, which is useful for debugging drift between git and the
  mirror and would make this table a softer on-ramp if `phase-035` ever wants to build on it.
- **Delete handling: hard-delete the mirror row, or tombstone it?** Leaning hard-delete, consistent with
  "current state only" — a tombstone is a step toward history tracking this plan deliberately isn't
  taking.
- **No concrete first consumer is identified yet.** Before this becomes an implementation phase, it's
  worth naming at least one real reader — a specific dashboard/reporting need, a specific SPA screen that
  currently pays for N sequential config loads, or a firmer connection to `phase-035` — so this doesn't
  ship as a table nothing reads. Recommend that discussion happens before, not after, an implementation
  phase doc gets written.
