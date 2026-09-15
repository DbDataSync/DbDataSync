# SCD2 duplicate-key handling: the window-function alternative, deferred pending a live server

**Status: a known, deferred optimization — now buildable, not started.** Extracted from
`architecture/implementation/done/phase-132-cdc-guaranteed-delivery-for-scd2.md`'s own "What's
explicitly still not built" section, where it sat as an inert bullet — moved here per
`architecture/implementation/README.md`'s "Follow-up work gets its own doc, not a paragraph."

## What phase 132 left open

> The window-function alternative for duplicate keys remains a real, deferred optimization once a live
> server is available to iterate the change-point detection against (it now is, for whoever picks this
> up — the row-by-row design shipped here is correct and only pays its extra cost when a key actually
> has more than one staged row).

Phase 132's own plan doc (`architecture/planning/done/mssql-cdc-source-batching-and-guaranteed-delivery.md`)
considered a set-based statement using `ROW_NUMBER()`/`LEAD()` window functions to detect and order
same-key duplicate changes within a batch, instead of the row-by-row approach that shipped. Row-by-row
was chosen because it was correct and simpler to reason about without a live server to iterate the
set-based version against; the note above records that the set-based alternative was never actually
disproven, just deferred.

## Why this might be worth picking up

- It's a pure optimization, not a correctness question — `Scd2Writer`'s current row-by-row handling is
  already correct (that's what phase 132 proved), so this is about cost, not behavior.
- It "only pays its extra cost when a key actually has more than one staged row" per the note above —
  worth confirming whether that cost is actually material in practice (a batch with many duplicate keys)
  before investing in the set-based rewrite, rather than assuming it matters.
- A live server is available now (per the note), which was the blocker phase 132 named for iterating
  against this approach at all.

## Not evaluated

Whether the set-based version is actually faster in practice, by how much, under what batch shapes — none
of this was measured. This doc exists to make the deferred option findable, not to argue it's worth
doing; that's the first thing whoever picks this up should establish.
