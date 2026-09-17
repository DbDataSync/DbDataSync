# Phase 34 — PostgreSQL logical replication

**Status**: Implemented 2026-09-16 — the reader, its configuration-time refusals, and the environment.
The slot's **operational lifecycle** is split out: see "What became its own phase" below. Unit-tested
locally; the integration suite runs in CI, which is also where the new container image is first built.
**One product question is still open and is flagged for a decision** — see the last section.
**Plan reference**: `architecture/planning/done/change-tracking-postgres.md`. Built on phase 32's
`PositionExpiredException` and phase 33's `IPositionAcknowledging`, as planned; neither needed changing.

## What was built

**`PgLogicalSlotReader`** (Kind `PgLogicalSlot`) — `pg_logical_slot_peek_changes` through the
`wal2json` output plugin, with the slot's LSN as the watermark. It implements `IChangeReader`,
`IPositionAcknowledging`, `IReadIntentDeclaring` and `IPositionCapturing`, and it is the first
Postgres-specific *reader* this driver has ever registered. The plan's claim held: logical decoding is a
plain SQL function returning rows, so it needed no change to `IChangeReader` and no streaming subsystem.

**`PgLogicalSlotStatement`** — every statement as text, separated from both the reader and the
provisioner. More is pinned here than usual, and the first unit test in the file is why: the difference
between the safe function and the one that silently loses data is four characters (`peek` vs `get`).

**Provisioning** — `EnableSourceChangeCapture` for this Kind checks `wal_level` and the
`output_plugin_libraries` allowlist, refuses a table that cannot report its own deletes, warns about
`REPLICA IDENTITY FULL`, and proposes the slot as a previewed step whose rationale says plainly that
nothing drops it afterwards.

**The environment** — `docker/postgres-logical/Dockerfile`, wired into `docker-compose.yml` with
`wal_level=logical`, `output_plugin_libraries` and a raised `max_replication_slots`, and CI's Postgres
moved off `services:`.

## The plan's slot model was unsafe, and this is the most important thing here

The plan said:

> **One slot per replication**, not per mapping: a slot decodes the whole database and filters by
> table, so per-mapping multiplies slots for no gain.

**There is a gain, and it is correctness.** Advancing a slot is `IPositionAcknowledging`, which is
per-mapping by its own signature — it is handed a `SourceTableRef` and a watermark, and knows nothing
about any other mapping. So with one slot shared by mappings A and B:

1. A reads to LSN 100, writes, stores 100, advances the slot to 100.
2. B has only ever read to LSN 50.
3. B's next peek starts from the slot's confirmed position — 100 — and the changes between 50 and 100
   are gone.

That is silent data loss of exactly the kind the phase's own "peek, never get" argument exists to
prevent, arriving through a different door. Resolved two ways, both of which are in the code:

- **The default slot is per source table**, derived from schema and table rather than from the
  replication. Keyed on the *table* specifically because `AcknowledgeAsync` is handed no mapping name —
  a mapping-derived default would resolve to one slot when reading and another when acknowledging,
  which shows up as a slot that silently never advances.
- **Advancing is opt-in and off by default** (`advanceSlot`), which is the same call
  `TriggerAuditReader`'s own pruning option already makes in this codebase, for the same reason: it
  discards WAL on somebody's production database. Here it has a second reason — an operator turning it
  on is stating that the slot belongs to this mapping alone. Left off, the slot's WAL grows until
  somebody acts, which is a disk-space problem an operator can see coming rather than a correctness
  problem they cannot.

**The cost of this is real and is not hidden**: `max_replication_slots` defaults to **10**. A
replication with more tables than that needs the setting raised, and it needs a restart, on top of
`wal_level` which needs one too. The compose file raises it to 20 so the dev loop does not hit it.

The better answer is a slot per replication advanced to the *minimum* position across every mapping
using it — one decode instead of N. That needs cross-mapping coordination no reader has access to, so it
is future work rather than something to half-build here.

