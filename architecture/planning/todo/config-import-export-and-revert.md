# Config import/export and revert

From the phase 15 mockups, which show an **Import config** action on the replications list, a
**Diff vs production** action on the config-history tab, and a **Revert** action on every commit in
that log. All three were omitted from the implementation because none has an endpoint behind it.

## Why it is worth having

The config store is already a git repository, and every change is already an auto-commit with an
author and a message. So the history is real and complete — what is missing is any way to *act* on
it. Revert in particular is a small step from what exists: the commit is there, the diff is there,
and `GitCommitService` already writes commits.

## The rough shape

- **Revert** — restore one replication's config to a given commit and record that as a new commit,
  rather than rewriting history. Needs care about what "revert" means when the commit touched several
  files, and about a revert that would leave a running replication pointing at a table mapping that no
  longer exists.
- **Export** — the config for a replication (or the whole store) as a portable bundle.
- **Import** — the inverse, which is where the difficulty is: name collisions, connections referenced
  by name that do not exist in the destination, and secrets, which are deliberately *not* in the
  config store and so cannot travel with it.
- **Diff vs production** implies more than one environment to diff against — see
  `planning/todo/environments.md`. Without that it degrades to "diff vs a chosen commit", which is
  still useful and much smaller.

## Open questions

- Does import mean "merge into this store" or "replace"? The mockup's single button implies the
  former, which is the harder one.
- What happens to a connection an imported replication references but which the destination does not
  have — reject the import, or create a stub the operator must complete?
- Secrets never leave the machine, so an imported replication is always inert until its connections
  are given credentials. That should be visible in the UI, not discovered at the first run.
