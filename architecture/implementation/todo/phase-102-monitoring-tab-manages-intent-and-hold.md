# Phase 102 — the Monitoring tab shows and manages every mapping's intent and hold

**Status**: Planned, not started.
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
