# Phase 74 — Watermark keys: quoted identifiers, keyed by mapping

**Status**: Complete.
**Plan reference**: `architecture/planning/done/watermark-key-and-lightweight-polling.md`

## The two problems, together

`WatermarkKey.Build` (used by `RunExecutor`, `PreviewService`, `ResyncService`, `JournalRecovery`)
currently produces `{ConnectionName}/{Database}/{Schema}.{Table}` by raw string interpolation, and
`ChangeWatermarks` is keyed on `(TaskName, SourceTable)` — the table, not the mapping that's reading it.
Two distinct bugs follow:

1. **Ambiguous key.** A schema literally named `a.b` with table `c` produces the same string as schema
   `a` with table `b.c`. Theoretical today, not fixable by reformatting alone without a migration.
2. **Cross-mapping collision.** Two table mappings in the same task pointing at the same source table
   share one `ChangeWatermarks` row regardless of which reader (or which reader *options*) each uses.
   Confirmed two ways this actually corrupts state: different reader kinds writing incompatible watermark
   formats into the same row, and `WatermarkReader`'s per-mapping `watermarkColumn` option silently
   diverging under the same key with no reader kind mismatch to signal it.

Both are fixed by changing what the key *is*, so they're one migration, one resync cost, not two.

## What to build

### `WatermarkKey`

Stop building the qualified table name by string interpolation. Use the source's own `SqlDialect`
(`QuoteIdentifier`/the schema-qualifying helper at `src/DataSync.Core/Sql/SqlDialect.cs:35,47`) so the
schema/table portion is unambiguous by construction — a quoted identifier can't contain its own closing
quote unescaped. `WatermarkKey.Build` needs a `SqlDialect` parameter now; check every call site
(`RunExecutor.cs:516`, `PreviewService.cs` — already resolves `sourceDialect` nearby, `ResyncService.cs`,
`JournalRecovery.cs`) for whether the right dialect instance is already in scope or needs resolving.

### `ChangeWatermarks`

Add `MappingName` to the table and its primary key: `(TaskName, MappingName, SourceTable)` — or drop
`SourceTable` from the key entirely if nothing besides the primary key ever needs to look a row up by
table alone (check `GetWatermark`/`SetWatermark`/`ClearWatermark`'s callers before deciding; keeping
`SourceTable` as a plain informational column rather than part of the key is fine either way, whichever
reads more simply). `SetWatermark`/`GetWatermark`/`ClearWatermark` all gain a `mappingName` parameter,
threaded through from every call site — `RunExecutor` already has `mapping.Name` in scope at each of its
three call sites; `PreviewService` and `ResyncService` need it added to what's already passed around.

### Migration cost

No backfill is possible or attempted — the whole point is that the old key conflated things that need
to stay separate, and there's no way to know from an old row alone which mapping it belonged to if more
than one shared it. Every replication using CDC, Change Tracking, or the generic Watermark reader loses
its stored position on upgrade and reads from scratch on its next pass. State this plainly in the
migration and in the retrospective — it's the same cost the original `WatermarkKey` doc comment flagged
as "worth fixing... if anything ever makes it more than theoretical," now being paid because issue 2 made
it concretely worth doing at the same time rather than twice.

## What this phase should not do

- The lightweight polling gate (phase 75) — independent, additive, doesn't depend on this key shape.
- Anything about `TriggerAuditReader`'s shadow tables, which aren't affected by this key change.

## How to verify

- A test with two mappings on the same source table, one Change Tracking and one generic Watermark,
  asserting each keeps its own position across passes (this is the regression test for the bug that
  motivated the phase — write it to fail against the old key shape first, the same discipline phase 72
  and 73 used).
- A test with two mappings sharing the same reader and table but different `watermarkColumn` values,
  same assertion.
- `WatermarkKey`'s quoting: a schema/table pair chosen so the old ambiguous format would have collided,
  asserting the new keys differ.
