# Phase 104 — run history: server-side filtering, and paging back through older runs

**Status**: Done.
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

## Outcome

Both open questions above are answered — see Judgement calls. Everything in the summary landed as
designed; nothing was descoped.

### What was built

**Store (`src/DbDataSync.State/TaskRunStore.cs`, `Models.cs`, `Migrations.cs`):**

- `GetRunHistory` gained `mappingName`, `status` and `cursor` parameters (`RunHistoryCursor?`, a
  `(DateTimeOffset EnqueuedAtUtc, Guid RunId)` pair) and now returns `RunHistoryPage` rather than a bare
  `IReadOnlyList<TaskRunRecord>` — see Judgement calls for why that type exists at all.
- The keyset predicate is exactly the expanded form the doc specifies:
  `EnqueuedAtUtc < $cursorTime OR (EnqueuedAtUtc = $cursorTime AND RunId < $cursorRunId)`, never the
  row-value-constructor spelling. `ORDER BY EnqueuedAtUtc DESC, RunId DESC` breaks ties deterministically.
  `EnqueuedAtUtc IS NOT NULL` excludes a null-timestamp row outright rather than trusting any one
  engine's NULL-ordering default.
- One extra row is fetched (`limit + 1`) so "is there a next page" is answered from what already came
  back rather than a second `COUNT` query; the extra row's own key becomes `NextCursor`.
- New index `IX_TaskRuns_TaskName_EnqueuedAt_RunId` on `TaskRuns(TaskName, EnqueuedAtUtc, RunId)` —
  appended as the next migration script, following this file's `{{key}}`/`{{text}}` token convention.
  The pre-existing `IX_TaskRuns_TaskName_EnqueuedAt` is left in place; it still serves the metrics
  window and the prune's recency ranking.
- `GetMappingRunHistory` is untouched and stays its own method — see its updated doc comment for why a
  second, unpaged, mapping-scoped query is deliberate rather than duplication waiting to diverge.
- `RunHistoryCursor` (a `readonly record struct`) and `RunHistoryPage` (implements
  `IReadOnlyList<TaskRunRecord>`, adds `NextCursor`) are new in `Models.cs`.

**API (`src/DbDataSync.Api/Controllers/RunsController.cs`; new
`Services/RunHistoryCursorCodec.cs`, `Models/RunHistoryResponse.cs`):**

- `GET replications/{name}/runs` takes `kind`, `mappingName`, `status`, `cursor`, `limit` and returns
  `{ runs, nextCursor }` (`RunHistoryResponse`) — `nextCursor` is the opaque, base64-over-JSON token
  `RunHistoryCursorCodec` encodes and decodes, never the raw `(EnqueuedAtUtc, RunId)` pair.
- `GET replications/{name}/runs/watermark-times` takes the identical four filter/cursor parameters and
  calls the identical `GetRunHistory` overload before handing the page to `RunWatermarkTimeService`,
  so it always resolves the same page `History` does.
- The cursor token carries the filters it was issued under (task name, kind, mapping, status); a
  decode whose embedded filters don't match the current request's resets to "no cursor" (page one)
  rather than erroring — see Judgement calls.

**Frontend (`api/types.ts`, `api/client.ts`, `api/hooks.ts`,
`pages/replication-detail/RunsPanel.tsx`):**

- New `RunHistoryFilters` (`kind`, `mappingName`, `status`, `cursor`) and `RunHistoryPage`
  (`{ runs, nextCursor }`) types. `client.ts`'s `runs.history` and `runs.watermarkTimes` both build
  their query string through one shared `runHistoryQuery(filters, limit)` function, so the two calls
  cannot quietly stop asking about the same page. `hooks.ts`'s `useRunHistory`/`useRunWatermarkTimes`
  both take a `filters` argument and append it to their query key (the existing `keys.runHistory(name)`
  prefix is kept exactly, so every pre-existing invalidation — trigger, cancel, a run completing on the
  hub — still matches whichever filter/page is currently open, with no changes needed at those call
  sites).
- `RunsPanel.tsx`: the `all | failed | backfills` `Filter` type is gone entirely. Three `<select>`
  filters (kind, mapping — options from `useTableMappings`, status) replace the three chips, each
  resetting the cursor stack to page one on change. Paging is a `(string | null)[]` cursor stack:
  "Older" pushes the current page's `nextCursor`, "Newer" pops one entry, and a "Back to top" link
  (shown whenever off page one) resets the whole stack in one click — the doc's "an older control
  matters as much as a newer one, and a way home". Polling (`refetchInterval`) is `undefined` — not a
  fast/slow choice, actually off — whenever the cursor stack is deeper than one entry, for both the
  history query and the watermark-times query; `RefreshCountdown` is swapped for a static
  "viewing an older page — not refreshing" note in that state rather than shown with a schedule the
  panel is not honouring.

