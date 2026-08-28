# Phase 37 — Making the scripting features legible (planned)

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/scripting-usability.md`.

## The complaint, and it is fair

> The newly added scripting features aren't the most intuitive, and they're the first thing you see.
> We need to move them to another area and improve the overall usability. The user needs a simple way
> to see all scripts that are generated at any phase, including the scripts that are generated
> internally by this tool.

Both halves are true and the first is literal. `OverviewPanel.tsx` renders `ScriptBindingsCard` at line
76 and `EndpointsCard` at line 85 — **the scripts card is above the endpoints card** on the replication
Overview. Endpoints are what a replication *is*; script bindings are an advanced customisation most
users will never touch. Phase 23 put the new thing first because it was the new thing, which is the
oldest reason to get an ordering wrong.

## Part 1 — Demote the bindings

Cheap, and most of the complaint.

- **Below the primary content, not above it.** Endpoints, then pipeline, then scripts on the
  replication Overview; connection details, then authentication, then scripts on the connection editor;
  columns, then scripts on the mapping.
- **Collapsed when nothing is bound.** A card reading "no scripts bound" occupying a screen's best
  space is pure noise for the majority who will never bind one. Collapsed it is one line that says the
  feature exists.
- **Named for what it does.** "Scripts" is what they are; *Custom transforms and providers* is what
  they do, and the card head should say which slots do what rather than showing four slot identifiers
  and a select each. `sqlColumnExpression` is a good key and a bad label.

## Part 2 — Show the SQL that will actually run

This is the substantial half, and it is what "see all scripts generated at any phase, including the
scripts that are generated internally" is asking for.

Right now a table mapping's behaviour is spread across: a literal `Transform` per column (phase 22), a
`sqlColumnExpression` script that generates more of them (phase 23), value and row transforms that run
in process (phase 24), provisioning DDL (phase 25), four lifecycle hook points (phase 26), a C# hook
generator (phase 27), possibly a scripted source query (phase 30) — and underneath all of it, the
statements the reader, staging provider and writer generate themselves.

An operator cannot see any of that without running it and reading the log.

### A preview endpoint, per table mapping

```
GET /api/replications/{name}/table-mappings/{mapping}/preview
```

returning, in execution order, every statement a pass would run:

| stage | where it comes from |
| --- | --- |
| pre-stage hooks | `HookRenderer`, plus phase 27's generator |
| the source read | `SourceProjection` + `WatermarkStatement` / `BatchReloadStatement` / `MsSqlChangeTrackingStatement`, or phase 30's builder |
| staging DDL and insert | `StagingStatement` |
| post-stage hooks | `HookRenderer` |
| the write | `DeleteInsertStatement` and the MERGE writers' builders |
| post-load hooks | `HookRenderer` |

Each labelled with which stage it belongs to, whether it is built-in or came from an operator's SQL or
a script, and — for a script — which script and at which binding level.

### This is nearly free, and the reason is not luck

**Every one of those statement builders is already a pure function, separated from execution.** That
separation was made deliberately and repeatedly, for testability: `MsSqlChangeTrackingStatement` exists
because "the select list had a real defect in it" and needed asserting without a server;
`WatermarkStatement` was extracted in phase 17 so "no config migration" could be a checked claim;
`StagingStatement`, `BatchReloadStatement`, `DeleteInsertStatement` and `SourceProjection` all followed.

So the preview is mostly *calling functions that already exist with the arguments a run would give
them*. What it additionally needs is a live connection for catalog columns — which the metadata
pickers already open on every interaction.

### In-process transforms are named, not shown

A `valueColumnExpression` or `rowTransform` generates no SQL; it is C# running on rows. The preview
names it, says which columns it declared and where it is bound, and links to the script. Pretending it
has a statement would be worse than saying it does not.

### Where it lives

A **Preview** tab beside the mapping editor, and a link to it from the pipeline card on the replication
Overview. Monaco renders it read-only, with the same SQL syntax highlighting phase 28 brought in.

## Part 3 — Make the Scripts list say what a script is doing

The Scripts screen lists name, kind, entry type, description and enabled. It does not say whether a
script is *bound to anything*, which is the first question anyone has about a script they did not
write.

A **Used by** column: the connections, replications and mappings that bind it, resolved by scanning the
config store — the same scan `ListScripts` already walks. A script bound nowhere is worth seeing as
such, because that is usually either a mistake or something safe to delete.

## What this phase does not build

Any new script slot, contract or capability. This is entirely about making what exists visible and
appropriately placed.

Editing SQL from the preview. It is read-only; the place to change a statement is the thing that
generated it, and a preview that could be edited would invite the question of what happens to the
generator.

## How to verify when built

- Playwright: the replication Overview's first card is Endpoints, not Scripts; the scripts card is
  collapsed when nothing is bound and expanded when something is.
- The preview endpoint returning statements in execution order for a mapping with: a literal transform,
  a scripted one, a hook, and a scripted source query — each labelled with its origin.
- A mapping with an in-process transform showing it named rather than as SQL.
- The Scripts list showing a bound script's binding sites and an unbound one as unused.
- `Category=Integration`: the preview's source-read statement matching what the reader actually issues.
  If those two can drift, the preview is a lie, and this is the test that keeps it honest.
- Full suite green.

## Open questions

- **Preview for a mapping that cannot run** — no endpoints resolved, a script that will not compile.
  Probably: show what can be built and say plainly what could not, since a preview is exactly where an
  operator would want to find that out.
- **Segments.** A backfill's statements depend on the segment. Preview the unsegmented form, with a
  note, or let the operator supply one? The second is more useful and more UI.

---

# Retrospective

All three parts built. The first was as cheap as the plan said; the second was as substantial, and
the thing that made it work was a decision about *where the answer lives* rather than any of the code.

## Ask the component, do not reconstruct it

Every statement builder in this codebase is already a pure function, separated from execution — that
separation was made deliberately and repeatedly, for testability. So the preview could have been
assembled from outside by calling them, and that is exactly what the plan describes as "nearly free".

It would also have been a **second place** deciding which builder to call with which arguments. The
day those two disagree, the preview does not break: it keeps rendering something plausible and
authoritative that is no longer what runs. That is worse than having no preview at all.

`IStatementPreview` puts the answer inside the component, built from the same inputs the run gives it
— `PreviewRequest` mirrors the run-time argument list on purpose. A component that does not implement
it is reported as not describing itself, rather than omitted: silence would read as "this stage runs
nothing", which is the one thing it definitely does not mean.

## Previewing found three copies of the same SQL

Writing the describers meant every statement had to come from a builder, and three did not:

- The change-tracking full load was inline in the reader. It is `BuildFullLoad` now.
- Both halves of each delete/insert writer were inline. They are builders now.
- The MsSql reload reader had its own copy of the generic `BatchReloadStatement` — the same SELECT,
  written twice. It uses the shared one; the two readers differ in how they discover columns and bind
  segment values, not in what they select.

Each was a place the preview and the run could have diverged. Being unable to write the preview
without fixing them is the point.

## The preview is dated, and has to be

An incremental reader's statement depends on the stored watermark. A preview that always showed the
first-pass form would be right exactly once and wrong for every pass after — so it reads the
watermark and describes what the *next* pass would issue. `WatermarkKey` moved out of the TaskRunner
into Core to make that possible: two spellings of that key would be two answers to "where did this
replication get to".

## The test that keeps it honest does not read the SQL

Comparing the preview's text to an expected string would test that the preview matches itself.
`PreviewIntegrationTests` **executes** the statement the preview showed against the real database and
asserts the pass loads exactly the rows that statement returns. If the two ever diverge — whatever the
text says — that fails.

## And it immediately showed something invisible

The Playwright test was written asserting the preview would show `UPPER({{column}})`, the transform
test 05 writes by hand on `Name`. It does not. Test 15 binds a `sqlColumnExpression` script to the
same mapping, the script wins, and what actually runs is `REVERSE(base.[Name])`.

That is correct behaviour — a script binding replaces the literal, atomically, by design. It was also
completely invisible: the only way to discover it was to run a pass and look at the data. The preview
names the generated expression, the script that produced it and the level it is bound at, in one line.
The test asserts that now, which is a better test than the one intended.

## Collapsing by attribute does not collapse

`hidden={!open}` on `.card-body` renders a fully visible card: `.card-body` sets `display: flex`, and
a class rule beats the user agent's `[hidden]`. Not rendering the body is both simpler and impossible
to override. Playwright caught it on the first run, which is the argument for asserting that a
collapsed thing is *not visible* rather than that a toggle exists.

## Hooks bind differently, and a naive scan would have lied

The Used-by column scans slot bindings and hook lists separately, because a hook binds by name from a
point's list rather than through the slot hierarchy. Scanning `Scripts` alone would have reported a
reusable SQL hook in daily use as **unused** — precisely the wrong answer for the one thing that
column exists to say, and one an operator might act on by deleting it.

## Verification

- `PreviewIntegrationTests` (3, `Category=Integration`) — the previewed read executed and matched
  against what the pass loads; every stage present in run order with each statement's origin; and the
  read switching from the full-load form to the incremental one once a watermark exists.
- `ScriptUsageScannerTests` (4) — bindings found at all three levels, a hook bound by name counted, an
  empty store, and a binding that names no script (explicit "none", inline SQL) not counted as a use.
- `ScriptsControllerTests` — the list's new shape, an unbound script reporting so, and slots carrying
  readable labels.
- Playwright 04b — the Overview's first card is Endpoints, and the scripts card is one line until
  something is bound.
- Playwright 15b — a bound script names its binding site; an unbound one reads "unused".
- Playwright 19 — the whole preview: stages in order, the generated expression attributed to its
  script and level, the in-process transform named as having no SQL, the reader's own `CHANGETABLE`
  statement, and the editor read-only.
- Full .NET suite green: 540 tests. Playwright: 21 green. `tsc -b` clean, `oxlint` unchanged at four.

## Open questions, both answered as the plan guessed

- ~~**Preview for a mapping that cannot run.**~~ Shows what can be built and says plainly what could
  not: a missing mapped column, a table without the primary key Change Tracking needs, a script that
  will not run, a hook naming a script that is not there. A preview is exactly where an operator would
  want to find that out, so those are surfaced rather than turned into a failed request.
- ~~**Segments.**~~ Previewed in the unsegmented form, with a note saying a backfill supplies its own
  and narrows it further. Letting the operator supply one is more useful and more UI; it belongs with
  the backfill form rather than here.
- **The mapping editor and the preview are separate screens**, and deliberately: the preview is about
  the mapping *as saved*, which is not what an editor with unsaved changes is showing. It is reached
  from the editor and from the pipeline card. Whether it should live as a tab within the editor is a
  question for whoever finds the round trip annoying.
- **A component that implements no describer** is reported honestly, and there are none today. That
  line exists for a driver added later, and is the thing to check when one is.
