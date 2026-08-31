# Phase 61 — A UI editor for segmenting strategies

**Status**: Complete
**Plan reference**: `architecture/planning/done/segmenting-strategy-editor.md`

## What this covers

A replication-level editor for `SegmentingStrategyConfig` entries — today only creatable/editable by
hand-editing the replication's config file, per phase 58's own named gap.

## 1. `SegmentingStrategiesCard` on the Overview page

New card, list-with-inline-editor (`ColumnMappingEditor`/phase 48's Checks card shape): each strategy's
Name and Kind, edit/remove, "Add strategy." Saves through the existing replication upsert
(`useUpsertReplication`) with the modified `SegmentingStrategies` array — no new CRUD endpoint, same
pattern phase 48 established for checks.

## 2. The add/edit form, by `Kind`

- **DuckDb / SourceSql / TargetSql**: `CodeEditor` (Monaco, `language="sql"`) for `Sql`, a `Column`
  input. Show the existing `.hint.warn` connection notice (reused verbatim from `BackfillForm`'s
  `runsAgainstAConnection` check) for the two connection-bound kinds.
- **Script**: a `ScriptName` select scoped to scripts implementing `ISegmentingStrategy`, plus
  `ParameterForm` bound to that script's declared `ParameterDescriptor[]`.

## 3. Test button

Runs the strategy through the same preview path `BackfillForm.tsx` already calls (reuse the hook/query,
don't fork a second implementation), rendering candidates (label, range, selected) inline in the editor
before the strategy is saved.

## What this phase does not build

- Any change to `SegmentingStrategyConfig`, `ISegmentingStrategy`, or strategy execution — authoring UI
  only.
- A replication-level default/override layer beyond what phase 58 already scoped (mapping-level
  defaults, replication-level strategy definitions — unchanged).

## How to verify when built

- Adding a DuckDb strategy through the editor, saving, and selecting it in `BackfillForm` produces the
  same candidates the editor's own Test button showed.
- Adding a Script-kind strategy renders that script's declared parameters via `ParameterForm` and persists
  the entered values.
- Editing an existing strategy (defined previously by hand in config) round-trips correctly through the
  new editor without altering fields the operator didn't touch.
- The connection-warning hint appears for SourceSql/TargetSql/Script-that-touches-a-connection strategies
  and not for DuckDb ones, matching `BackfillForm`'s existing behavior.
- Full suite green, including a new Playwright flow: author a strategy through the UI, test it, use it in
  a backfill — closing the gap phase 58's retrospective named.

## Open questions

- None — scope and shape are settled by phase 58's own retrospective and existing `VerificationCheckConfig`
  precedent.

---

# Retrospective

A small phase with one thing in it the doc did not anticipate, and one placement decision the doc
could not have made because the screen it described no longer exists.

## The Test button needed a server change the doc did not ask for

The doc says to reuse the preview path `BackfillForm` already calls and not fork a second
implementation. That path takes a *saved* strategy's **name** — it looks the strategy up in the
replication's config. A Test button that only works after saving is not a Test button: it means
committing a strategy to config history in order to find out that its query does not compile, which is
precisely the situation the button exists to remove.

So `SegmentingPreviewService` keeps one runner and gains a second way in. By name, as before, or by
value, for a strategy that exists only in the editor's draft. Everything below the split — the 1:1
check, the connection opening, the error handling that turns a malformed query into a message rather
than a 500 — is the same code both ways, which is what makes "test it before you save it" a claim
about the thing that will actually run rather than about a lookalike. There is a test asserting the two
paths produce identical candidates, because that claim is the whole feature and nothing else would
notice it drifting.

This is a change to the API, which the doc's "authoring UI only" scope did not contemplate. It is
still not a change to `SegmentingStrategyConfig`, `ISegmentingStrategy`, or how a strategy executes,
which is what that scope line was protecting.

## Where the card went, now that the Overview is tabbed

The doc places a new card on the Overview "next to where replication-level things already live", which
described a flat stack of cards. Phase 64 landed first in this build order and turned that stack into
tabs, so the instruction no longer names a location.

It gets **its own tab, called "Reload Segmenting" — the same words the mapping editor's tab uses.**
The two are halves of one idea: the replication defines the named strategies, and a mapping's Reload
Segmenting tab chooses among them. Somebody who has seen the phrase on a mapping and wants to know
where those names come from will look for the same phrase on the replication, and the version of this
that costs them an afternoon is the one where it is somewhere else under a different name.

The alternatives were both worse for the same reason — they are about something else. **Pipeline** is
reader, staging and writer: which components run. **Custom Transforms** is script *bindings* — which
script fills a slot — and a segmenting strategy is not bound to a slot; it is a named definition
referenced by name. Filing it under either would have made "where do I find this" a question with a
memorised answer instead of an obvious one.

## Decisions the phase doc left open

The doc says "None — scope and shape are settled". These came up anyway, all of them small:

- **The Test button asks which mapping only when there is more than one.** A strategy proposes ranges
  over one table's column, and the source-SQL and target-SQL kinds resolve their connection through a
  mapping's endpoints — so a mapping genuinely is required and "test against nothing in particular"
  has no answer. But for the common replication there is one plausible choice, and a mandatory picker
  with one option is a step with no decision in it.
- **Test is a mutation, not a query.** It reads and writes nothing, so a query is the obvious hook —
  and a query keyed on a half-typed SQL string would re-run on every keystroke, which for the two
  connection-bound kinds means hitting a real database while somebody types. It runs when Test is
  pressed.
- **A new strategy starts with a worked DuckDB query rather than an empty editor.** The four required
  column names are the thing most likely to be got wrong, and stating them in prose above an empty box
  is strictly worse than showing a query that already returns them.
- **The connection warning is worded more strongly here than at the picker.** Phase 58 put it where a
  strategy is *chosen*; this is where one is *created*, and the cost being warned about is that it
  runs on every scheduled reload from now on, not that it runs once now.
- **Commit is disabled without a name and a column.** Both are required for every kind, and the column
  is specifically not inferable — the query returns bounds, not the thing they bound. A strategy
  saved without one is a strategy that fails the first time it runs, unattended.
- **Remove takes effect immediately in the draft, with no confirmation**, matching the Checks card. It
  is not saved until Save settings, so the undo is not saving.
- **Editing is by index, not by a copy of the strategy** — the Checks card's arrangement, which is
  what makes Cancel a genuine discard rather than a second edit.

## Verification

- `SegmentingPreviewTests` (6), new — the endpoint had no API-level coverage at all before this. A
  saved strategy proposing its candidates; an unsaved one previewed without being saved, asserting the
  config really is untouched afterwards; **the two paths agreeing**; a query that does not compile
  coming back as a message rather than a 500; a query returning the wrong columns saying so; and a
  missing mapping being a 404. Every case uses a DuckDb strategy, which opens no connection — so they
  run with no database anywhere, the same property that makes previewing on picking safe.
- Playwright 41, new — phase 58's named gap, closed end to end: author a strategy in the editor, test
  it before saving and see its proposed bounds, confirm nothing was written, save, reload, reopen and
  see it round-trip, watch the connection warning follow the kind, then select it in the Backfill form
  and run it — with the strategy's own labels appearing as the runs' segment labels.
- Full suite green: 839 unit, 153 integration, 43 Playwright.

## Open questions

- **Still no integration test driving a source-SQL or target-SQL strategy against a live server.**
  Phase 58 flagged this and it is unchanged: the editor can author one and warns about what it costs,
  but the two connection-bound kinds are still covered only by their refusal paths and by the shared
  runner beneath them.
- **Nothing validates a strategy's SQL at save time.** Test is offered, not required, so a strategy
  that has never been tested can be committed and will fail on its first unattended run. Requiring a
  successful test before saving was considered and rejected — it would make a target-SQL strategy
  unsaveable while its database is down, which is a different problem from the query being wrong.
- **A strategy referenced by a mapping's default segmenting can be removed without warning.** The
  removal is a draft edit like any other, and nothing cross-checks `DefaultSegmenting`'s `Custom`
  entries against the surviving names. The runtime already handles a missing strategy, but the editor
  could say so before the save rather than leaving it to be found.
