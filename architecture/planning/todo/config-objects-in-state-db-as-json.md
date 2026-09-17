# All current config objects mirrored into the state database as JSON

## The problem / motivation

Config — connections, replications, table mappings, scripts — lives exclusively in the git-tracked file
repository today. Nothing about it is queryable from the state database, which is where everything else
(runs, watermarks, the work queue) already lives. The one existing precedent for "a piece of config
mirrored into the state db for fast reads" is narrow and later-triggered than it might sound: the
`Tasks` table's `Enabled`/`Paused` columns, refreshed opportunistically at `TaskRunner` worker startup and
on pause/resume actions — **not** synchronously on every config save. `docs/state-database.md`'s own
words for why it exists: "an `Enabled`/`Paused` mirror the scheduler checks fast, without opening the
config repo."

This plan is that same idea, generalized — but now with two concrete, named consumers (settled
2026-09-16), which sharpen the design considerably from a vague "read optimization":

1. **An admin with a configured remote (`MsSql`/`Postgres`) state database wants to analyze
   configuration** — query it directly, cross-referenced with run/state data, without touching the git
   working tree at all. This is the "reporting/BI read" case: something like "every replication whose
   writer is `Scd2`," or "every connection using driver X," answered with SQL against the state store
   the same infrastructure team that already operates that database can already reach.
2. **An admin provisioning a *new* DbDataSync server wants to resume from the state database alone** —
   copying just the SQLite file (or pointing at the same remote `MsSql`/`Postgres` database), with **no
   copy of the git config repository at all**, and have the new instance reconstruct its
   connections/replications/mappings/scripts from what's in the state db.

Scenario 2 changes what this feature actually is. It is no longer purely a read-optimization mirror that
git could keep being the sole source of truth for — it has to be a **faithful enough backup** that a
fresh deployment can be rebuilt from it alone. That doesn't make it the *primary* source of truth during
ordinary operation (git still is, and still gets written first, on every save) — but it does mean the
mirror has to be complete enough to actually restore from, not just "good enough for a dashboard."

## What "all current config objects" means, precisely

Exactly four independently-saved types exist — confirmed by reading every `Save*`/`Load*`/`Delete*`
method on `ConfigRepository`, the only class with any:

| Type | Keyed by | Save method | Mirror this whole object |
| --- | --- | --- | --- |
| Connection | `name` | `SaveConnection` | `ConnectionConfig` |
| Script | `name` | `SaveScript` | `ScriptDefinition` (**not** just `ScriptConfig`) |
| Replication task | `name` | `SaveReplicationTask` | `ReplicationTaskConfig` |
| Table mapping | `replicationName` + `mapping.Name` | `SaveTableMapping` | `TableMappingConfig` |

**Scripts need the code, not just the manifest, or scenario 2 can't actually restore one.** Confirmed
directly: `ScriptConfig` (the YAML manifest — kind, language, entry type, parameters) and the script's
actual source text are two separate files on disk (`config/scripts/<name>.yaml` + `.cs`/`.sql`), but the
*in-memory* type the API already works with, `ScriptDefinition { Manifest: ScriptConfig, Code: string }`,
already combines them as one plain object — `ConfigRepository.LoadScript` already returns exactly this.
Mirroring `ScriptDefinition` (not `ScriptConfig`) costs nothing extra and is what makes a restored script
actually runnable rather than an empty shell with no code.

Segmenting strategies, hooks, and script bindings are **not** separately saved objects — they're fields
embedded inside a replication task or table mapping's own config, and ride along inside that object's
JSON automatically; nothing separate to mirror for them.

**Two things this repo tracks that are explicitly out of this inventory**, both worth naming rather than
silently omitting:
- `dbdatasync.config.yaml` (app-level settings) — a real, pre-existing gap even in git history reading
  today. Less critical for scenario 2 specifically (a fresh deployment's app-level settings are usually
  provided fresh, per-environment, via `DbDataSync:*` environment variables rather than expected to
  travel with the data) but a natural fifth object type to add later.