## Three other things the plan did not anticipate

**`wal2json` cannot apply a column transform.** Every other reader here builds a `SELECT` and puts each
mapping's transform expression in its projection. This one has no `SELECT` — rows come out of the WAL as
they were written. Silently ignoring a transform would write untransformed values into a target whose
shape assumes they were transformed, so a mapping with one is refused by name at the start of the pass.

**Decoded values have to be converted, not passed through.** JSON has three scalar types where Postgres
has a hundred, so a `timestamp` arrives as a string. Handing that on verbatim would work through the
generic staging provider (the server coerces a parameter) and *fail* through phase 38's binary `COPY`
(which coerces nothing) — so the same mapping would work or not depending on a staging Kind that is
supposed to be interchangeable. Values are converted to the CLR type the column's Postgres type implies,
reusing the table `PostgresValueBinding` already had for segment bounds, now shared as `PostgresValues`.

**No public image carries `wal2json` any more.** The plan expected "an image that carries it or a small
build step". Debezium's Postgres images are the usual answer and are no longer one: as of 3.x their
Dockerfile builds `decoderbufs` and nothing else — checked against their repository rather than assumed.
So it is a build step, which in turn means CI cannot start Postgres as a `services:` container (those
can only pull). It now comes up through `docker compose up -d --wait postgres` in a step, the same
image, port and credentials, which changes nothing about the tests. The Dockerfile itself is two lines:
the official Debian-based `postgres:17` already has the PGDG apt repository configured and PGDG
publishes `postgresql-17-wal2json`, so there is no compiler, no headers and no source checkout.

### A prerequisite that did not exist when the plan was written

The first CI run failed every integration test here with `library "wal2json" may not be used as an
output plugin`, on a server where the plugin was installed correctly. PostgreSQL 18.6, 17.11, 16.15,
15.19 and 14.24 added **`output_plugin_libraries`** as the fix for CVE-2026-6471: an output plugin
library now has to be on an allowlist before a slot may use it, and the default holds only the two that
ship with Postgres.

So **installing the plugin is no longer the same as permitting it**, and that is a third prerequisite
alongside `wal_level` and the package — one that produces a message leading nowhere obvious. The
provisioner checks it before proposing a slot, with `current_setting('output_plugin_libraries', true)`
so an older minor version reads as "restricts nothing" rather than "allows nothing", and the refusal
quotes the setting to add and says that this one needs only a reload rather than a restart.

Worth noting how it was found: the plan's own verification list asked for integration tests against a
real `wal_level=logical` container, and this is exactly what they were for. Nothing about the reader
was wrong.

## What became its own phase

Neither of these is a boundary that was considered and declined — both are work that should genuinely
get done, and the phase doc itself is emphatic about why. Split per this folder's own rule rather than
left as prose here.

- **`phase-151-deprovisioning-source-side-state.md`** — the plan asked for the slot to be "dropped when
  the replication or mapping is deleted". **There is no deprovisioning concept anywhere in this
  codebase**: deleting a mapping or a replication removes config and nothing else, and phase 33's
  trigger-audit shadow tables and triggers are left on the source today in exactly the same way. So this
  is a new cross-cutting mechanism that owes the same duty to two readers, not a Postgres detail. Until
  it exists, the slot's create step says in its own preview text that nothing drops it automatically,
  and the drop statement is quoted there so an operator can run it.
- **`phase-152-replication-slot-lag-and-orphans.md`** — surfacing retained WAL on the connection card,
  and reporting slots named `dbdatasync_%` that no config claims. The *statements* are built and
  covered here (`SlotState` returns retained bytes; `OurSlots` lists them); what is missing is an API
  surface and a place on the screen, which is where the work actually is.

## How this was verified

