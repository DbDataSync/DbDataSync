# Phase 74 — Watermark keys: quoted identifiers, keyed by mapping

**Status**: Not started.
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
