# Phase 145's set-based SCD2 duplicate handling was never timed against a real server

**Status: open.** Extracted from `architecture/implementation/done/phase-145-scd2-duplicate-keys-set-based.md`'s
own "What is not verified" section, per `architecture/implementation/README.md`'s "Follow-up work gets its
own doc, not a paragraph."

## What is open

Phase 145 replaced phase 132's per-key/per-row loop with two window-function statements. The round-trip
count is countable by construction and is not in doubt: `1 + K + 2KR` statements for `K` duplicate keys of
`R` rows each became **3**, and the loop that produced the first number no longer exists.

That is not the same claim as *faster*, and this phase shipped without making the stronger one.

The phase's own plan asked for exactly this and named why:

> **A real before/after comparison**, not just "it's obviously fewer round trips": run a batch with a
> meaningful number of duplicate keys (say, 50 keys × 5 staged rows each) through both implementations
> … against a real server, and record actual wall time or round-trip count — the follow-up doc this phase
> came from explicitly says this was never measured, and "it's obviously better" shouldn't be the only
> evidence in the retrospective.

It still has not been measured. The machine phase 145 was implemented on has no Docker and no SQL Server,
so the integration suite ran in CI rather than locally, and CI runs correctness tests — not benchmarks.

This is now the **second** time this measurement has been deferred: it was first asked for in
`architecture/planning/done/follow-up-phase-132-scd2-duplicate-key-window-function-optimization.md`, which
is what phase 145 itself came from.

## Why it is worth doing rather than assuming

The rewrite is not free. It buys round trips and pays for:

- **A sort per window.** `PARTITION BY {keys} ORDER BY {OrderingColumn}` over the whole staging table, for
  a batch where most keys are singletons and are filtered out by `__DS_KeyCount > 1` *after* the window is
  computed. The loop sorted nothing.
- **A correlated `NOT EXISTS` against the staging table per candidate target row**, twice — the
  "ignore the versions this pass itself opened" exclusion that makes the two statements order-independent
  (see the phase doc for why it is needed).
- **One `LAG` column per mapped value column.** A wide mapping makes the derived table wide.

None of these plausibly costs more than `2KR` round trips at any real `K` and `R`. But "plausibly" is the
word the phase 132 follow-up already objected to, and the shape of the answer — whether the win is large
at every batch size or only above some `K`, and whether a wide mapping erodes it — is not guessable from
the statement text.

## What would close this

A batch with a meaningful number of duplicate keys (the plan's own suggestion: 50 keys × 5 staged rows)
through both implementations against a real SQL Server, recording wall time. The old implementation is at
`git show <phase-132..144 range>:src/DbDataSync.Drivers.Generic/Scd2Writer.cs` — the commit before phase
145 — so "both implementations" needs a checkout, not a reimplementation.

Worth recording alongside: a *wide* mapping as well as the two-column one the existing integration fixture
uses, since the `LAG`-per-value-column cost is the one that scales with something the fixture holds fixed.

Not urgent. Phase 145 was itself explicitly a pure optimization with no correctness or urgency argument
behind it, and this is a measurement of that optimization.
