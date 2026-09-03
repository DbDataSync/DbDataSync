# Auto-provisioning must report the columns it just created back into the metadata cache

**Status: resolved — ready for an implementation phase doc.**

## The gap, confirmed by reading the code

`RunExecutor.EnsureTargetTableProvisionedAsync` (`RunExecutor.cs:832-897`) already knows the exact
column shape it's about to create or alter — `provisioned` (line 853,
`HistorizedProvisioning.Extend(columns, writerKind)`) is built right before the DDL runs and is exactly
what lands in the target table. After running the `CREATE`/`ALTER` steps, the method returns without
ever touching `mapping.TargetColumns` (phase 90's cache).

Consequence: a mapping configured to auto-provision its target (`CreateTargetTableIfMissing`/
`AlterTargetTableColumns`) creates the table, then — later in the *same* pass, once a writer or staging
provider needs `TargetColumns` per phase 91 — fails with `MetadataNotCachedException`. The table now
exists correctly; the run still fails, every time, until an operator manually hits Refresh metadata once.
After that first manual refresh it's fine — the gap is specifically the *first* auto-provisioned pass.

## Why this isn't a quick fix

Confirmed by grep: **no code in `DbDataSync.TaskRunner` has ever called `ConfigRepository.SaveTableMapping`,
and no `GitAuthor` is constructed anywhere outside the API layer.** Every config write in this codebase
originates from an API controller. `RunExecutor` only reads config (`LoadConnection`, etc.) — it has
never needed to write any, until now.

This is the same shape phase 39 already solved for the *state* store: TaskRunner doesn't write SQLite/
Postgres state directly either — it reports through a loopback protocol (`IRunnerState` →
`LocalRunnerState`/`RemoteRunnerState` → `StateProtocol` records → `RunnerStateEndpoints` →
`JournalRecovery` for offline resilience) to the API process, which is the sole writer. `SetWatermark`
is the closest precedent: TaskRunner computes a value mid-pass and needs it durably recorded by the
process that owns writing it.

## Resolved: extend the same pattern to this new case

TaskRunner reports the newly-provisioned columns to the API over the existing loopback connection; the
API performs the real, git-authored `SaveTableMapping` call — mirroring `SetWatermark`'s six-point chain
(new interface method, protocol record, endpoint, and whatever of `LocalRunnerState`/`RemoteRunnerState`/
`JournalRecovery` genuinely applies to a config write rather than a state write — see open question
below). Rejected: giving `TaskRunner` direct git-write authority over config — it would cross the exact
single-writer boundary phase 39 established for state, for no reason config should be treated
differently.

**In-memory update too, not instead.** The report-to-API round-trip closes the gap for every *future*
pass reading a re-loaded mapping, but the *current* pass already has `mapping` in memory
(`EnsureTargetTableProvisionedAsync`'s own parameter) — set `mapping.TargetColumns` on that same
in-memory object immediately after a successful provision, so the current pass succeeds without waiting
on the round-trip to complete or re-loading anything. Both matter: the in-memory set fixes *this* pass,
the reported write fixes *every pass after it*.

## Open question for the implementation phase

Whether the new capability belongs on `IRunnerState` itself (same connection, same authentication,
different concern than what that interface's name suggests it covers) or a sibling interface reusing the
same host/endpoint infrastructure. `IRunnerState` is explicitly about the *state* store; config is a
different resource with its own owner (`ConfigRepository`, git-backed). Lean toward a sibling interface
for the same reason `ConfigRepository` and `DbDataSync.State`'s stores are already separate concerns in
this codebase — but confirm the exact loopback-host wiring (`RunnerStateEndpoints`'s equivalent for
config, if one should exist alongside it, or whether it extends the same endpoint group) before deciding
in code rather than in this doc.

## What this phase should not do

- Give `RunExecutor`/`TaskRunner` direct git repository access.
- Touch the `AlterTargetTableColumns` path differently from `CreateTargetTableIfMissing` — both produce
  a `provisioned` column list that's equally authoritative once applied; both need the same report.
- Attempt to reconcile a concurrent operator edit racing with an automated provisioning report — out of
  scope; whatever conflict-handling `SaveTableMapping`/git already has for concurrent writes applies here
  unchanged, this phase doesn't need to invent anything new for that case.

## How to verify

- A test asserting a mapping configured to auto-provision, pointed at a source/target pair whose target
  table doesn't exist yet, succeeds on its very first pass — no manual Refresh needed.
- A test asserting the mapping's persisted config (re-loaded fresh, not the in-memory object) reflects
  the provisioned columns after that pass, so a second, independent process picking up the same mapping
  also succeeds without a manual step.
- A test asserting `AlterTargetTableColumns` reports its resulting shape the same way `CreateTargetTable`
  does.
- Full suite green (`Category!=Integration`, `Category=Integration`), `tsc -b`/SPA build clean (no SPA
  change expected).

**Next step**: ready for an implementation phase doc.

---

# Outcome

Agreed, as `implementation/todo/phase-094-auto-provisioning-metadata-report.md`.
