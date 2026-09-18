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

Full design in `architecture/implementation/README.md`'s new "Phase IDs" section, built the same pass:

- `phase-<Letter><N>-<kebab-slug>.md` — one uppercase letter (a lineage: a continuous thread of related
  phase work, however long it runs), an incrementing number *within* that letter, then a slug.
- A new, unrelated burst of work picks an unused letter — checked against `todo/`/`done/` first, the
  same cheap check the old scheme always should have made before claiming its next integer and usually
  didn't, since the old scheme's whole surface was one number everyone raced for.
- Two phases sharing a number under different letters imply nothing about relative order. Ordering
  across lineages, when it matters, still lives only in the "Build order" table beneath this section —
  unchanged in shape, since it already existed specifically to decouple filename from priority.
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
everyone pushes to; an integer everyone increments) with a namespace partitioned by *lineage* (a staged
branch a promotion step gates; a letter a continuous thread of work owns), keeping the good property the
old scheme had (a short, easy-to-say local sequence — `dev`'s own commit history; a letter's own
incrementing number) while removing the property that caused the actual collisions (global, ungated
sharing with no check between concurrent claims).

### A genuine design choice, not just the user's literal suggestion typed back

The letter-prefix idea came from the user directly ("a prefix random letter followed by the incrementing
number") in response to three numbering options this phase's own planning conversation offered first —
none of which the user picked. Worth being honest about what was and wasn't independently derived: the
*mechanism* (random letter, checked against existing prefixes before use, persisting for one lineage's
own subsequent phases) is this phase's own design work, reasoned through from the user's shorter
one-sentence framing — in particular, why a single random letter is collision-safe enough in practice
(lineages are started far less often than phase numbers used to be claimed, so a cheap check-before-claim
step at lineage-start time is enough, unlike the old scheme's high-velocity shared counter where the same
check was too slow to matter) rather than assumed safe by the letter count alone.

### What's still genuinely open

- Branch protection for `dev`/`test`/`main` — a real decision, not resolved here, named plainly in
  `architecture/branching-and-releases.md`.
- Whether a `test` → `main` PR should open itself automatically.
- Whether 26 letters (single, uppercase) is actually enough headroom over this repo's real lifetime, or
  whether a second letter should be added once the first 26 are exhausted — not a problem yet, and not
  worth solving before it's real.
