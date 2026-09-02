# Verify: are metadata/introspection queries confined to design time?

**Status: not investigated — planning backlog. No resolution yet.**

## The concern

A running replication pass should read only what's already in the mapping's stored config
(`ColumnMappings`, connection info, segmenting config) — not issue live schema-introspection queries
against the source or target while actually doing replication work. Introspection (`ListColumnsAsync`,
`ListTablesAsync`, catalog lookups) belongs at *design time* — the mapping editor, a connection test, a
preview — where an operator is actively configuring something and a live round-trip is the whole point.
During a dispatched `WorkItem`'s real pass, it's the same category of problem the Monitoring-tab lag
queries were: work that should cost the state database (or, here, the mapping's own stored config)
costing the source/target system instead, on every run, for information that's presumably already known
and shouldn't need re-asking.

## One concrete case already known, not yet confirmed as the only one

`BatchReloadReader.ReadChangesAsync` (`src/DataSync.Drivers.Generic/BatchReloadReader.cs`) calls
`catalog.GetColumnsAsync(sourceConnection, source.Schema, source.Table, cancellationToken)` **during the
actual read**, to build a segment's predicate (`SegmentScope.Build(dialect, binder, segment, columns)`
needs each segment column's type to render a correct, dialect-specific comparison). This is a live
catalog query issued on every segmented pass, not just at design time. Whether this is:

- a real instance of the problem (the column's type should already be knowable from the mapping's
  stored `ColumnMappings`, or should be captured once and cached rather than re-queried every pass), or
- a legitimate exception (segmenting can target a column that isn't itself one of the mapping's selected
  `ColumnMappings` — e.g. an unmapped key column used only to slice the load — in which case there may
  be nothing stored to read type info *from*, and the live query is the only source of truth)

is exactly the kind of judgment call this investigation needs to make case by case, not assume either
way going in.

## What "verify" should mean

An audit, not a guess — the same discipline phase 72's "audit rather than an assumption" precedent used:
grep every driver project (`DataSync.Drivers.MsSql`, `DataSync.Drivers.Postgres`, `DataSync.Drivers.Generic`,
`DataSync.Scripting`, plus DuckDB once phase 89 lands) for calls into `ITableCatalog`/schema-introspection
methods, and for each call site classify it:

- **Design-time only** — reachable only from the mapping editor, connection testing, or
  `IStatementPreview`/`PreviewService`'s path. Fine as-is; no change needed.
- **Run-time, and the information is already in stored config** — a real gap. The fix is to read the
  already-stored value instead of the live query, the same shape phase 87 used for reader lag (cache at
  the moment it's genuinely free, read the cache at the moment it's needed).
- **Run-time, and the information is genuinely not capturable from stored config today** — per the
  user's framing, this is where capture needs to start: extend what's stored (mapping config, or a new
  cached-metadata table/column) so a future pass can read it instead of asking the source/target live.
  Design that storage addition the same way every prior caching fix in this session has: captured at a
  moment the connection is already open for another reason, not a query invented solely to populate a
  cache.

## What this doc should not do yet

Resolve anything. This is scoping the investigation, not the investigation itself — the actual audit
(reading every driver's reader/writer/staging code) hasn't been done. `BatchReloadReader`'s case above
is a lead, not a conclusion.

**Next step**: do the audit described above; come back with a classified list of every run-time
introspection call site before deciding what (if anything) needs a phase doc.