**Tests:**

- `TaskRunStoreTests.cs` (new, SQLite, all executed and passing): the concurrent-write stability test
  (page one, insert more rows, page two from page one's cursor skips/repeats nothing), the deterministic
  tie-break test (three rows forced to one `EnqueuedAtUtc`, paged, compared against one unpaged read of
  the same three as ground truth), each filter alone and combined, a filter matching nothing, a null
  `EnqueuedAtUtc` row's exclusion, and the exact-boundary `NextCursor` null/non-null cases.
- `CrossEngineStateTests.cs` (new theories, `[Trait("Category","Integration")]`, run on all three
  `Engines`): the same keyset-paging and combined-filter properties, so the suite states the claim for
  SQL Server and PostgreSQL even though this sandbox cannot reach either server to execute those cases.
- `RunHistoryCursorCodecTests.cs` (new, pure unit tests, no HTTP): round-trip, null/empty input, a
  garbage token, and — one `[Theory]` per dimension — a cursor whose replication, kind, mapping or
  status no longer matches the request, each resetting to page one rather than throwing.
- `RunsControllerTests.cs` (extended): the endpoint's new parameters filter and combine correctly, a
  filter matching nothing returns an empty page with a null cursor, and a page's `nextCursor` resumes
  correctly when fed back as `cursor`.
- `RunWatermarkTimeTests.cs` (extended): `watermark-times` given the same filters as `History` resolves
  to exactly the same run ids — not a superset (an excluded run's watermark leaking through) and not a
  subset — using a fixture where the excluded run has a watermark just as resolvable as the kept ones'.
- `ResyncTests.cs` (fixed, pre-existing): its one call to the history endpoint deserialized a bare
  array; updated to the `{ runs, nextCursor }` shape this phase introduced.
- `run-history-filtering-and-paging.spec.ts` (new E2E): a 55-run fixture whose one failure is the
  *oldest* run — outside an unfiltered first page's newest fifty — proves the status filter searches
  the whole history rather than whatever page happened to be on screen; a second test pages older
  (reaching that same failure with no filter at all), then back via both "Newer" and "Back to top"; a
  third asserts the poll count does not grow while off page one and does grow again after returning.
  The stub implements real filter/sort/cursor semantics against the fixture (not one canned response),
  so the client's own query-building is what is under test.
- `run-details-dialog.spec.ts`, `runs-watermarks-refresh.spec.ts`, `monitoring-restructure.spec.ts`,
  `mapping-column-add.spec.ts`, `golden-path.spec.ts` (fixed, pre-existing): every stubbed or real
  response for `GET .../runs` updated from a bare array to `{ runs, nextCursor }`.

### How it was verified

**Executed, and passing:** every `DbDataSync.State.Tests` SQLite test (156 total in the project, one
pre-existing, unrelated failure — `RemoteRunnerStateTests`'s file-locking flake, reproduced against
`git stash` too, so not introduced here); the 21 `TaskRunStoreTests` including all of this phase's new
ones; the 8 new `RunHistoryCursorCodecTests`. `npx tsc --noEmit` and `npm run build` on the SPA, and
`npm run lint` (oxlint), all clean. All 85 Playwright specs listed and parsed without error
(`--list`); 32 of them — every stubbed spec this phase touches, plus `lag-monitoring.spec.ts` and
`monitoring-intent-and-hold.spec.ts` as an unrelated-regression check — were actually run, against a
hand-started API + vite dev server per this phase's own environment note (scratch Playwright config,
globalSetup/globalTeardown stripped, never committed), and all 32 passed.

**Executed against a real server, beyond the stubs:** the API and TaskRunner were started for real
(no TestServer) against a scratch SQLite state file, and exercised directly with `curl` — a real
connection, replication and mapping created over the HTTP API, a real run triggered (which produces a
real `TaskRuns` row through the real work queue and a real spawned TaskRunner process), then read back
through `GET .../runs` with `kind`/`mappingName`/`status` combined (matched exactly), a mismatched
filter (empty page, `nextCursor: null`), and `GET .../runs/watermark-times` with the identical filters
(empty object, correctly, since the run had made no watermark durable yet). This is not something any
committed test runs, but it is real end-to-end confirmation that the wire format, filter semantics and
lockstep behaviour hold outside of both the SQLite unit tests and the network-stubbed E2E suite.

**Blocked by a pre-existing environment issue, reasoned about instead:** `DbDataSync.Api.Tests`'
`RunsControllerTests`/`RunWatermarkTimeTests` HTTP tests (both this phase's new ones and every
untouched one in the same files, and in unrelated files like `ConnectionsControllerTests`) return 500
here — confirmed to be the documented
`NotSupportedException: Negotiate authentication requires a server that supports IConnectionItemsFeature`
TestServer incompatibility, unrelated to this phase, by reproducing the identical 500 against
endpoints this phase never touched (`Get_WhenRunDoesNotExist_Returns404`, an untouched
`ConnectionsControllerTests` test, and the pre-existing `RunWatermarkTimeTests` case that asserts a
404). 251 of 403 `Api.Tests` fail in this run, which is the same order of magnitude as this
project-wide issue, not something this phase's endpoints made worse. The manual `curl` verification
above is what stands in for these in this environment; they are written correctly and will run for
real in CI (phase 98).

**Reasoned about by hand, not executed:** SQL Server and PostgreSQL correctness for the keyset query.
`CrossEngineStateTests`' new theories fail here with connection-refused for both engines (confirmed
the same is true of every *pre-existing* theory in that file, e.g. `ARunRoundTrips`, so this is the
documented sandbox limitation and not a regression). Traced by hand instead:
`MsSqlStateDialect.Limit` renders `OFFSET 0 ROWS FETCH NEXT @fetchLimit ROWS ONLY` (valid after the
`ORDER BY` this query always has) and `PostgresStateDialect.Limit` renders `LIMIT @fetchLimit`; both
dialects rewrite every `$name` placeholder to `@name` via the same `StateDatabase.Command` regex the
whole store already relies on, so the rendered predicate is the same expanded
`EnqueuedAtUtc < @cursorTime OR (EnqueuedAtUtc = @cursorTime AND RunId < @cursorRunId)` on all three
engines — no row-value constructor anywhere. The timestamp comparison is a string comparison, but
every row is written as `DateTimeOffset.UtcNow.ToString("O")` (a fixed-format, `Z`/`+00:00`-suffixed
ISO 8601 string), which is exactly the assumption every pre-existing `ORDER BY EnqueuedAtUtc DESC`
query in this store already depended on before this phase.

