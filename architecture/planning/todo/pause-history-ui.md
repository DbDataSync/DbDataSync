# Pause/resume history UI — follow-up

**Status: minimal placeholder, 2026-08-31.**

Phase 64 builds `PauseEvents` (every pause and resume, its note, timestamp, and who did it) but
deliberately ships with **no UI to view that history** — logged, not surfaced. Confirmed acceptable for
now: the data exists and is queryable; a viewer is a separate, later task.

**Next step, whenever this is picked up**: design where/how `PauseEvents` shows in the SPA — the
replication Status card is the obvious home (it already shows current pause state per phase 64), likely
as a small list or an expand-from-summary, per the open question phase 64 left unresolved. Not scoped
further than that here.
