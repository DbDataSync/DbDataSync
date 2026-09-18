# Phase 156 — Branching methodology (dev/test/main) and phase numbering (letter+number)

**Status**: Built and verified 2026-09-17. See the Retrospective below. **The last phase issued under
the old all-integer scheme, deliberately** — see `architecture/implementation/README.md`'s new "Phase
IDs" section, which this phase writes.
**Plan reference**: none — raised directly in conversation, two related but independent problems solved
in one pass because both trace back to the same root cause: every concurrent session/worktree sharing one
flat namespace (one branch, one incrementing integer) with no structural collision protection.

## Why this phase exists

This session's own history is the evidence: `phase-150` and `phase-154` were each independently claimed
by two different concurrent sessions, discovered only at rebase time as real filename collisions needing
manual reconciliation ("Reconcile with main's phase 154 after the rebase," "Reconcile with main's MySQL
and Oracle drivers after the rebase" — both real commits from this session's own pulled history). The
same root cause shows up twice, at two different layers:

- **Branching**: every session pushed straight to `main` — a single branch serving as both "everyone's
  latest work" and "what a release is cut from," with nothing between "just landed" and "shipped."
- **Phase numbering**: every phase doc raced for the next integer in one shared, global sequence, with
  the check-then-claim step ("what's the highest number currently in `todo/`+`done/`?") happening at
  slightly different times on different branches with no lock between them.

Neither problem is really about concurrency itself — concurrent sessions are the whole point of how this
repo gets worked on — it's about the *namespace* concurrent work has to share being flat, with no
structural separation between independent lines of work.

## What this phase builds

### Branching: `dev` → `test` → `main`

Full design in `architecture/branching-and-releases.md`, built the same pass:

- `dev` is now where everyone pushes — the exact fast-forward-only discipline already used against
  `main`, retargeted.
- `test` is a promoted, always-CI-green snapshot of `dev`, moved automatically by a new workflow
  (`.github/workflows/promote-test.yml`) that reacts to `CI`'s own completion on `dev` via
  `workflow_run` and fast-forwards `test` to match — never force-pushed, and refuses outright if `test`
  is ever found holding a commit `dev` doesn't (a state nothing is supposed to be able to produce, since
  nothing else pushes to `test` directly).
- `main` is promoted from `test` **deliberately** — a PR, opened and merged by a human or an explicitly
  authorized agent, never automatic. `release.yml`'s own `release/v*` tag-based flow is unchanged; it
  still cuts from `main`.
- `.github/workflows/ci.yml`'s triggers widened from `main` alone to `[dev, test, main]`, for both `push`
  and `pull_request` — the same checks, run at every stage rather than only the last one.

**Branch protection is deliberately not turned on.** Enforcing "no direct push to `main`/`test`" would
immediately affect every session that doesn't yet know this doc exists and is still pushing straight to
`main` out of habit. That cutover needs its own explicit go-ahead, not to happen as a side effect of
writing the intended flow down — named as an open item in `architecture/branching-and-releases.md`'s own
"Not yet decided" section rather than silently deferred.

### Phase numbering: letter + number, not one shared integer

Full design in `architecture/implementation/README.md`'s new "Phase IDs" section, built the same pass
(and corrected once — see the Retrospective — before anyone else had reason to use it):

- `phase-<N><Letter>-<kebab-slug>.md` — the number, then one uppercase letter, then a slug. Numbering
  itself is unchanged from before: check `todo/`/`done/` for the highest one in use, take the next.
- The letter is picked once per session/worktree — random, ideally checked against letters already in
  `todo/`/`done/` — and reused for every phase doc that session writes, whatever each one turns out to be
  about. It carries no meaning of its own; it exists purely so that the rare moment two sessions land on
  the same *number* (exactly what happened twice already — `phase-150`, `phase-154`, each independently
  claimed and only caught at rebase) produces two coexisting filenames (`157Q`, `157M`) instead of one
  real collision needing manual reconciliation.
- The bare number is still the normal way to refer to a phase in conversation or in a cross-reference —
  "phase 157" — exactly as before. The letter only has to be said out loud the rare time a number
  actually collided and there's a real "which one" to answer.
- **Prospective only.** `phase-001` through `phase-155` keep their names; `phase-156` (this one) is the
  deliberate last integer-only phase, named in its own header so the cutover point is unambiguous to
  anyone reading history later rather than inferred from a date.

