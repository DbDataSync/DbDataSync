# Script development and testing

**Empty as of 2026-08-27 — nothing to plan against yet**, though the title suggests a real gap and one
that has been recorded elsewhere.

What exists today: a script is written in the Scripts editor, compiled on save with diagnostics as
Monaco markers (phase 28), and then bound — and the next thing that happens is a replication run. There
is a **Check** action that compiles, and nothing that *executes* a script against sample input.

Phase 23's own plan named this and it was not built:

> a **Test** action that runs the script against sample input and shows the result. The connection-test
> flow from phase 19 is the precedent — an operator gets to find out it works before a run does.

Phase 30's plan said the same, more sharply, about scripted queries:

> More important here than for any other script slot, because a wrong change query is not a crash — it
> is a target that silently disagrees with its source.

And phase 23 left the open question of **where preview data comes from**: reading N rows from the real
source is the honest sample and is also a real query against production.

**Next step**: confirm this is what the title means. If so there is most of a phase already written
across those two documents.
