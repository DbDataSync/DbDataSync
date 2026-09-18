# `dbdatasync.config.yaml` has a history and a diff now, and nowhere to see them

**Status: open.** Extracted from
`architecture/implementation/done/phase-035-config-history-diff-and-revert.md`'s "Open questions,
resolved", per `architecture/implementation/README.md`'s "Follow-up work gets its own doc, not a
paragraph." Carried there from phase 81's own retrospective, which judged it out of scope for itself
specifically because phase 35 existed to take it.

## What phase 35 did, and what it left

Phase 81 found that the repo-root `dbdatasync.config.yaml` — the console's own port, TLS and auth
settings — is git-tracked and auto-committed like everything else, and invisible to every history query
in the product. It diagnosed the cause precisely: `GitCommitService.GetHistory` matched
`prefix + "/"`, which is right for a directory and matches nothing at all for a bare file, since
`dbdatasync.config.yaml` normalizes to `dbdatasync.config.yaml/` and the file's own path does not start
with that.

**Phase 35 fixed the query half.** The match now accepts the bare path as well as a prefix of it, and
`ConfigHistoryDiffAndRestoreTests.ARepoRootFile_HasHistoryAndADiffLikeAnythingElse` pins both the
history and the diff working for it. The capability exists and is proven.

**What is missing is a screen**, and two decisions that have to be made before building one.

## Decision 1 — where a repo-root file's history belongs

It is not any one replication's, so the Version Control tab is the wrong home. The obvious candidate is
the admin config screen phase 81 built, which is where those settings are edited — a history section
under the form, reusing phase 35's diff viewer.

That is probably right, and it is worth stating why it is a decision rather than an obvious extension:
the admin screen is a *form over settings*, where the Version Control tab is a *log*. Bolting a log
onto a form is the kind of thing that reads fine in a mockup and is cluttered in use. An alternative
worth considering is a single repository-wide history view — every config commit, filterable — of which
a replication's tab becomes one filtered view and the root file another.

## Decision 2 — whether restore applies to it at all

**Restoring this file has a risk the replication restore does not have: it can make the console
unreachable.** It holds the listening port, the TLS certificate reference and the authentication mode.
Restoring it to a commit from before TLS was configured, or to one naming a certificate since removed,
takes the screen that did the restoring offline — and the recovery is a command line on the host, not
an Undo button.

So the options are:

1. **Diff only.** History and View changes, no restore. Cheapest, and loses little: an operator reading
   the diff can make the edit themselves in the form, which is a smaller and more deliberate action.
   **This is the recommendation.**
2. **Restore with a restart-and-verify path**, of the kind phase 113's certificate swap needed. Real
   work, and only worth it if somebody actually wants it.

Option 1 also sidesteps the question of what "validate before committing" means for this file, which
has no `EndpointResolution` equivalent — its validation is `ApiOptions` binding and whatever
`config check` knows.

## How to verify when built

- The admin config screen (or wherever decision 1 lands) shows the commits that touched
  `dbdatasync.config.yaml`, and only those.
- A commit's diff renders in the same viewer the Version Control tab uses.
- If restore is built: a restore that would leave the console unreachable is refused, or is gated
  behind something stronger than a confirmation dialog.
