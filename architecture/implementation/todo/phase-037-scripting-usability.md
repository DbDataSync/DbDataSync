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
