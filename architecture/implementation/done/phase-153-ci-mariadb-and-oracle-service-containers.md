# Phase 153 — CI: MariaDB and Oracle service containers for the integration suite

**Status**: Closed 2026-09-17 — **superseded by phase 154** before this phase's own mechanism was ever
confirmed by a real CI run. This phase's `services:` block and manual "Provision Oracle" step no longer
exist in `.github/workflows/ci.yml`; phase 154 replaced both with `docker compose up -d --wait` against
`docker-compose.yml` directly, closing the root cause (`ci.yml` hand-duplicating that file's topology)
this phase's own retrospective named but only worked around. See
`architecture/implementation/done/phase-154-ci-integration-suite-onto-docker-compose.md`, whose own real
run (`35283992102`) is the actual, real-world proof MariaDB and Oracle work in CI — the proof this
phase's own "Still not verified" section below says it never got to produce itself. Kept as a full record
rather than deleted: the finding in its Retrospective (GitHub Actions `services:` containers can't
bind-mount repo files, because they're created before `actions/checkout` runs) is exactly what phase 154
cites as its own motivation, and is still true of `services:` blocks in general even though nothing in
this repo depends on it working around that any more.
**Plan reference**: `architecture/implementation/done/phase-147-mysql-mariadb-driver-and-trigger-audit.md`,
`architecture/implementation/done/phase-148-oracle-driver-trigger-audit-and-flashback.md` (the two phases
whose new `Category=Integration` test projects this fixes CI for), `.github/workflows/ci.yml`'s own
`dotnet-integration` job (what this phase edits).

## Why this phase exists

Phases 147 and 148 added two new test projects — `DbDataSync.Drivers.MySql.Tests` and
`DbDataSync.Drivers.Oracle.Tests` — with `[Trait("Category", "Integration")]` test classes that open real
connections to a MariaDB container (`MariaDbTestDatabase`/`MariaDbPipelineTests`/`MariaDbTriggerAuditReaderTests`)
and a real Oracle container (`OracleTestDatabase`/`OraclePipelineTests`/`OracleFlashbackReaderTests`/the
Oracle `TriggerAuditReaderTests`). Both phases verified this locally against `docker-compose.yml`'s own
`mariadb`/`oracle` services, and both phases' own retrospectives report every test green.

**Neither container exists in CI.** `.github/workflows/ci.yml`'s `dotnet-integration` job — the only job
that runs `--filter "Category=Integration"` — stands up `mssql-source`, `mssql-target`, `postgres`, and a
single `mysql` (added in phase 109c, for an unrelated proof that predates phase 147's real compiled MySQL
driver). There is no `mariadb` service and no `oracle` service at all. Every `dotnet-integration` run
since phase 147 merged has been failing on every `MariaDb*` test with a connection refused, and every
`Oracle*` test with the same, the moment `dotnet test` reaches those two projects — a real, ongoing CI
gap this phase closes.

## What this phase does

Edits `.github/workflows/ci.yml`'s `dotnet-integration` job only. No source code changes — this is
infrastructure, not a driver fix.

- **Adds a `mariadb` service** (`mariadb:11`, port 13307 → 3306, `MARIADB_ROOT_PASSWORD`/`MARIADB_DATABASE`
  matching `docker-compose.yml`'s own service exactly) so `MariaDbTestDatabase`'s hardcoded default
  connection string — unchanged, no new environment variable needed — just works, the same way the
  existing `mssql-source`/`mssql-target`/`postgres`/`mysql` services already let their own test fixtures'
  defaults work unchanged in CI.
- **Adds an `oracle` service** (`gvenzl/oracle-free:23-slim`, port 15210 → 1521,
  `ORACLE_PASSWORD`/`APP_USER`/`APP_USER_PASSWORD` matching `docker-compose.yml`), for the same reason.
- **Adds a "Provision Oracle" step**, applying `docker/oracle-init/10-grants.sql` and
  `docker/oracle-init/20-flashback-probe-table.sql` via `docker exec <container> sqlplus -s / as sysdba`
  after `actions/checkout` and after the service's own healthcheck has passed (GitHub Actions blocks
  every job step until every declared service is healthy, so there is nothing to wait for beyond that).
  This step exists because `docker-compose.yml`'s own mechanism for the same two scripts — bind-mounting
  `./docker/oracle-init` to `/container-entrypoint-initdb.d` — **cannot work as a GitHub Actions
  `services:` entry**: service containers are created, and their healthchecks satisfied, before
  `actions/checkout` ever runs, so a bind mount of a repo-relative path at that point sees an empty
  directory. The Oracle entrypoint decides there is nothing to run and never looks again — the container
  reports healthy having silently skipped both scripts. Found by reasoning about the two mechanisms'
  actual ordering, then confirmed by testing the exact replacement invocation (`docker exec -i <container>
  sqlplus -s / as sysdba < docker/oracle-init/10-grants.sql`) against a live Oracle container before
  committing to it, rather than assumed to work by analogy with the entrypoint's own internal
  `sqlplus -s / as sysdba` call.

## What this phase does not do

- Does not change `docker-compose.yml` — that file's own bind-mount mechanism is correct and unaffected;
  this phase only works around GitHub Actions' inability to reproduce it, in CI specifically.
- Does not touch the `dotnet`/`dotnet-windows`/`playwright`/`package` jobs — none of them run
  `Category=Integration` tests, so none of them are affected by this gap or this fix.
- Does not add a `tools/dev-harness` scenario, `CrossEngineReplicationTests` extension, or any other item
  phase 147/148's own "Still not built" sections already named as out of scope — this phase is scoped
  strictly to making the tests that already exist actually run in CI.

## How to verify

- `.github/workflows/ci.yml` parses as valid YAML.
- The exact replacement invocation for the Oracle provisioning step
  (`docker exec -i <container> sqlplus -s / as sysdba < docker/oracle-init/10-grants.sql`) tested against
  a live `gvenzl/oracle-free:23-slim` container before merging — confirmed it lands in `CDB$ROOT` (matching
  what `ALTER SESSION SET CONTAINER = FREEPDB1;` at the top of both `.sql` files assumes) and that the
  grants apply cleanly.
- The next `dotnet-integration` CI run on `main` is the real proof: every `MariaDb*`/`Oracle*` test in
  `DbDataSync.Drivers.MySql.Tests`/`DbDataSync.Drivers.Oracle.Tests` should report passing rather than a
  connection-refused failure. Not observable from this environment (no access to GitHub Actions runners
  here) — the retrospective below states this plainly rather than claiming a CI run that didn't happen.

## Retrospective

Built and pushed 2026-09-17. Two services added (`mariadb`, `oracle`) and one new step ("Provision
Oracle") to `.github/workflows/ci.yml`'s `dotnet-integration` job, matching `docker-compose.yml`'s own
topology and ports exactly so every affected test fixture's hardcoded default connection string needed no
change and no new CI-only environment variable.

### The one real finding: GitHub Actions services can't bind-mount repo files the way `docker compose` can

Not obvious until reasoned through carefully: a GitHub Actions job's `services:` containers are created,
and the job blocks on their healthchecks, as part of "Set up job" — a phase that completes *before* the
first `steps:` entry (`actions/checkout`) runs. `docker-compose.yml`'s `./docker/oracle-init:/container-entrypoint-initdb.d`
bind mount relies on those files already being on disk at container-creation time, which is true when a
developer runs `docker compose up` from a checked-out working tree, and is never true for a GitHub Actions
service container — the repository doesn't exist on the runner's filesystem yet when Oracle's entrypoint
decides whether `/container-entrypoint-initdb.d` has anything in it. The image reports healthy having
silently done nothing, which is the worst version of this failure: not a build error, not a red CI run
naming the problem, just every `Oracle*` test failing downstream with what looks like an ordinary missing
grant.

Verified rather than assumed twice: first, that `docker exec -i <container> sqlplus -s / as sysdba`
(peer OS authentication, no connection descriptor) reaches `CDB$ROOT` exactly the way the entrypoint's own
internal call does — confirmed via `SYS_CONTEXT('USERENV','CON_NAME')` against a live container before
writing the CI step at all, since an earlier local investigation (phase 148's own manual testing) had only
ever exercised the *password-based* connection form (`sys/pw@host:port/service`), which lands directly in
the PDB rather than `CDB$ROOT` and would have made both scripts' own `ALTER SESSION SET CONTAINER =
FREEPDB1;` meaningless or an error. Second, that the real `10-grants.sql` file — not just an ad hoc
`SELECT` — applies cleanly through that exact invocation, piped from the actual checked-out file rather
than retyped inline.

### Never verified — superseded first

The "Still not verified" gap this section originally named (a real GitHub Actions run of this phase's own
`services:`-plus-`docker exec` mechanism) was never closed, in either direction: nobody watched this
phase's own approach fail *or* succeed on a real run. Phase 154 replaced it — `services:` block and
"Provision Oracle" step both — before this phase's own first push to `main` completed a full
`dotnet-integration` run, on the reasoning in phase 154's own doc (the duplication between `ci.yml` and
`docker-compose.yml` was the real problem; this phase's fix was still a second, hand-maintained copy of
the same topology, just a complete one). So the `docker ps --filter "publish=15210"` container-discovery
step in particular was never exercised in CI at all — worth stating plainly rather than leaving the
original wording, which reads as an open question this phase might still resolve. It won't; phase 154's
own real run (`35283992102`, `DbDataSync.Drivers.Oracle.Tests` 21/21) is the actual answer to the
question this section used to ask, for a different, better mechanism than the one that asked it.
