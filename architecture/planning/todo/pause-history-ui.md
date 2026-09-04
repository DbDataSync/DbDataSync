# Pause/resume history UI — follow-up

**Status: minimal placeholder, 2026-08-31.**

Phase 64 builds `PauseEvents` (every pause and resume, its note, timestamp, and who did it) but
deliberately ships with **no UI to view that history** — logged, not surfaced. Confirmed acceptable for
now: the data exists and is queryable; a viewer is a separate, later task.

**Next step, whenever this is picked up**: design where/how `PauseEvents` shows in the SPA — the
replication Status card is the obvious home (it already shows current pause state per phase 64), likely
as a small list or an expand-from-summary, per the open question phase 64 left unresolved. Not scoped
further than that here.

## Widened, 2026-09-04: one history over both grains

`planning/todo/reset-a-mappings-watermark-from-the-ui.md` introduces a **table-mapping-level** hold —
`ReadHold`, whose values include `Paused` — alongside phase 64's replication-level pause. That makes
this doc's open question bigger and, usefully, easier to answer.

Two histories would be the wrong shape. `PauseEvents` is keyed by `TaskName` with no mapping column, so
a second grain would otherwise mean a second table, a second retention story and a second screen — for
what is one question an operator asks: *who stopped this, when, and why*.

So the attractive answer is **one table covering both**, and one screen over it:

- `PauseEvents` gains a nullable mapping column. Null means the replication itself, which is what every
  existing row already is — so the migration is a column add with no backfill, in the shape phases 72,
  87 and 94 have all used here.
- A hold and a resume at either grain write to it identically, and the note travels with the event.
- The viewer this doc was always about then shows both, in one list, with the grain visible per row.

**Notes are part of the same follow-up.** Recording *why* a table was held is what makes the history
worth reading at all — a list of timestamps answers "when" and leaves the question people actually have
unanswered. Phase 64 already carries a note on a replication pause; the table-level hold should take one
on the same terms.

Still not urgent, and still not needed for the mechanism to work: a hold does its job without anybody
being able to read its history. But it should be built as one thing when it is built, rather than the
table-level half being bolted on afterwards — which is precisely what this doc exists to stop happening
twice.
