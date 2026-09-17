# Phase 35 — Config history: diff and revert

**Status**: Implemented 2026-09-16. Unit- and API-tested locally; the Playwright spec runs in CI.
**Plan reference**: `architecture/planning/done/config-history-diff-and-revert.md`, split out of the
original `config-import-export-and-revert.md`. Import and export stay in
`planning/todo/config-import-export.md`, where the hard questions are.

## What was built

The config history has been real and complete since phase 6 — every change is an auto-commit with an
author and a message, and the Version Control tab has shown that log ever since. What was missing was
any way to act on it. Both actions here operate on commits that already exist, through a service that
already writes them; nothing new is stored and no new concept is introduced.

- **`GET /api/replications/{name}/history/{sha}/diff`** — the patch one commit made, scoped to that
  replication's directory. Read-only.
- **`GET /api/replications/{name}/history/{sha}/restore-preview`** — what restoring to it *would*
  change. See below; this is not the same question.
- **`POST /api/replications/{name}/history/{sha}/restore`** — puts the config back and records that as
  a new commit. Admin by omission, like every other write on that controller.
- **The Version Control tab** gains **View changes** (an inline Monaco diff editor under the commit
  row) and **Restore to here** (a confirmation naming what appears, disappears and differs, with the
  same diff editor under it). `yaml` joins `csharp` and `sql` as a registered Monaco language — one
  more lazy chunk, 3.5 kB.

## Three things worth knowing, two of which the plan did not spell out

### The restore preview is a different question from the commit's own patch

The plan said the confirmation "is built from the same diff the first action shows, so there is one
source of truth". That is right about the machinery and wrong about the anchor, and the difference is
the whole reason the confirmation exists:

> A commit's own patch answers *what did this change*. A restore's confirmation has to answer *what
> will this change*. Once anything has happened since, those are different sets.

Restoring to a commit that only renamed a column may well delete three mappings created after it —
none of which appear in that commit's own patch. So there are two endpoints over one diff engine,
anchored differently: the commit against its parent, the preview against HEAD. "One source of truth"
is honoured as one implementation, not one answer.

### Both sides of each file, not a unified patch

The plan named Monaco's diff editor, which computes its own diff from two documents — so the API
returns `before` and `after` per changed file rather than patch text. That is also the better answer
on its own terms: a patch shows changed hunks and elides everything else, and for a config file the
elided part is most of the context somebody opened it to read.

This settles the plan's **diff-size** open question in a specific way: **the file list is always
complete and only the content is capped** (256 KB per diff, by default). Truncating the list would
answer "what changed" with a lie; truncating content answers it with less detail. A file dropped for
budget is still listed, and `Truncated` says so.

### A dangling connection warns rather than refuses

The plan's stated rule is that "a revert that produces config the tool would reject on save must not be
reachable through a different door", and the validation is exactly the save's — extracted from
`SaveTableMapping` into a `ValidateTableMapping` both now call, so the two doors cannot drift.

Its example, though — a restore that "postdates a connection rename, so the restored config references
a connection that no longer exists" — is **not** something a save rejects. Saving a mapping that names
a connection which does not exist is allowed today; the connection may be about to be created.
Refusing it here would make the restore stricter than the save it restores, which is the opposite of
the rule. So it comes back as a **warning** the confirmation shows, which is the doc's own answer for
the running-replication case: say what will happen, and let the operator decide.

## How this was verified

- **`ConfigHistoryDiffAndRestoreTests`, 14 tests, green locally** against a real git repository: both
  sides of a modified file; a first commit reading as every file added; a diff scoped so another
  replication's changes in the same commit do not appear; an unresolvable sha refused by type; the
  preview differing from the commit's own patch; the older content coming back as a **new** commit with
  the older one still in the log; a mapping created after the restore point removed; **a restore being
  itself restorable**; a restore to before the replication existed refused; a restore that would
  produce save-rejected config refused *with nothing written*; the dangling-connection warning present
  when it should be and absent when it should not; and a restore to the current state recording
  nothing.
- **`ConfigHistoryEndpointTests`, 5 tests, green locally** — what only crossing the wire can show: the
  routes as the client builds them, the change kind arriving as a JSON *string* rather than an integer,
  an unresolvable sha as a 404 rather than a 500, and a refused restore as a 400 carrying the reason.
- **`config-history-diff-and-restore.spec.ts`** — four Playwright tests, stubbed at the network
  boundary with a fake server that actually appends to its own log on restore, so the assertion that
  **the log grows rather than shrinks** is about the client reflecting a real change. Runs in CI; not
  run locally, where Playwright is not installed.
- **The SPA builds and lints clean**, and `yaml` lands as its own 3.5 kB lazy chunk beside `csharp` and
  `sql` — the editor is still only paid for by a screen that shows one.
- **The full non-integration suite is green.**

## Open questions, resolved

**Diff size** — settled above: complete list, capped content.

**Scope of a restore** — unchanged from the plan, and now with a mechanism behind it. This restores one
replication's directory; a connection it references lives outside that directory and is not restored,
which is exactly why the validation and the dangling-connection warning exist. Whether "restore
everything at this commit" should exist is a separate and much larger question, untouched here.

**`dbdatasync.config.yaml` has no history view** — the *query* half is fixed and tested, and it was a
one-line bug exactly as phase 81 predicted: `GetHistory`'s prefix match only ever matched paths *below*
a prefix, so a bare repo-root file normalized to `dbdatasync.config.yaml/`, which that file's own path
never starts with. It now matches the bare path too, and `ARepoRootFile_HasHistoryAndADiffLikeAnything`
pins it. The *screen* half is not built, and the reason is not effort: see
`architecture/planning/todo/follow-up-phase-035-root-config-has-history-but-no-screen.md`.

## What this phase does not build

Import and export — the genuinely hard questions (name collisions, connections referenced by name that
do not exist in the destination, and secrets, which are deliberately not in the config store and cannot
travel with it) stay in `planning/todo/config-import-export.md`.

Diff between two arbitrary commits, or against another environment. "Diff vs production" needs more
than one environment to diff against (`planning/todo/environments.md`, blocked on a product decision),
which is why the tab still says nothing about it.

Revert of a connection or a script. Both are git-tracked and both could work the same way; the
replication is where the mockup put it and where the risk of a broken restore is highest, so it is the
one the pattern was built on.