- Users/sessions/invites — already live in the state database directly; nothing to mirror.

## Design proposal (leaning, not agreed)

1. **One generic table**, tentatively `ConfigObjects` — `ObjectType`, `Key` (the name, or the compound
   `replicationName/mappingName` key), `Json`, `UpdatedAtUtc`, `CommitSha`. A generic type+key+blob shape
   over four (soon possibly five) near-identical per-type tables, so a new config object type later is a
   new `ObjectType` value, not a migration. `CommitSha` is no longer optional given scenario 1 — an
   analysis query correlating the mirror against anything else that references a commit (or simply
   confirming "is this row current") needs it, so it's included outright rather than left as an open
   question.
2. **One hook point.** Every one of the four `Save*` methods converges on exactly one place —
   `GitCommitService.CommitChanges`, the single choke point every config write passes through, per its
   own doc comment: "Every config write goes through `CommitChanges` immediately after the file(s) hit
   disk." That is where the mirror write belongs — once, not duplicated by hand across four save paths
   (and, separately, the equivalent delete-side call — see open questions).
3. **Serialization is less new work than it first looks.** Reuse `System.Text.Json` against the same
   POCOs the API already returns, rather than `YamlConfigSerializer` (a genuinely separate pipeline).
   Checked directly, not assumed: `BatchReloadSegment`, `DeleteGuard`, and `AfterChangeStrategy` already
   carry `[JsonPolymorphic]`/`[JsonDerivedType]` attributes for `System.Text.Json`'s own polymorphism
   support, already exercised by the API's existing JSON responses. No new converter work needed.
4. **This is a mirror, not a history — one row per object, overwritten on every save.** Keeping this a
   pure current-state mirror keeps it cleanly out of `phase-035-config-history-diff-and-revert.md`'s own,
   still-unstarted scope. Scenario 2 only ever needs the *current* state of each object to rebuild a
   fresh deployment anyway — it has no use for prior versions. Whether a JSON snapshot per commit could
   later become part of `phase-035`'s own storage is a real, related question, explicitly not this plan's
   to answer.
5. **Engine-neutral by construction.** A large-text column is exactly what `{{text}}` already tokenizes
   to per state engine — no new per-engine mechanism.
6. **Respects the single-writer architecture for free.** Only the API process ever calls a
   `Save*`/`Delete*` method, so a mirror-write hooked at `GitCommitService.CommitChanges` only ever fires
   inside the one process already allowed to write the state database — worth confirming directly before
   building, not merely assumed here.
7. **No new secrets exposure, and a real, named gap for scenario 2.** No config object embeds a secret
   value anywhere — `ConnectionConfig.CredentialSecretRef` is a reference into the OS-native secret
   store, never a plaintext credential. A JSON mirror of these objects carries exactly the same exposure
   the git-tracked YAML already does — no more. **But this means restoring a connection from the mirror
   recovers everything except its actual password.** The secret store is a separate, OS-native mechanism
   this feature does not and should not try to also back up. This is not a new limitation this plan
   introduces — a git-only backup already has exactly the same gap — but scenario 2 makes it load-bearing
   in a way it wasn't before, so the restore flow (below) has to surface it plainly rather than let an
   admin discover it only when the first replication run fails to connect.

## Restoring from the state database (scenario 2)

This is new work beyond "write the mirror" — a read path that only exists for provisioning, and it
deserves its own design rather than being folded silently into "someone will read the table eventually."

