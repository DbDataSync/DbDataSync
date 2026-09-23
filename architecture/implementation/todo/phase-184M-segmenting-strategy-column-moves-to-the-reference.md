# Phase 184M — A segmenting strategy's column moves from the strategy to whoever references it

**Status**: Built. See Retrospective.
**Plan reference**: none — raised directly in conversation ("each table will likely have a completely
different column name so it will need to be configured at the mapping level"), confirmed real by reading
the code, and resolved by asking which of two shapes to build rather than guessing on a schema change
this repo's own "no migration" policy makes one-way.

## Why

`SegmentingStrategyConfig.Column` (`src/DbDataSync.Core/Config/SegmentingStrategyConfig.cs`) lived on the
named, replication-wide strategy object — one column, shared by every mapping that referenced the
strategy by name. That's backwards: a strategy's query returns *bounds*, never the thing they bound (its
own now-removed doc comment already said so), and which column those bounds apply to is a property of
whichever table a mapping is dividing — not of the reusable SQL/script that computes the boundaries. A
strategy named `monthly-buckets` is exactly the kind of thing worth reusing across many tables, and every
one of them needing the identical column name to do it was the actual bug being reported.

## What changed

**The column moved onto the reference, not the strategy.** `SegmentingStrategyConfig` no longer carries
`Column` at all. `CustomSegment` (`src/DbDataSync.Core/Config/BatchReloadSegment.cs`) — the marker a
mapping's `DefaultSegmenting` or an ad-hoc Bulk Load/reconcile-deletes request already carried to name
*which* strategy to run — gained a second, optional field for it:

```csharp
public sealed record CustomSegment(string StrategyName, string? Column = null) : BatchReloadSegment;
```

Optional because `Script`-kind strategies never needed one (a script already knows its own column, or
builds `RangeSegment`s that carry none) — the same exemption `SegmentingStrategyRunner` already enforced
when the field lived on the strategy; that check moved with it, now validated against the caller-supplied
column instead.

**Every layer between "an operator picks a strategy" and "the runner executes it" now threads a column
parameter instead of reading `strategy.Column`:**

- `SegmentingStrategyRunner.RunAsync` — gained a `string? column` parameter.
- `CustomSegmentExpansion.ExpandAsync` — reads `custom.Column` off the marker it's already unwrapping.
- `CustomSegmentExpansion.ProposeAsync` (the ad-hoc preview path — Bulk Load's checklist, the editor's
  Test button) — gained a `string? column` parameter, since there's no `CustomSegment` to read it from
  when an operator is previewing a strategy that isn't saved as a mapping default yet.
- `SegmentingPreviewService`'s two `PreviewAsync` overloads (by saved strategy name; by unsaved strategy
  body) — both gained `string? column`.
- `RunsController`: the by-name preview route (`GET .../segmenting/{strategyName}/preview`) gained a
  `[FromQuery] string? column`; the unsaved-strategy route (`POST .../segmenting/preview`) changed its
  body from a bare `SegmentingStrategyConfig` to a new `TestSegmentingStrategyRequest(Strategy, Column)`
  (`src/DbDataSync.Api/Models/TestSegmentingStrategyRequest.cs`) — since the strategy itself no longer has
  anywhere to carry the editor's scratch test column.
- `BatchReloadSegmentYamlConverter` — `custom` mode's YAML gained an optional `column:` key, read/written
  alongside `strategyName`.

**Frontend, three real surfaces that needed a column input that either didn't exist or lived in the wrong
place:**

- `SegmentingStrategiesCard.tsx` (the replication-level strategy editor) — the shared "Over column" field
  is gone from both the list view and the editor. The Test button needed its own, separate, non-persisted
  column input (`strategy-test-column-input`) instead, since testing a strategy still needs *some* column
  to run against even though the saved strategy no longer has one.
