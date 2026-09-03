# Phase 99 — the initial-load rule, written down and pinned

**Status**: Complete.
**Plan reference**: none — this began as a question ("how does a mapping decide between an initial load
and a change query?") whose answer turned out to be undocumented. Documentation work is a phase, per
this folder's README.

## Why this exists

The rule a reader follows on its first pass — **no stored watermark means read the whole source table,
not read nothing** — was implemented correctly in every reader and stated nowhere. `detailed-design.md`
§3.7 documented the `ChangeWatermarks` row in detail, including the key granularity and the
"repointed mapping starts over" consequence, but nothing said what a reader *does* when the lookup
comes back empty, or why.

That is a bad thing to leave implicit, because the rule is implemented independently in each reader:
there is no shared base class enforcing it and no single call site to review. A reader that skipped
the full load would leave a mapping permanently and silently half-replicated against a source table
that already had rows — the pass succeeds, the counts look plausible, and the rows that predate the
feed being switched on simply never arrive.

## What this phase built

### `architecture/detailed-design.md` §4.1

A new subsection under End-to-End Data Flow, deliberately placed there rather than in a new document:
§3.7 already carries the `ChangeWatermarks` half and is cited from code, so a second file would have
been a second thing to keep in step. It states:

- **Which reader runs is configuration**, resolved most-specific-first by
  `PipelineResolution.ReaderKind` — work item, then mapping override, then replication. No
  auto-detection anywhere.
- **Whether it full-loads turns on one fact**, with the three-line lookup from `RunExecutor` quoted,
  and the two consequences that live in those lines: the key includes the source table, and a Backfill
  is handed `null` unconditionally so it can never disturb the incremental cursor.
- **A per-reader table** of what each does with and without a watermark, and the one shared reason.
- Two behaviours that fall out of the same branch: a first pass is unbounded even when a row cap is
  configured (a capped full load has no resumable position), and re-pointing a mapping at another
  source table starts it over.
- What writes and clears the cursor, including why acknowledgement happens after the target write
  commits, and that Resync is the deliberate route back to an initial load.

### `ChangeReaderFirstPassContractTests`

Three tests, in `DbDataSync.Api.Tests` because it is the only project that sees every driver assembly —
the same reason and the same shape as `AuthorizationCoverageTests`, which sweeps controllers for the
same kind of "nobody decided about this" gap.

- **Every `IChangeReader` is declared** either as full-load-on-first-pass, with the name of the test
  that proves it against a real database, or as exempt with the reason. A reader in neither table fails
  the test, and the message says what the decision is between rather than only that one is missing.
- **Neither table names a reader that is gone**, so a stale entry cannot make the coverage read as
  broader than it is.
- **Every declared proof names a test that exists**, checked by reflection over the driver test
  assemblies — loaded by path rather than by project reference, which would drag every driver's
  fixtures into this assembly to check four names.

The point is not to re-test the readers; the per-reader integration tests already do that, and they are
what the table names. The point is that a *new* reader cannot be added without somebody deciding which
contract it follows.

### `IChangeReader.ReadChangesAsync`'s `previousWatermark`

The parameter had no doc comment at all. It now carries the rule in one paragraph, and points at both
§4.1 and the contract test — because the interface is what somebody writing reader number nine reads.

## The classification, as it stands

| Reader | contract |
| --- | --- |
| `MsSqlChangeTrackingReader`, `MsSqlCdcReader`, `TriggerAuditReader`, `WatermarkReader` | full load on first pass |
| `BatchReloadReader`, `MsSqlBatchReloadReader` | exempt — a reload reads every row by definition |
| `ScriptedQueryReader`, `DuckDbQueryReader` | exempt — a query source has no feed to have been switched on |

## How it was verified

Each of the three guards was deliberately broken and seen to fail, then restored:

- dropped `DuckDbQueryReader` from both tables → `EveryChangeReader_IsDeclared…` failed
- pointed a declared proof at a test name that does not exist → `EveryDeclaredProofNamesATestThatExists`
  failed
- added a non-reader type to a table → `NeitherTableNamesAReaderThatIsGone` failed

This mattered: phase 96 produced an assertion that passed against the very defect it was written for,
and the lesson taken from it was to prove a new guard fails before trusting it.

Full non-integration suite green apart from three failures confirmed pre-existing on a clean checkout
(`SecretCommandTests`, `InviteCommandTests`, `ChangePollingGateTests.CdcAndChangeTracking…`).

## Also fixed here

`phase-096-…md` existed in **both** `todo/` and `done/`: the completion commit `beb9c3e` added the
retrospective without deleting the intermediate copy, which breaks this folder's "a phase moves exactly
once" rule and leaves two documents claiming different statuses for one phase. The `todo/` copy is
removed; the `done/` one is the complete retrospective.

## What this phase did not do

- **No behaviour change.** Not one reader was edited. The rule was already right everywhere; only its
  statement was missing.
- **No cross-engine behavioural contract test.** Driving all eight readers through a real first pass
  would need Change Tracking, CDC, a trigger shadow table and a watermark column stood up together,
  and four of them are already covered individually. The gap this closes is "nobody decided", not
  "nobody tested".
- **No audit of `detailed-design.md`'s older sections**, some of which have drifted — §1 still says v1
  is MSSQL → MSSQL only, which Postgres and DuckDB have made untrue. Worth its own pass; not folded in
  here.
