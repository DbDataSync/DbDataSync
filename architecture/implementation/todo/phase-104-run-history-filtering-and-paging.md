# Phase 104 — run history: server-side filtering, and paging back through older runs

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/replication-detail-ux-improvements.md`, resolved
2026-09-04. Item 2 of four; items 1, 3 and 4 are phase 103.

Depends on phase 103 only for where the panel lives. The work here is a store query, an endpoint and a
set of controls, and none of it cares which route renders `RunsPanel`.

## Why filtering and paging are one phase

Because building either alone produces something that lies.

Filtering today is applied client-side to rows already fetched:

```js
filter === 'failed' ? r.status === 'Failed'
: filter === 'backfills' ? r.runKind === 'Backfill'
: true
```

The server call behind it is `GET replications/{name}/runs?kind=&limit=50`. So the filter searches
**one page of fifty**. Add paging without moving the filters and "Failed" would report *no failed runs*
for a replication with plenty — just none in the newest fifty. Add filters without paging and they stay
as misleading as they are now, on a bigger list.

So the filters move server-side as part of adding paging. That is the phase.

## The store

`TaskRunStore.GetRunHistory(taskName, runKind?, limit)` today:

```sql
SELECT … FROM TaskRuns WHERE TaskName = $taskName {AND RunKind = $runKind}
ORDER BY EnqueuedAtUtc DESC {database.Limit("limit")};
```

It gains `mappingName`, `status` and a cursor.

### The keyset predicate has to be written the long way

A cursor on `(EnqueuedAtUtc, RunId)`, not offset: runs are append-heavy and the list polls, so offset
paging genuinely duplicates and skips rows as new runs arrive at the top — not theoretically.

**The obvious spelling does not work here.** A row-value comparison —

```sql
WHERE (EnqueuedAtUtc, RunId) < ($cursorTime, $cursorId)     -- NOT this
```

— is unsupported by SQL Server, and since phase 63 the state store runs on SQLite, SQL Server **and**
PostgreSQL. It has to be the expanded form, which all three accept:

```sql
WHERE EnqueuedAtUtc < $cursorTime
   OR (EnqueuedAtUtc = $cursorTime AND RunId < $cursorId)
```

Writing this down matters because the row-constructor version is what anyone reaches for first, it
works in two of the three engines, and the SQLite tests that most people run locally would pass.

**This departs from `useVerificationResult`, which pages by offset/limit**, and the departure is
deliberate: a verification result is a fixed parquet file that does not grow while you read it, so
offset is stable there and is not here. Recorded so a later tidy-up does not "make them consistent"
and reintroduce the shifting-rows bug.

### An index, and a nullable sort key

- **`TaskRuns(TaskName, EnqueuedAtUtc, RunId)`** in `Migrations.cs`. Without it the keyset predicate is
  a scan, and the whole point of paging is a history too long to read at once.
- **`EnqueuedAtUtc` is nullable.** Phase 73's migration backfills it, so rows with a null are not
  expected — but the three engines disagree about where NULLs sort, and a paging cursor that silently
  strands rows is worse than one that refuses. The query should exclude or explicitly order nulls
  rather than inherit whichever behaviour the engine happens to have.

### Do not duplicate `GetMappingRunHistory`

`TaskRunStore` already has a per-mapping history method, used by scheduling due-ness. The new
`mappingName` filter is a predicate on the general query, not a second call path — two queries meaning
"runs for this mapping" that diverge is a bug waiting for whoever changes one.

## The endpoint

```
GET replications/{name}/runs?kind=&mappingName=&status=&cursor=&limit=50
 -> { runs: [...], nextCursor: string | null }
```

`nextCursor` null means the end. An opaque string rather than exposing the two column values, so the
ordering can change without every client learning about it.

**`runs/watermark-times` takes the same filters and the same cursor.** It exists to be joined to the
history list client-side, and today takes the same `kind` and `limit` for exactly that reason. A list
that filters and pages while its companion does not would blank the watermark column for rows that
have one — a silent wrong answer, not a visible failure.

## The panel

- **Filter controls for kind, mapping and status.** The mapping filter's options come from the
  replication's table mappings, which `RunsPanel` can already reach — a free-text mapping name would
  invite typos that return an empty list indistinguishable from "no runs".
- **The existing three-way `Filter` type goes away.** `all | failed | backfills` becomes two of the new
  filters, and leaving it alongside them would mean two mechanisms disagreeing about what is shown.
- **Paging back, and a way home.** A "newer" control matters as much as "older": with polling suspended
  off page one, returning to the top is how the live view comes back.
- **Polling continues only on the first page**, and only with no cursor applied. Older pages are a
  stable keyset window and go static until the reader returns, so nothing moves under someone reading
  history — and the live view keeps behaving exactly as it does today.

## How it will be verified

**Unit** (`DbDataSync.State.Tests`) — the keyset behaviour is the part that is easy to get subtly wrong
and invisible when wrong:

- **stability:** read page 1, insert several new runs, read page 2 with the cursor from page 1 — page 2
  contains no row from page 1 and skips none between them. This is the test offset paging fails, and
  the reason for the whole decision.
- ties on `EnqueuedAtUtc` are broken by `RunId` deterministically, so a batch of runs enqueued in the
  same instant pages correctly rather than repeating or dropping one
- each filter alone and in combination, including a filter that matches nothing returning an empty page
  with a null `nextCursor` rather than a cursor that loops
- a null `EnqueuedAtUtc` row does not strand paging

**Run these against every state engine.** The dialect abstraction is exactly where this class of bug
hides: the row-constructor form above passes on SQLite and PostgreSQL and fails on SQL Server, so a
SQLite-only suite would report success on a broken query.

**API** (`DbDataSync.Api.Tests`) — the endpoint's parameters map through; `watermark-times` returns
times for exactly the runs the same filters and cursor return.

**E2E** — filter by status and see a failure that is *not* in the newest fifty runs (the fixture has to
be deep enough for that; a shallow one passes against the current client-side filter and proves
nothing); page older and back; confirm the list stops polling off page one and resumes on return.

## Decisions

- **Keyset cursor, expanded predicate**, for portability across the three state engines and stability
  under a polling list.
- **Filters are server-side**, because paging makes client-side filters wrong rather than merely
  limited.
- **`watermark-times` moves in step**, always.
- **Polling only on page one.**

## Out of scope

- **Full-text search over error messages.** A filter on status is not the same as searching what went
  wrong, and the second wants a different index.
- **A date-range filter.** Plausible and not asked for; the cursor makes it straightforward to add.
- **Retention or pruning.** Phase 60 already prunes run history; this changes how it is read, not how
  long it is kept.
- **Paging anything else.** The mapping run history views and the verification result keep what they
  have.

## Open questions to resolve during implementation

- **Does the cursor need to encode the filters it was issued under?** Paging with a cursor from a
  different filter set produces a coherent-looking but meaningless page. Encoding them and rejecting a
  mismatch is a few lines; the alternative is trusting the client to reset the cursor whenever a filter
  changes, which is the kind of thing that works until someone adds a filter and forgets.
- **Page size — fixed at 50, or chosen?** Fifty is the current implicit answer and nobody has asked for
  another, but paging makes it visible for the first time.