### Judgement calls

- **`RunHistoryPage` implements `IReadOnlyList<TaskRunRecord>` instead of `GetRunHistory` changing its
  return type outright.** Roughly a dozen existing call sites across five test files (plus one in
  `SchedulerServiceHoldTests`) call `GetRunHistory` expecting a plain list — `.Count`, indexers, LINQ,
  `Assert.Single`/`Assert.Empty`. A wrapper that *is* one, with `NextCursor` bolted on, kept every one
  of those compiling and passing unchanged (verified by running them) while giving the one caller that
  cares (the endpoint) somewhere to read the cursor from — cheaper and less risky than touching a dozen
  unrelated call sites to carry a new return shape through.
- **A cursor minted under different filters resets to page one rather than rejecting the request with
  an error.** The open question left this as either; reset was chosen because the mismatch is, from
  the caller's side, an ordinary GET — most likely a client that changed a filter without dropping the
  old cursor — and "start that question over" is a more useful answer than a 400 for what nobody did
  wrong on purpose. Documented in `RunHistoryCursorCodec`'s own doc comment so a later reader does not
  have to reconstruct the reasoning from a diff.
- **A null `EnqueuedAtUtc` row is excluded from the query rather than given an explicit
  engine-by-engine `ORDER BY ... NULLS LAST`/`CASE WHEN` treatment.** Phase 73 backfills the column, so
  in practice this never fires; excluding it is a portable, one-line answer that behaves identically on
  all three engines, where hand-writing "put nulls last" three different ways would have been three
  more things to get right and verify.
- **`GetMappingRunHistory` was left exactly as it was, not given the same filter/cursor treatment.**
  It already answers "runs for this mapping" for scheduling due-ness and per-mapping history views,
  unpaged, and those callers have no use for a cursor. Extending it would have made two paging
  mechanisms to maintain where the doc explicitly asks for one; its doc comment now says why it stays
  separate rather than leaving that as a silent inconsistency for a future reader to puzzle over.
- **Page size stays fixed at 50** — no `limit` control was added to the panel's UI, matching the doc's
  explicit call.
- **The E2E fixture is 55 runs with the one failure placed *oldest*, and the stub implements real
  filter/sort/cursor logic against it**, rather than a shallow fixture and a canned response per
  request. A shallow version would pass against the pre-phase-104 client-side filter too and prove
  nothing about the fix; a canned-response stub would prove the client renders whatever JSON it is
  handed, not that it builds the right request in the first place.