**Real precedent for the shape of this already exists**: `dbdatasync invite` already opens the state
database *directly*, bypassing the API entirely, specifically because the situation it exists for — an
admin locked out, nobody can sign in — has no running, reachable API to call. Its own doc comment: "it
opens the state database directly rather than calling the API, because... an endpoint that needs a
session is no help there." It resolves `StateEngine`/`StateConnectionString` the same way `serve` does
(`dbdatasync.config.yaml`, then `DbDataSync__*` environment variables), so it already reaches a
`MsSql`/`Postgres`-backed store exactly as well as a SQLite one. A restore command is the same shape of
problem — provisioning a new server means there may be no config repository to point an API at yet, and
no reason to stand up a full `serve` process just to run a one-time bootstrap.

Leaning proposal: a new CLI command (name TBD — `dbdatasync config restore-from-state`, or folded into
`dbdatasync setup`'s existing "fresh install or an existing one" TUI as a third path it detects and
offers) that:

1. Opens the state database directly, the same way `invite` does.
2. Reads every row of `ConfigObjects`.
3. Writes each object back out through the *same* validated path config already uses to write to disk —
   `ConfigRepository`'s own `Save*` methods, not a bespoke file-writer, so a restored object gets exactly
   the same validation, atomic-write, and serialization behavior a normal save gets. (This also means it
   naturally re-establishes the mirror row it just read from, harmlessly — restoring from the mirror and
   writing through the mirror's own hook point are the same operation.)
4. Restores in dependency order — connections and scripts first (nothing depends on them existing but
   they're depended on), then replication tasks, then table mappings (which need their parent replication
   task's directory to exist first).
5. Produces one git commit (or a small, clearly-labeled sequence) recording the restore as what it is —
   **prior git history is not, and cannot be, recovered this way.** A restored deployment's Version
   Control tab starts from the restore point; everything before it is gone unless the old git repository
   itself is *also* available (in which case, restoring from it directly is simpler than this whole
   mechanism and this feature isn't what's needed). This is an honest, load-bearing limitation to state
   plainly in the command's own output, not just in this doc.
6. Ends with a clear, itemized report of what needs attention before the deployment is actually usable —
   at minimum, every restored connection whose secret isn't already present in this machine's own secret
   store (which, on a genuinely new server, is none of them) needs its password re-entered before that
   connection will work.

## What this does not do

- **Does not replace git as config's source of truth during ordinary operation.** Every save still
  writes to git first; the mirror is refreshed from that write, never the other way around, except
  during the one-time restore flow above.
- **Does not restore secrets.** Connection passwords live in the OS-native secret store, never in config,
  never in the mirror — a restored deployment needs them re-entered. Named prominently, not a footnote.
- **Does not restore prior git history.** A restore reconstructs *current* state only; the audit trail
  before the restore point is not recoverable through this mechanism.
- **Does not build config diff or revert.** That is `phase-035`'s own, separate, still-unstarted scope.
- **Does not cover `dbdatasync.config.yaml`** in a first version — named above, deferred rather than
  silently dropped.

## Open questions (UNDECIDED)

- **One generic table vs. one per object type.** Leaning generic for extensibility, but a per-type table
  would let a common analysis query (scenario 1) filter on a real typed column instead of parsing JSON
  per row — genuinely more relevant now that "analysis" is a named use case, not resolved here.
- **Delete handling: hard-delete the mirror row, or tombstone it?** Leaning hard-delete, consistent with
  "current state only."
- **Restore command name and entry point** — a new top-level CLI command, or a path inside `dbdatasync
  setup`'s existing TUI (which already distinguishes "fresh install" from "existing one," but doesn't
  today check whether an existing, populated state database is sitting there with no config repo beside
  it — a real gap this feature would need to add detection for either way).
- **Restore conflict handling.** What happens if the target directory already has *some* config in it —
  refuse outright (restore is for a genuinely empty target only), or offer to merge/overwrite object by
  object? Leaning toward refusing on anything but an empty config root for a first version — merge
  semantics are a much larger design question or their own.
- **Should a restore be previewable (`--dry-run`, listing what would be written) before it commits
  anything to disk?** Leaning yes, given how consequential a botched restore during provisioning would
  be, but not designed here.
