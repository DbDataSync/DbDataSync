# Phase 15 — ClrKernel Restyle

**Status**: Complete
**Plan reference**: `DataSync Mockups.dc.html` in the Claude Design project
`57cdea73-ae62-4e21-9db3-688c906ceaeb` ("Data sync webapp mockups"), read via `DesignSync`. Seven
frames covering all thirteen states of the existing SPA.

## What was built

A wholesale restyle of `src/DataSync.Web` into the design language of the mockups. Not a re-skin of
the current layout — the design replaces the app's chrome:

| | today | design |
| --- | --- | --- |
| chrome | 56px header, nav links, centred 1100px column | 46px icon rail + 42px breadcrumb bar + 46px tab bar, full-bleed |
| surface | cards on `#f7f8fa`, 8px radius, 20px padding | 7px cards on `#fafaf8`, 34–36px card headers, 13–14px padding |
| accent | `#2f6fed` blue | `#0f7a55` green on warm neutrals (`#eeece7`/`#fbfaf8`) |
| tables | `<table>`, 0.92rem, 8×10 padding | CSS grid rows, 11.5px monospace, 29px headers / 34–40px rows |
| identifiers | body font | `ui-monospace` everywhere a name, host, count or timestamp appears |
| density | roomy | deliberately dense — the design is an operator console, not a marketing page |

Screens, and the routes they land on:

1. **Replications · list** → `/replications`, with an Explorer sidebar of replications.
2. **Replication · overview** → `/replications/:name` Overview. The pipeline becomes three
   selectable stage tabs (Reader → Staging → Writer) with the selected stage's Kind and its options
   as a key/value table — replacing today's raw-JSON textarea.
3. **Table Mappings · edit** → the Mappings tab, with a mappings sidebar and a column-mapping table
   carrying source type and a PK badge.
4. **Runs · live run & history** → the Runs tab: live log panel, backfill panel beside it, and a
   run-history grid with Kind/Segment/Read/Written/Duration/Status and All/Failed/Backfills filters.
5. **Version Control · config commit log** → the History tab, renamed to match the design.
6. **Connections · list** → `/connections`.
7. **Connection · edit** → a new `/connections/:name` route. Today connections are edited inline on
   the list; the design gives editing its own screen, including the `Properties` dictionary that has
   never had a UI.

## What the design shows that the system cannot back

The mockup is populated with a richer product than exists. Rendering those elements would mean
inventing data in a running application, which is a different thing from inventing it in a mockup —
an operator cannot tell a placeholder from a reading. **Each of these is therefore omitted, not
faked**, and listed here so the boundary is a decision rather than an oversight:

- **Environment pill (`prod`) and environment switching** — no such concept exists.
- **`Activity` nav item** — no such page or endpoint.
- **Health and reachability** — "4 of 5 healthy", per-connection `Reachable` column, "reachable"
  pills on endpoints, "Last test" card, "reachable · 12ms". Nothing tests connections.
- **`Import config`, `Diff vs production`, `Revert`** — no endpoints.
- **Lag, Duration-as-SLA, `Mode` (CDC/Batch), "Last 24 hours" counters and the sparkline** — no
  aggregate or timing data is recorded. (Run *duration* is the exception: it is `endedAtUtc -
  startedAtUtc` and is real, so the Runs grid keeps it.)
- **`Used by N replications` on a connection, and the `Used by` card** — computable only by scanning
  every mapping of every replication; deferred rather than faked.
- **Driver badges for `ORA`/`PG`, `Oracle`/`Wallet` auth, `Vault` credential store, per-stage
  `Default` values, `Transform` values, `Write mode`** — none of these exist in the config model.
  The columns that are real (`Transform` is a field on `ColumnMapping`) are kept; the rest are not.
- **`Endpoints` on the replication Overview**, presented as "used by every table mapping". This
  contradicts the data model: source and target belong to each *table mapping*, not to the
  replication. Omitted from Overview and kept on the mapping editor, where it is the real thing.
  Worth raising with the design's author — it may be a deliberate product direction rather than an
  oversight, in which case it is a backend change, not a styling one.