- Full suite green (`Category!=Integration`, `Category=Integration`), `tsc -b`/SPA build clean if
  anything in the SPA reads a watermark key shape directly (check `types.ts` and `RunsPanel`/preview
  code for any assumption about the old format before assuming there's nothing to touch).

---

# Outcome

## What was built

### The key, in two parts, in one migration

`ChangeWatermarks` is now keyed on `(TaskName, MappingName, SourceTable)`, and `WatermarkKey.Build`
spells the `SourceTable` portion through the source's own `SqlDialect` rather than by interpolating
dots. The table is dropped and recreated rather than altered: the change is to the primary key, which
none of the three engines can alter in place, and there is nothing in the table worth carrying across.

`SourceTable` **stays in the key**, which the phase doc left open. The caller audit it asked for found
nothing that looks a row up by table alone — every one of `GetWatermark`/`SetWatermark`/`ClearWatermark`'s
callers has the mapping in scope at the call, and `RunExecutor`, `PreviewService` and `ResyncService` all
resolve their source through `mapping.Sources[0]` — so `(TaskName, MappingName)` alone would have worked.
It was kept for a reason the audit surfaced rather than settled: a mapping repointed at a different
source table would otherwise resume from a position belonging to a table it no longer reads. That is the
same class of bug as the one this phase exists to fix, and the column costs nothing to leave where it is.

Keeping it also keeps the quoting fix load-bearing. With the table out of the key, `WatermarkKey`'s
spelling would have become decoration on an audit column, and the migration would have been paid for one
fix rather than two.

### What the quoting covers, and what it does not

`Build` takes a `SqlDialect` and renders `{ConnectionName}/{QuoteIdentifier(Database)}/{QualifyTable(Schema, Table)}`.
The database is quoted as well as the schema and table, which the phase doc did not ask for: it is a SQL
identifier too, and a slash inside one keyed identically to the separator before it — the same ambiguity,
one segment to the left, and free to close while the migration was already being paid for.

The connection name is left bare. It is a DataSync config name, not an identifier in any engine, and
there is no dialect that could meaningfully quote it.

### Threading the dialect to four places

- **`RunExecutor`** — `ResolveDialect(sourceDriver)` at the key's own line. The pass resolves
  `sourceDialect` about fifty lines later for the statement builders; hoisting that up would have moved
  a variable across a block boundary to save a call that returns a singleton.
- **`PreviewService`** — `sourceDialect` was already in scope, as the phase doc predicted.
- **`JournalRecovery`** — needed none. The phase doc lists it as a `Build` call site; it is not one. It
  replays a key that a runner already built, and its only change is the mapping name (below).
- **`ResyncService`** — had no dialect and no connection factory, and the fix has a wrinkle worth
  recording. It now injects `DriverRegistry` and reads the source connection's *configuration* for its
  driver type, rather than opening the connection. Opening one would have made a resync impossible for
  an unreachable source, which is a plausible way to have arrived at a resync in the first place.

  That still introduced a dependency on the connection file existing, where before the service read only
  the replication and the mapping — and it broke `ResyncTests`, whose fixture had never needed a
  connection. Both halves were fixed: the connection load moved inside the existing
  `catch (FileNotFoundException) → NotFound`, so a resync for a replication whose connection is gone
  reports not-found rather than 500ing, and the fixture now saves a real connection it never opens.

### `MappingName` through the state boundary

`IRunnerState.GetWatermark`/`SetWatermark`, `LocalRunnerState`, `ChangeWatermarkStore`'s three methods,
the `GET /watermark` query string and `SetWatermarkRequest` all carry it now.

`SetWatermarkRequest.MappingName` is declared `string? MappingName = null`, last, on the idiom this
record's neighbours already use for `FailureKind`, `Timing` and phase 71's watermark pair: an entry
journalled before the field existed still has to deserialize. Recovery **skips** such an entry with a
warning rather than replaying it under an invented mapping — replaying it would hand some mapping a
position that is not its own, which is the bug, and skipping costs exactly the re-read the migration
already cost. The endpoint refuses the same request for the same reason, rather than writing a row keyed
on a sentinel that nothing will ever read.

## How it was verified

- `ChangeWatermarkStoreTests.TwoMappingsOnTheSameSourceTable_KeepSeparatePositions` and
  `ClearWatermark_LeavesTheOtherMappingOnTheSameTableAlone` — the second because a resync of one mapping
  used to put every mapping on that table back to the beginning, which is the same shared row doing
  damage in the delete direction.
- `WatermarkKeyTests.Build_DistinguishesADottedSchemaFromADottedTable`, on both a bracket-quoting and a
  double-quoting dialect; `Build_DistinguishesASlashInTheDatabaseFromTheSeparatorBeforeIt`; and
  `Build_EscapesAClosingDelimiterInsideAName`, which is the one that makes "unambiguous by construction"
  a property rather than a hope — a table called `Or]ders` cannot end its own bracket.
- `RunExecutorIntegrationTests.TwoMappingsOnOneSourceTable_WithDifferentReaders_KeepTheirOwnPositions`
  and `..._WithDifferentWatermarkColumns_KeepTheirOwnPositions` — the two shapes the plan confirmed
  corrupt state, through the real worker rather than through store calls.

  **Both were run against the old key shape first and both fail there**, with precisely the symptoms the
  plan described: the Change Tracking mapping ends up holding `501`, a row id it cannot read as a
  version, and `by-id` resumes from `9001`, a position measured along a column it never reads. The row
  ids are deliberately far from any plausible change-tracking version so the two positions cannot agree
  by coincidence.

  Each drained run is asserted `Succeeded` inside the drain helper. Without that, a pass that failed for
  an unrelated reason writes no watermark, and `null` reads as "the two mappings kept separate
  positions" — the test would have passed for the worst possible reason. It caught a real setup error
  while these were being written: this fixture does not auto-provision targets.
- Cross-engine coverage came free, as with phase 73: `CrossEngineStateTests`'s 39 integration tests run
  the whole migration set against SQLite, Postgres and SQL Server and passed first time, including the
  `DROP TABLE`/`CREATE TABLE` pair and the three-column upsert conflict target.
- Full suite green — `Category!=Integration` 877 passed, `Category=Integration` 200 passed, `tsc -b`
  clean, 0 failures in either.

## Decisions

- **`SourceTable` kept in the primary key** — see "The key, in two parts".
- **The database quoted as well as the schema and table**, beyond what the phase doc asked for. Same
  ambiguity, adjacent segment, no additional migration.
- **`ResyncService` reads the connection's config rather than opening it**, and reports not-found rather
  than failing when that config is missing.
- **A mapping-less journal entry is skipped, not defaulted.** The alternative — an empty sentinel mapping
  name — writes a junk row *and* still loses the position, so it is strictly worse than skipping.
- **`RunExecutor`'s later `sourceDialect` left where it is.** `ResolveDialect` returns a singleton; a
  second call is cheaper than moving a declaration out of the block that uses it.

## What this phase did not build

- The lightweight polling gate — phase 75, unchanged and independent of this key shape.
- Anything touching `TriggerAuditReader`'s shadow tables, which do not use this key.
- Any SPA change. The audit the phase doc asked for found nothing: `types.ts`, `RunsPanel` and the
  preview code carry watermark *values* end to end and never construct or parse a key. `tsc -b` was run
  regardless and is clean.
- Deduplication of the `driver is IDialectProvider` idiom, which now appears in six places. It was
  already in five before this phase, it is three lines each, and folding it into a shared helper is a
  refactor across three projects that has nothing to do with watermark keys.

## Notes

The doc comment on `WatermarkKey` that flagged this collision as "worth fixing behind a migration if
anything ever makes it more than theoretical" is gone, replaced by the reasoning for the format that
replaced it. It was right about the cost and right to wait: the fix cost exactly the universal resync it
predicted, and it cost that once rather than twice because issue 2 arrived to share the bill.
