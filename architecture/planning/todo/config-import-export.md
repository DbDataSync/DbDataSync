# Config import and export

**Status: proposal, not agreed — and the questions below are the reason.** Split out of
`config-import-export-and-revert.md`; the diff-and-revert half was resolved and became phase 35
(`planning/done/config-history-diff-and-revert.md`). This is the half where the difficulty is, and
none of it is technical.

From the phase 15 mockups, which show an **Import config** action on the replications list.

## The rough shape

- **Export** — the config for a replication (or the whole store) as a portable bundle. The easy
  direction, and nearly free: the config *is* files in a git repository.
- **Import** — the inverse, and where every hard question lives.

## Why no implementation phase was written

Because each of these changes what gets built, and none can be inferred:

- **Does import mean "merge into this store" or "replace"?** The mockup's single button implies the
  former, which is the harder one — and the two need different UIs, not just different code.
- **What happens to a connection an imported replication references but which the destination does not
  have?** Reject the import, or create a stub the operator must complete? A stub is friendlier and
  means the store can hold config that cannot run, which nothing in the model allows today.
- **Secrets never leave the machine.** They are deliberately not in the config store, so an imported
  replication is always inert until its connections are given credentials. That has to be *visible*,
  not discovered at the first run — which is a UI design question as much as an engineering one.

## What has changed since this was written

Phase 31 made connections addressable by connection string, which means an imported connection can now
carry more of its own configuration — but also that a connection string is more likely to contain
environment-specific detail that an import should not blindly take. That sharpens the second question
rather than answering it.

Phase 35's restore validates config before committing it, and an import wants exactly the same check.
Whatever it produces should go through the same door.

**Next step**: answer the three questions above. They are product decisions.
