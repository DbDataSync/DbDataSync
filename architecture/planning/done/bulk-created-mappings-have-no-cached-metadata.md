# Bulk-created mappings have no cached column metadata

**Status: raised 2026-09-03, not yet agreed.**

Creating mappings in bulk should capture the source and target column metadata at the moment each
mapping is created, the same way creating one through the editor does. Today it captures nothing.

## What the code does now

`TableMappingsController.BulkCreate` builds each mapping with `NewMapping(...)` — name, one source
spec, one mirrored target spec — and hands it straight to `configRepository.SaveTableMapping`. That
path never goes near `MappingMetadataCapture.Apply`, and it would have nothing to give it if it did:
the bulk screen (`MappingsOverview.tsx`) fetches *table* names via `useTables` and never fetches
columns, so unlike the mapping editor there is no already-loaded column list sitting in the client
waiting to be persisted.

So every mapping created in bulk lands with `SourceColumns` and `TargetColumns` empty and
`ColumnsCapturedUtc` null.

## Why that matters more than it did when the cache was introduced

Phase 90 could leave an uncaptured mapping alone because nothing consumed the cache yet. Phase 91
changed that: readers and writers run from the cache only, with no live fallback anywhere, and an
empty cache **throws**. A bulk create of forty tables therefore produces forty mappings that cannot
run until somebody opens each one and presses Refresh — forty times, one screen at a time. The
gesture the bulk screen exists to remove comes straight back in a different tab.

## The shape of the fix, roughly

Capture server-side inside the bulk loop, because the client has nothing to send. The introspection
already exists and is already the right one: `MappingMetadataService.RefreshAsync` reads both sides
through `MetadataService`/`IColumnCatalog` (so a connection with a bound `metadataProvider` script is
honoured) and treats a side it cannot read as a stated reason rather than as a table with no columns.
Phase 94 is the precedent that a non-editor, server-side path may write this cache — auto-provisioning
already reports the shape it created.

Things to settle when this is picked up rather than now:

- **Reuse `RefreshAsync` or factor its read out?** As written it loads the mapping and saves it, so
  calling it per table after `SaveTableMapping` means two saves and two commits per mapping. The
  read half is what's wanted; the capture should happen *before* the single save.
- **Cost and progress.** Forty tables becomes eighty catalog calls on a call that currently makes
  none. The `bulkMappingProgress` channel is already there to report it, but whether the tables are
  introspected one at a time down the loop or read once per side up front is a real choice.
- **A target that isn't there yet.** Bulk mirrors the source's schema and table, and the target may
  legitimately not exist until provisioning runs. That must be the normal state `RefreshAsync`
  already treats it as — capture the source, record why the target was unreadable, and let phase 94's
  provisioning path fill in the target later. It must not fail the batch.
- **A source that can't be read** — should the mapping still be created (uncaptured, as today) with
  the reason surfaced in `BulkCreateResult`, or should the table be reported alongside `Skipped`?
  Leaning toward the former: a created-but-uncaptured mapping is exactly today's behaviour, so this
  is only ever an improvement on it.

Explicitly not proposed here: any backfill of mappings created in bulk before this exists. Those stay
on Refresh, per phase 90's no-silent-migration rule.

---

# Outcome (2026-09-03)

Agreed, with all four questions above answered. Design lives in
`implementation/todo/phase-095-bulk-create-metadata-capture.md`:

1. **Factor the read half out of `RefreshAsync`**, so bulk captures before its single
   `SaveTableMapping` — one save and one commit per mapping, one introspection path.
2. **Sequential per table, with progress reported.** `bulkMappingProgress` grows a stage so the
   screen shows the catalog read instead of appearing to stall. No new concurrency, and no new
   `IColumnCatalog` method.
3. **An unreadable target is normal**, exactly as `RefreshAsync` already treats it: capture the
   source, keep the stated reason, never fail the batch. Phase 94's provisioning report fills the
   target side in later.
4. **An unreadable source still creates the mapping**, uncaptured with its reason surfaced — that
   is today's behaviour, so this can only be an improvement on it.

A fifth thing surfaced while writing the phase doc and is folded into it: **`ColumnMappings` is empty
on a bulk-created mapping too**, and staging throws on an empty list
(`MsSqlStagingTableProvider.cs:41`, `BatchInsertStagingProvider.cs:52`) quite independently of the
cache. Fixing only the cache would have moved the failure from one exception to another. Phase 95
therefore also generates the column mappings from the columns it just read, on exactly the rule
`ColumnMappingEditor`'s "Auto-map by name" button already uses.
