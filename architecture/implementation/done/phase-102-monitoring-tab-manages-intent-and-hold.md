# Phase 102 — the Monitoring tab shows and manages every mapping's intent and hold

**Status**: Done.
**Plan reference**: `architecture/planning/done/reset-a-mappings-watermark-from-the-ui.md`.
Last of three — 100 stores it, 101 makes it mean something, this puts it where an operator works.

## Why the Monitoring tab

Because it is already the per-mapping operational view: a row per mapping, source and target resolved
through the same inheritance the server applies, and how far behind each one is. An intent and a hold
are the same kind of fact about the same row. Putting them anywhere else would mean an operator who has
just been told on this screen that something is wrong has to leave it to do anything about it.

## What this phase will build

### 1. Intent and hold on each mapping's row

`MappingLagRow` gains them beside the lag cell. It already carries `data-lag-state` for exactly this
kind of at-a-glance state, and intent and hold want the same treatment — both for the operator and
because it is what an E2E assertion can read.

The row is already on phase 96's `.grid-row auto` variant, so it can grow without the overflow that
variant was created to fix. Worth remembering *why* that variant exists: this row outgrew a fixed
height once already, silently, when phase 88 added a third line to the lag cell.

**A held mapping must not read as merely idle.** A hold is the interesting state on this screen — it is
the one that means nothing is happening and will not until somebody acts.

### 2. The actions

Per mapping: set the intent, pause and resume, and recover from a hold. All through phase 100's
endpoints.

- **Only intents the mapping's reader declares** (phase 101) are offered. The support matrix is not
  uniform — the watermark reader has no honest `ChangesFromEarliest`, batch reload has no incremental
  mode at all — and offering one that cannot be honoured is the button that lies.
- **Recovery from `PositionExpired` is a choice, presented as one.** `ChangesFromEarliest` catches up
  from the surviving floor in minutes and loses only what the source genuinely discarded;
  `InitialLoad` is the full reload for when that is not good enough. Today the operator gets `Resync`
  and no alternative, which on a large table is hours.
- **`ChangesFromLatest` is deliberate data loss** and its confirmation has to name what is being
  skipped rather than ask "are you sure". It probably wants a phase 43 verification offered beside it —
  that is what would tell an operator whether "this table is already in sync" was true.

### 3. Two pause grains, both visible

Phase 64's pause is per replication (`Tasks.Paused`); a `ReadHold` of `Paused` is per table. Both will
be true at once sometimes, and the screen has to make that legible: **a table resumed under a paused
replication must read as "still not running, and here is why"**, not as running. A stated precedence,
shown, or an operator resumes one and cannot work out why nothing happens.

### 4. The default setting

`DefaultReadIntent` is config, so it belongs with the other settings — the replication's on its
settings screen, the mapping's on its pipeline tab beside the other overrides, showing INHERITED the
way those already do. What it means deserves a sentence on screen rather than only in a doc: setting it
to either `Changes…` value is how an operator says **the application must never choose a full load on
its own**, and a brand-new mapping under that setting will never read the rows that predate its feed.

## How it will be verified

E2E, stubbed at the network boundary — the pattern `lag-monitoring.spec.ts` and
`run-details-dialog.spec.ts` already use, and the only SPA test layer this repo has (see
`planning/todo/spa-has-no-component-test-layer.md`).

- A held mapping renders as held and does not read as idle.
- Only declared intents are offered, driven from a stubbed capability payload — including the watermark
  reader's missing `ChangesFromEarliest`, which is the case a uniform UI would get wrong.
- Recovering from a hold issues the expected call and the row stops being held.
- `ChangesFromLatest` requires a confirmation that names what is skipped.
- A table resumed under a paused replication still reads as not running.
- The lag cell in its fullest state *plus* the new content stays inside its row — phase 96's assertion
  extended, because that is the row this phase makes taller and the defect it had was invisible until
  asserted.

## What this phase will not do

- **No new server behaviour.** Everything here calls phase 100's endpoints against phase 101's
  behaviour. If something is missing, it is a gap in one of those, not a thing to add here.
- **No pause history or notes UI.** `planning/todo/pause-history-ui.md`, which should cover both grains
  in one table and one screen when it is built.

## Open questions to resolve during implementation

- Whether the actions live inline on the row or behind a per-mapping detail popup. Inline is fewer
  clicks; a popup has room for the confirmation text `ChangesFromLatest` needs, and
  `RunDetailsDialog` is the precedent for "the row is a summary, the popup is where you act".
- Whether a mapping with several sources — several rows, several intents, several holds — is one row
  with a combined state or several. The key is per source table; the screen currently shows one row per
  mapping.

## Outcome

Resolved during implementation: the actions live behind a per-mapping popup (`MappingReadStateDialog`),
following `RunDetailsDialog`'s precedent, opened from a "Manage…"/"Recover…" link beside the row's
intent and hold text. A mapping's several sources was moot in practice — every mapping in this codebase
still has exactly one, per `ResolveWatermarkKey`'s own 1:1 assumption — so the row stayed one per
mapping, unchanged.

This phase started from a prior session's WIP, paused mid-implementation by a rate limit (commit
`361d407`). Almost everything in that WIP was already correct and complete on inspection — the
backend `SupportedIntents` plumbing, `readIntent.ts`/`holdState.ts`, `ReadIntentSetting`,
`MappingReadStateDialog`'s picker/recovery/data-loss flows, `MonitoringPanel`'s row wiring, the
`DefaultReadIntent` settings on both `OverviewPanel` (replication level) and `MappingPipelineCard`
(mapping level, INHERITED badge and all), `TableMappingForm`'s threading of the field into the save
payload, and the API client/hooks/types — all cross-checked line by line against the phase doc and
against phase 100's actual controller (`TableMappingsController.GetReadState`/`SetReadState`, which
predates this phase and needed no changes) and found to match. This session's job was mostly
verification, plus the two real fixes below that only showed up once the E2E suite was actually run
end to end rather than read as a diff.