- `DefaultSegmentingCard.tsx` (a mapping's own default segmenting) already had the natural per-mapping
  home for this — a `Column` field added to the `custom` mode block, mirroring the `auto` mode's existing
  `Column`/`Buckets` pair right above it.
- `BulkLoadForm.tsx` and `ReconcileDeletesForm.tsx` (the two ad-hoc "queue this now" forms, structurally
  identical copies of the same segment picker) — previously had **no column concept at all** for `custom`
  mode; the existing `column` state (already used by `list`/`range`/`auto`) is now also shown and used for
  `custom`, threaded into `useSegmentingPreview`'s new `column` argument.

`api/types.ts`'s `BatchReloadSegment` custom variant and `SegmentingStrategyConfig` interface were updated
to match; `api/client.ts`/`api/hooks.ts` thread `column` through `previewSegmenting`/`previewUnsavedSegmenting`/
`useSegmentingPreview`/`useTestSegmentingStrategy` (including in the query key, so a column edit re-runs
the preview the same way a strategy-name edit already did).

## What this does not do

- **No migration.** An existing `driver.yaml`/replication config with a strategy's old `column:` field
  loses it silently on next read (YamlDotNet ignores an unmapped key on `SegmentingStrategyConfig` — it
  doesn't error, since that type never marked unknown keys as fatal) — matching this repo's own stated
  policy of a documented breaking change with no migration or compat shim (the same call this feature's
  own planning doc made for an earlier "no migration" segmenting change), since there are no serious
  installations yet.
- Does not add a "copy the mapping's default column into an ad-hoc form" convenience — `BulkLoadForm`/
  `ReconcileDeletesForm` already pre-fill their `column` state from the mapping's saved default for
  `list`/`range`/`auto`; the same pre-fill effect now also covers `custom`, for free, since it's the same
  state variable.
- Does not change how `Script`-kind strategies work — they still never need a column, at the strategy
  level or the reference level, exactly as before.

## How to verify

- `tests/DbDataSync.Core.Tests/DefaultSegmentingYamlRoundTripTests.cs`'s `EverySegmentMode_SurvivesARoundTrip`
  now includes `new CustomSegment("year-month", "OrderDate")` and asserts it round-trips through YAML.
- `tests/DbDataSync.Scripting.Tests/SegmentingStrategyRunnerTests.cs` — every test updated to pass column
  as its own argument rather than through the strategy; `AStrategyWithNoColumn_SaysWhatItIsMissing` still
  proves the "every kind but Script needs one" rule, now enforced against the parameter.
- `tests/DbDataSync.Api.Tests/SegmentingPreviewTests.cs` — all 6 tests updated (`?column=` query string for
  the by-name GET, `TestSegmentingStrategyRequest` for the POST) and pass unchanged in substance.
- Full backend unit suite green: `DbDataSync.Scripting.Tests` (58), `DbDataSync.Core.Tests` (276),
  `DbDataSync.Drivers.DuckDb.Tests` (33), `DbDataSync.Api.Tests` (543 passed, 23 skipped/Windows-only) —
  all pass with these changes.
- Frontend: `tsc -b` (typecheck) clean, `oxlint` clean (only pre-existing, unrelated warnings), `vitest run`
  green (86 tests).
- `tests/DbDataSync.Web.Tests/tests/golden-path.spec.ts`'s test 41 (the one comprehensive spec covering
  this feature end to end) updated to match the new shape: fills the new scratch
  `strategy-test-column-input` before testing rather than a since-removed `strategy-column-input`; drops
  the now-meaningless "column round-trips on reopening the strategy" assertion (there's nothing left to
  round-trip on the strategy itself); fills the newly-shown `bulk-load-column-input` before expecting
  candidates in the ad-hoc Bulk Load flow, which previously needed no column at all and would now fail
  without one.
- **Not run** in this environment — this repo's Playwright suite needs a real SQL Server container and a
  real browser, neither available here. The spec edits above are a source-level update to match the new
  UI shape, not a confirmed passing run; whoever next has a real environment should run
  `golden-path.spec.ts` test 41 for real before calling this fully verified.

## Retrospective

Built and verified as far as this environment allows, 2026-09-23. The one real design decision — column
lives entirely on the reference with no replication-level default at all, versus a `ReconcileConfig`-style
inherit-or-override — was resolved by asking rather than guessing, since either is defensible and this
repo's "no migration" policy means whichever shape ships is the only one anyone gets without a second
breaking change. Chose "moves entirely," matching the user's own framing and the strategy's pre-existing
doc comment reasoning (a strategy's query never knew what its bounds meant; only the caller ever did).

The biggest surprise surfaced along the way: **two of the three UI surfaces that needed a column input
(`BulkLoadForm.tsx`, `ReconcileDeletesForm.tsx`) had never had one at all** — the ad-hoc "pick a strategy
and run it now" flow relied entirely on the strategy's own `Column`, silently, with no field for an
operator to see or change it. Moving the column to the reference didn't just fix the reported bug, it
surfaced (and fixed) a second, quieter gap: an operator using the ad-hoc flow previously had no way to
know or override which column a strategy would run over at all.