## How to verify when built

- `npx tsc -b` clean (not `--noEmit`, which type-checks nothing here — see phase 010), `oxlint` no
  worse than its current two warnings.
- Every screen walked by hand against the mockup at 1240px.
- The Playwright suite last: it asserts on `data-testid` and on visible text, and this rewrite moves
  both. Test IDs are preserved where the element survives; the suite is repaired at the end.

## Open questions

- The `Endpoints`-on-a-replication mismatch above.
- The design has no empty, loading or error states. Today's `ErrorBanner`, `empty-state` and
  `Loading…` are restyled to fit rather than dropped, since the app has to render them.


## What the implementation added beyond restyling

Three pieces of the design are genuinely better fits for the data than what they replace, and are
functional rather than cosmetic:

- **The pipeline as three selectable stages.** Reader → Staging → Writer, with the selected stage's
  implementation and its settings below it. It replaces three stacked Kind pickers each with a
  raw-JSON textarea. A stage's Kind and its options belong together, and the options are a string
  dictionary — which a Setting/Value table states plainly and a JSON blob does not.
- **`KeyValueTable`**, used for both stage options and a connection's `Properties`. The latter has
  never had a UI at all; it was reachable only by editing YAML by hand.
- **A connection editor with its own route** (`/connections/:name`, `/connections/new`) rather than a
  form appended below the list.

Also real, and kept because the data exists: run **Duration** (`endedAtUtc − startedAtUtc`), the
source column's SQL type and the target's PK badge in the column-mapping grid, per-mapping column
counts in the sidebar, and the All/Failed/Backfills run filters.

`Layout`, `KindSelect`, `kindOptions` and `JsonOptionsEditor` are deleted — the shell, the stage
picker and the key/value table replace them.

## How this was verified

Every screen was rendered against a live environment (`tools/dev-harness up`) at the design's own
1240px and read back: replications list, new-replication form, overview with each pipeline stage
selected, table mappings, runs, runs with the backfill panel open, version control, connections, and
the connection editor. Three defects were found and fixed that way rather than by reasoning: the
backfill column drifted left when no live run sat beside it, the Port label was left-aligned where
the design right-aligns it, and "All 1 table mapping" read badly in the singular.

`npx tsc -b` clean. `oxlint` reports three `set-state-in-effect` warnings — two pre-existing
(`useRunHub`, `OverviewPanel`) and one new (`ConnectionEditPage`), all the same
seed-a-draft-from-the-server pattern the codebase already used.

**The .NET side is untouched** — this phase changed no API, contract or type.

## Repairing the Playwright suite

Done in its own commit, after the restyle, so the large visual diff stayed reviewable. Ten tests, all
green. What it took is worth recording, because most of it was the suite finding real gaps rather
than merely needing to be told about new markup:

- **The design has no heading elements.** Every page title was a `<span>`, so `getByRole('heading')`
  found nothing — and a screen reader would have found nothing either. Fixed in the markup rather
  than by weakening the assertion: pane titles became `<h1>`/`<h2>`, and on the replication detail
  screens — whose subject is named only in the breadcrumb — the final crumb renders as the `<h1>`.
- **That first fix then produced two `<h1>`s reading "Replications"** on the list screen, one in the
  breadcrumb and one in the pane. The crumb-as-heading is now opt-in per screen (`heading: true`),
  set only where the pane has no title of its own. Two headings reading the same word is worse than
  none for anyone navigating by headings, so the test catching it was doing its job.
- Assertions updated where the UI genuinely changed: run status reads as the design's lowercase word,
  the run-kind chip is uppercase, the live panel separates counts with `·`, column mappings are
  CSS-grid rows rather than `<table>` rows, the mappings list is a sidebar, and connections open
  their own screen.
- **Test 10 was rewritten rather than patched.** It checked that invalid JSON in the options textarea
  disabled Save; that textarea no longer exists. It now exercises what replaced it — the stage picker
  swapping both Kind picker and options, and a stage option surviving a save and reload, which is the
  only proof it reached the config repo.

## What's explicitly not built

Everything in "what the design shows that the system cannot back" above.