### How it was verified

**`dotnet build` / `tsc --noEmit` / `npm run build`**: all clean, re-confirmed after the fixes below,
not just accepted from the coordinating session's earlier pass.

**Backend tests.** `DbDataSync.Drivers.Abstractions.Tests`: full suite, not only the filtered
`DriverCapabilityOptInTests` — 62/62 pass, confirming `ReaderCapability`'s new required
`SupportedIntents` parameter (the only constructor call site is `DriverRegistry` itself) broke nothing
downstream. `DbDataSync.Drivers.DuckDb.Tests`: 33/33 pass. `DbDataSync.Drivers.MsSql.Tests` (118
failures) and `DbDataSync.Drivers.Postgres.Tests` (26 failures): both need a live database this sandbox
does not have — confirmed by the failures all being connection timeouts, not assertion failures, and
unrelated to anything this phase touches. `DbDataSync.Drivers.Generic.Tests`: 2 pre-existing failures
in `PipelineStatementTests`, a CRLF-vs-LF string comparison — a file this phase never touched, an
environment artifact of this Windows checkout, not a regression.

**E2E.** Same blocker phases 101 and 103 already hit: this sandbox has no Docker, and
`global-setup.ts` shells out to it unconditionally, so `playwright test` cannot run at all here.
Worked around exactly as phase 103 did, never committed: built the API, started it by hand with
`playwright.config.ts`'s own env vars, started `vite` by hand with `--host 127.0.0.1`, and pointed a
scratch config (no `globalSetup`/`globalTeardown`/`webServer`) at the two running servers.

- `monitoring-intent-and-hold.spec.ts` (new, 6 tests) — **all pass**, both on the first run and after
  the fixes below (which changed no assertion, only rendering).
- `monitoring-restructure.spec.ts`, `lag-monitoring.spec.ts`, `run-details-dialog.spec.ts`,
  `runs-watermarks-refresh.spec.ts`, `mapping-metadata-cache.spec.ts`, `mapping-column-add.spec.ts`,
  `duckdb-query-source.spec.ts` — **all pass** (37 tests total across these seven files plus this
  phase's own), confirming the new per-row `useMappingReadState`/`useCapabilities` calls this phase
  adds to every Monitoring row do not break screens that do not stub those routes (React Query just
  reports them as loading/errored, which none of these specs assert against). One single flaky failure
  in `lag-monitoring.spec.ts` on the very first combined run, gone on every repeat (including 3x in
  isolation and a second full combined run) — recorded as environment flake, not a regression.
- `golden-path.spec.ts` (real SQL Server) — **not run**, no SQL Server reachable in this sandbox.
  Grepped for anything this phase touches (`read-state`, `ReadIntent`, `defaultReadIntent`,
  `SupportedIntents`, `ReaderCapability`) — no matches, so nothing in it needed updating.

**Screenshots.** Four new ones from this phase's own spec (`90`–`93`), regenerated a second time after
the fixes below to confirm the rendered result. Screenshots from specs this phase did not touch came
out byte-different on this run too (the same real-time-text-rendering noise phase 103 already
documented) and were reverted rather than churned.

### Judgement calls

- **Found and fixed a real rendering bug the WIP's own E2E spec could not have caught**, because the
  spec only asserted the confirmation banner's text was present, not that it was legible.
  `read-state-data-loss-warning` rendered its sentence as a column of single words instead of a
  paragraph, overflowing the dialog by more than double its width (`scrollWidth` 1086px against a
  428px box) — visible only by actually opening the dialog and looking, which is exactly why this
  phase's own doc insists on E2E over trusting the diff. Root cause: `MappingReadStateDialog` (like
  every dialog in this codebase) is mounted inline in the row that opens it rather than through a
  portal, and this row's cell is a direct child of `.grid-row`, whose `.grid-row > *` rule sets
  `white-space: nowrap` (for the lag/source cells' own truncation) — a property that inherits straight
  through `position: fixed` into the modal's own text, suppressing wrapping there too. Fixed generally,
  once, in `index.css`'s `.modal-backdrop` (`white-space: normal`) rather than only in this one dialog,
  since every current and future modal mounted from inside a row would otherwise inherit the same bug
  silently.
- **A second, independent bug in the same banner**, uncovered only once the `white-space` fix let text
  wrap at all: `.banner` is `display: flex`, and this banner's several top-level inline children (a
  text run, a `<strong>`, a `<span>`, another text run) each become their own flex item and lay out as
  narrow side-by-side columns instead of one paragraph. Fixed by wrapping the whole sentence in a
  single `<span>`, matching the one-content-item shape `ErrorBanner`/`RestartRequiredBanner` already
  use correctly. **The identical latent bug exists in at least two pre-existing files this phase did
  not otherwise touch** — `OverviewPanel.tsx`'s `scd2-delete-blind-warning` (predates this phase) and
  `StatusCard.tsx`'s paused-replication banner, whose pause note visibly renders as a second column
  beside the "Paused." text in this phase's own `93-monitoring-paused-under-replication-pause.png`
  screenshot, purely incidentally. Left unfixed and flagged here rather than patched: neither file is
  otherwise in scope for this phase, and fixing them has no test coverage in this task.
- **The E2E harness's blocker is Docker's absence**, not the environment note's webServer flakiness —
  same finding as phases 101 and 103, reconfirmed rather than assumed.