- **`PgLogicalSlotStatementTests`, 16 tests, green locally, no server** — peek-not-get, the read asking
  for one JSON object per change scoped to one table, the LSN comparison happening as `pg_lsn` rather
  than as text (an LSN is unpadded hex, so `0/9` sorts *after* `0/10` ordinally), slot-name validation
  refusing what Postgres would reject rather than escaping it, the table-derived default, the transform
  refusal, and the declared intents and defaults.
- **`PgLogicalSlotTests`, `Category=Integration`** — insert/update/delete with the delete carrying only
  its key; decoded values coming back as the CLR types their columns are; another table's changes
  filtered out; **a read that is not acknowledged seeing the same changes again** (the test the phase
  exists to be trusted by); acknowledging advancing the slot and the default *not* advancing it; a
  dropped slot and a recreated one both raising `PositionExpiredException`; `ChangesFromLatest` reading
  nothing and skipping nothing until acknowledged; a keyless table refused at configuration time with a
  message that also says what not to do reflexively; the slot planned once and satisfied afterwards; an
  operator-named slot resolving identically in the plan and the reader; a slot on another output plugin
  refused by name; and the container itself asserted to decode logically and carry the plugin.
- **`ChangeReaderFirstPassContractTests` updated**, not worked around: the new reader is in `Declaring`
  with the integration test that proves its set, and has a construction recipe. It failed twice on the
  way in, which is the file doing its job.
- **The full non-integration suite is green.**

### What is not verified

**Nothing here has run against a real Postgres on the implementing machine** — no Docker — so the
integration suite, *and the new container image itself*, are first exercised by CI. The image is the
part worth naming: if `postgresql-17-wal2json` is not resolvable on the runner, the build step fails
loudly rather than silently, which is why it is a step rather than a `services:` entry. The package was
confirmed present in PGDG's `bookworm-pgdg` index before being written down.

**`max_replication_slots` at its default of 10 is untested**, because the dev container raises it. What
a replication with more tables than slots actually does — the create step failing with the server's own
wording — has not been seen.

## Still open, and it needs a decision rather than more work

The plan's own first open question, unchanged and now the only thing blocking this phase from being
finished rather than shipped:

> **Is `wal2json` an acceptable prerequisite**, or must `pgoutput` come first? A product question about
> who the first user is, not a technical one.

Everything above is built on `wal2json`, which is what the plan directed ("Start with `wal2json`… `pgoutput`
is the fallback… Not first"). What phase 34 adds to the question is that the prerequisite is **heavier
than it looked**: not just "an extension", but an extension with no ready-made image behind it any more,
which this repo now carries a Dockerfile for. RDS, Aurora and Cloud SQL all offer `wal2json`, so the
managed-Postgres case is unaffected; the ones who pay are self-hosted deployments that have to install
a package on the source server, on top of the `wal_level` restart they already needed.

`pgoutput` needs no extension at all and is the fallback the plan named. It is strictly more work — it
is the binary replication protocol, so it streams, and the bounded window has to be enforced by stopping
at a target LSN rather than by the query doing it, which means it is not a `SELECT` and not shaped like
anything else in this codebase.

**Nothing here has to be undone either way.** If `pgoutput` is wanted, it is a second reader beside this
one, and the slot lifecycle, the provisioning checks, the value conversion and the environment are all
shared. The question is only whether it is wanted *before* anyone uses this.

The second open question — **DDL**, since Postgres does not decode it, so a column added to a tracked
table changes what `wal2json` emits with no warning — is partly answered by construction: a column the
mapping does not carry is skipped, and a value that will not convert to the mapping's idea of the
column's type fails with a message naming the column and saying to refresh the metadata. A column
*removed* at the source still silently arrives as null. Not closed.

## Out of scope, as planned

`pgoutput`. The trigger fallback (phase 33 covers it generically). Any change to the Postgres driver's
batch or watermark paths. A bounded read: `pg_logical_slot_peek_changes` has an `upto_nchanges`
parameter that would fit `BoundedRead`, and nothing here needs it yet.
