# Scripting: layout and legibility

**Status: resolved 2026-08-27 — see Outcome at the end.** Renamed from
`fix-scripts-layout-on-all-screens.md`, because the second sentence turned out to be the larger half
and it is not about layout.

## The note as written

> The newly added scripting features aren't the most intuitive, and they're the first thing you see.  We need to move them to another area and improve the overall usability.

The user needs a simple way to see all scripts that are generated at any phase, including the scripts that are generated internally by this tool.

---

# Outcome — resolved 2026-08-27

Agreed, as `implementation/todo/phase-037-scripting-usability.md`, in three parts.

**The first complaint is literally true and the fix is small.** `OverviewPanel.tsx` renders
`ScriptBindingsCard` at line 76 and `EndpointsCard` at line 85 — the scripts card is *above* the
endpoints card on the replication Overview. Endpoints are what a replication is; script bindings are an
advanced customisation most users will never touch. Phase 23 put the new thing first because it was the
new thing.

**The second is the substantial one, and is not about layout at all.** "See all scripts that are
generated at any phase, including the scripts that are generated internally" is asking for something
that does not exist: a view of the SQL a pass would actually run. A mapping's behaviour is currently
spread across a literal transform per column (22), a script that generates more of them (23),
in-process transforms (24), provisioning DDL (25), four hook points (26), a hook generator (27), a
possible scripted source query (30), and the statements the reader, staging provider and writer build
themselves — and an operator can see none of it without running it and reading the log.

The phase's finding is that **this is nearly free**, and not by luck: every one of those statement
builders is already a pure function separated from execution, because each was extracted for
testability — `MsSqlChangeTrackingStatement` after a real defect in its select list, `WatermarkStatement`
so phase 17's "no config migration" could be a checked claim, and `StagingStatement`,
`BatchReloadStatement`, `DeleteInsertStatement` and `SourceProjection` after them. A preview is mostly
calling those with the arguments a run would give them.

The third part — a **Used by** column on the Scripts list — answers the first question anyone has about
a script they did not write, and a script bound nowhere is worth seeing as such.
