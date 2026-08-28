# Config history: diff and revert

**Status: resolved 2026-08-27 — see Outcome at the end.** Split out of
`config-import-export-and-revert.md`, which covered two problems of very different sizes. This is the
half that acts on history the tool already has; import and export are
`planning/todo/config-import-export.md`.

From the phase 15 mockups, which show a **Diff vs production** action on the config-history tab and a
**Revert** action on every commit in that log. Both were omitted from the implementation because
neither had an endpoint behind it.

## Why it is worth having

The config store is already a git repository, and every change is already an auto-commit with an author
and a message. So the history is real and complete — what is missing is any way to *act* on it. Revert
in particular is a small step from what exists: the commit is there, the diff is there, and
`GitCommitService` already writes commits.

- **Revert** — restore one replication's config to a given commit and record that as a new commit,
  rather than rewriting history. Needs care about what "revert" means when the commit touched several
  files, and about a revert that would leave a running replication pointing at a table mapping that no
  longer exists.
- **Diff vs production** implies more than one environment to diff against — see
  `planning/todo/environments.md`. Without that it degrades to "diff vs a chosen commit", which is
  still useful and much smaller.

---

# Outcome — resolved 2026-08-27

Agreed, as `implementation/todo/phase-035-config-history-diff-and-revert.md`.

Three things the phase settles that this doc raised:

- **"Revert" means restore, not `git revert`.** An inverse patch conflicts if anything touched the same
  lines since; restoring the tree at a commit does not, and it is what an operator means. The commit
  message says which it is.
- **The broken-restore worry is answered by validating before committing** — with the validation that
  already exists (`EndpointResolution.Validate`, `ValidateHooks`, script-binding resolution). A revert
  that produces config the tool would reject on save must not be reachable through a different door.
- **Diff-vs-production degrades to diff-vs-commit**, exactly as this doc suggested, and that is most of
  the value. The environment version stays blocked on `planning/todo/environments.md`.

The diff view uses Monaco's diff editor, which phase 28 already brings in and whose own retrospective
named this as the natural follow-on — so the rendering cost of this phase is one more lazy language
chunk for YAML.
