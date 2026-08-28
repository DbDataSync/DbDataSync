# Environments

**Status: blocked on a product decision, not on engineering.** The doc's own first open question —
*which reading is intended* — decides whether this is a UI convenience or a change to the shape of the
config store, and the two differ by an order of magnitude. Its own warning is why nothing was picked
for it: "the first is not a step towards the second — shipping a tag and later discovering the store
needs restructuring would mean throwing the tag away."

So no implementation phase was written. Answering the question below is the next step, and it is one
for whoever owns the product direction rather than something to infer from the mockups.

The phase 15 mockups show an environment pill (`prod`) in the breadcrumb of every screen, an
**All environments** filter in the Explorer sidebar, an `Environment` field on a connection, and a
**Diff vs production** action. No such concept exists, so all of it was omitted.

## What the design seems to mean by it

An environment looks like a *scope* that a connection belongs to and that the whole console is
filtered by — pick `prod` and you see prod's connections and the replications that use them. That is
a bigger idea than a label: it implies the same replication can exist in more than one environment,
which is what makes "diff vs production" meaningful.

## The two readings, which differ a lot in cost

1. **A tag on a connection.** Connections gain an `Environment` string; the UI filters by it. Cheap,
   and delivers the Explorer filter and the field on the connection editor. Does not deliver "diff vs
   production", because there is still only one config store.
2. **A dimension of the config store.** The same replication defined once and promoted between
   environments, with per-environment connection bindings. This is what makes diff-and-promote real,
   and it reaches into the config repository layout, the API's routes, the scheduler (does a
   disabled-in-staging replication still tick?) and the run model.

Reading 1 is a UI convenience. Reading 2 is a product direction. They should not be conflated, and
the first is not a step towards the second — shipping a tag and later discovering the store needs
restructuring would mean throwing the tag away.

## Open questions

- Which reading is intended.
- If it is (2): is an environment a branch of the config repo, a directory within it, or a separate
  store? The git-backed design makes branching tempting and probably wrong — a branch that never
  merges is a fork.
- How environments interact with the TaskRunner, which today takes one repo root and one state
  database.