## What this phase does not do

- Does not rename any existing phase doc, under either scheme.
- Does not turn on branch protection for `dev`/`test`/`main` — named as an open decision, not made here.
- Does not automate opening the `test` → `main` PR — named as a plausible small follow-up in
  `architecture/branching-and-releases.md`, not built.
- Does not change how `architecture/planning/todo/`'s own unnumbered, kebab-slug planning docs work —
  that convention already avoids the exact collision this phase fixes for phase docs, by never numbering
  at all; nothing here needed to touch it.

## How to verify

- Both new/edited workflow files (`ci.yml`, `promote-test.yml`) parse as valid YAML.
- `dev` and `test` branches exist on `origin`, both pointed at the same commit as `main` at the moment
  they were created.
- The real proof of `promote-test.yml` — a genuine `workflow_run` firing after a real `CI` success on
  `dev`, and a real fast-forward landing on `test` — is not observable from this environment, the same
  honest gap phase 153/154's own docs already named for their own CI changes. Watch the first real push
  to `dev` after this lands.
- The real proof of the numbering scheme is simply whether the next phase doc anyone writes uses it.

## Retrospective

Built and pushed 2026-09-17, in one pass, from a short design conversation rather than a written plan —
the "Plan reference" above is honestly "none" rather than a fabricated pointer.

### The two problems share one shape, which is why one phase covers both

Both fixes are the same move at different layers: replace one shared, flat, racy namespace (a branch
everyone pushes to; an integer everyone increments) with something that keeps the number's good property
— a short, easy-to-say sequence, still checked-and-incremented exactly as before — while adding a cheap,
low-cardinality tag that only has to do real work on the rare occasion two independent claims land on the
same number at once. `dev`/`test`/`main` gates promotion instead of publishing everything straight to the
branch releases are cut from; the letter disambiguates a number collision after the fact instead of trying
to prevent one. Neither fix removes the underlying concurrency (see `architecture/branching-and-releases.md`'s
own "Why three, not one" section for the branching side of this) — both just move the cost of a collision
somewhere cheaper to pay it.

### Got the letter's actual job wrong on the first pass — corrected in the same session

The first draft of this section (and of the README's "Phase IDs" section it points to) read the user's
one-sentence suggestion — "a prefix random letter followed by the incrementing number" — and built a
letter *per lineage*: a fresh random letter for each new topic of work, reused only by phase docs judged to
belong to that same thread, with the number resetting to count within a letter rather than across the whole
repo. That's a plausible-sounding design and it is not what was asked for. The user caught it directly:

> If that's how you determine lineage, you'll burn through the alphabet in less than a week… I said
> *random* letter for a reason. If 2 sessions use the same number, I can distinguish with the letter, if
> they don't collide, I can just use the number.

The tell was in "random" — a letter assigned by topic isn't random, it's a classification decision, and a
classification decision needs an agent to keep making judgment calls about what counts as the same lineage,
exactly the kind of ongoing overhead a short reference ID shouldn't carry. The actual design is simpler
than what got built first: the letter is a **per-session/per-worktree label**, picked once (randomly) the
first time a session writes a phase doc and reused for everything that session goes on to write regardless
of subject — not a property of the work, a property of *who's currently typing*. The number keeps behaving
exactly as it always has: check `todo/`/`done/` for the highest one in use, take the next, no reset, no
per-letter counting. The letter only earns its keep the rare time two sessions' independently-computed
"next number" checks land on the same integer — precisely what already happened twice in this repo's real
history (`phase-150`, `phase-154`) — at which point the differing session letters keep the two resulting
*filenames* from colliding even though the numbers did. In the normal, non-colliding case a phase is still
just "phase 157," exactly as before the letter existed at all.

Both this section and the README's "Phase IDs" section were rewritten in place once the correction landed,
rather than left as a first draft with a follow-up phase to fix it — the mechanism had not yet been used by
anyone else when the correction arrived, so there was nothing downstream depending on the wrong shape yet.

### What's still genuinely open

- Branch protection for `dev`/`test`/`main` — a real decision, not resolved here, named plainly in
  `architecture/branching-and-releases.md`.
- Whether a `test` → `main` PR should open itself automatically.
- Whether 26 letters (single, uppercase) is actually enough headroom over this repo's real lifetime, or
  whether a second letter should be added once the first 26 are exhausted — not a problem yet, and not
  worth solving before it's real.
