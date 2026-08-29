# Data snapshotting and SCD tracking

**Status: draft, 2026-08-29 — surveying the design space, not yet resolved.**

## The ask

Support two related but distinct capabilities: periodic **snapshotting** of a table's full state, and
**SCD (slowly changing dimension) tracking** — preserving history of a row's values across changes,
rather than overwriting it in place. Both need to work when source and target are the *same* connection
(a history table living beside the live table it tracks), and equally when the target is a fully separate,
remote connection — the normal DataSync shape.

## Two capabilities, not one

They're related (both are "don't just overwrite, keep history") but solve different problems and cost
different amounts:

- **Snapshotting**: capture the table's *entire* state at a point in time, as a new batch of rows,
  regardless of what changed. Cost is proportional to table size per snapshot, taken on some schedule
  (nightly, hourly). Good for point-in-time reporting, audits, or sources with no reliable change
  detection at all.
- **SCD tracking** (Type 2, the common case): preserve a row's *history of values* — each time a tracked
  row changes, the previous version is closed out (an end-of-validity marker) and a new version is
  opened, keyed by the row's business key plus a validity range. Cost is proportional to what actually
  changed, taken continuously off a real change feed. Good for "what did this customer's address look
  like on March 3rd," not just "what does it look like now."

They should very likely be **two different writer kinds**, not one configurable mode, because their
correctness properties differ (see below) — but they can share the same provisioning/bookkeeping-column
machinery, since both need the target table to carry more than the mapped columns.

## How each maps onto the existing model

DataSync's Reader → Staging → Writer pipeline (`architecture.md`) already separates "what changed" from
"how it's applied." Both capabilities are squarely **new `IChangeWriter` implementations** — nothing
about the reader or staging abstractions needs to change:

```csharp
Task<WriteResult> ApplyAsync(
    DbConnection targetConnection, TableRef target, StagedChangeSet staged,
    IReadOnlyList<ColumnMapping> columnMappings, IReadOnlyDictionary<string, string> options,
    CancellationToken cancellationToken);
```

- **A snapshot writer** is close to the simplest writer possible: every row it's handed gets inserted,
  tagged with a snapshot marker (a timestamp or run id column), never updated or deleted. It would
  typically be paired with a **full read** (`BatchReload`, already built) on a schedule, rather than a
  watermark or log-based reader — a snapshot is defined by "everything, right now," not "what changed."
  `SupportsReconciliation` is meaningless here in the usual sense; there's no notion of the writer
  "removing what's absent," because every snapshot is additive by design.
- **An SCD Type 2 writer** is the harder one. For each incoming row it needs to know: is this key new
  (open a first version), changed (close the current version, open a new one), or — the hard case —
  **absent** (does the source's disappearance of a key mean delete, and if so, does *this* row close?).
  That last case depends entirely on **`IChangeReader.DetectsDeletes`**: an SCD2 writer fed by a
  delete-blind reader (plain Watermark) can never close a row whose source record was deleted — it will
  look "current" forever. This needs to be surfaced plainly to whoever configures the pairing, not
  discovered later.

## Same connection, remote target — this is mostly already free

Nothing in the current model requires source and target to be different connections —
`architecture.md`'s own connection model already allows one connection to serve as source, target, or
both. The interesting question isn't "can they be the same connection" (they already can), it's:

- **The target table must still be a distinct table object** — an SCD/snapshot history table living
  beside the live table it tracks, never the same table, since a writer can't read and rewrite the row
  it's mid-write on. This is a naming/provisioning concern (the target-table picker and provisioning
  already assume the target is *a* table; nothing assumes it's a *different* table from the source today,
  because until now source and target being the same connection didn't imply anything about the same
  table).
- **A remote target needs nothing extra.** The writer operates against `targetConnection` exactly as
  every writer does today — same-connection is simply the case where that connection happens to equal the
  source's. No special-cased code path, as long as nothing elsewhere assumes source ≠ target (worth an
  explicit check rather than an assumption).

## Provisioning and bookkeeping columns

Both writers need target columns beyond the mapped ones:

- **Snapshot**: at minimum, a snapshot timestamp/run-id column.
- **SCD2**: a validity range (`ValidFrom`/`ValidTo`, or `ValidFrom` + `IsCurrent`), and — because the
  source's own primary key is no longer unique in the target (the same business key now has multiple
  historical rows) — **a new surrogate key** the target actually keys on. The natural key plus validity
  range becomes a mapped concept the writer manages, not something column mapping expresses today.

This is squarely `IProvisioner`/`CreateTableStatement` territory — phase 45's target-column work
(inferred type, editable via pencil, and the new `alterTargetTableColumnsIfMissingOrChanged` setting) is
directly relevant: the bookkeeping columns are exactly the kind of "columns the mapping needs that aren't
one-to-one with the source" that machinery should be extended to describe, not something a new, separate
mechanism reinvents.

## Interaction with verification (phase 43/48)

A verification check comparing a live source against a historized target needs to compare against
**only the current version** of each key (`WHERE IsCurrent = 1`), not the full history — otherwise every
row count check would fail by definition (the target legitimately has more rows than the source). This is
a real dependency to flag, not solve here: phase 43's checks assume source and target are the "same
shape"; an SCD/snapshot target isn't, and a check against one needs to know how to narrow itself back down
to a fair comparison.

## Open questions — these are product decisions, not implementation details

1. **Does SCD2 require a delete-detecting reader, or is "never closes on delete" an acceptable, clearly
   labeled limitation for readers that can't?**
2. **How does an operator query "as of" a point in time?** Is that DataSync's problem (a view, a helper),
   or is "the table has ValidFrom/ValidTo, write your own WHERE clause" sufficient?
3. **Retention for both.** Snapshots and SCD history both grow without bound by design — does anything
   ever purge old versions/snapshots, and if so, on what policy?
4. **Surrogate key generation** — an identity/sequence the target owns, or something DataSync computes
   (a hash of natural key + ValidFrom)? This affects whether the target needs write-back of a generated
   key, which nothing in the writer contract does today.
5. **Does a "snapshot" need to detect that nothing changed and skip an entire no-op copy**, or is writing
   every row on every snapshot (even unchanged ones) acceptable given it's meant to be a simple, cheap
   mechanism?

**Next step**: resolve the open questions above — they change the writer contract's shape (surrogate key
write-back, in particular) — then this is ready for an implementation phase doc.
