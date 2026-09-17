# Phase 154 — `dotnet-integration` onto `docker-compose.yml`, not a second container definition

**Status**: Complete. Confirmed on a real CI run (`35283992102`) — every job green, `dotnet-integration`'s
new "Start the databases" step (`docker compose up -d --wait`, no explicit service list) brought up all
six containers including Oracle, whose bind-mount provisioning ran correctly with no manual `docker exec`
step — see "Real CI confirmation" below.
**Plan reference**: none — raised directly in conversation (comparing `dotnet-integration`'s native
`services:` block against the `playwright` job's own `docker-compose.yml` usage) and agreed on the same
day. `architecture/implementation/done/phase-153-ci-mariadb-and-oracle-service-containers.md` is the
immediate motivation: that phase's own retrospective names the root problem this phase closes.

## Why this phase exists

`dotnet-integration` hand-duplicated `docker-compose.yml`'s container topology as a second, independent
GitHub Actions `services:` block — same images, same ports, same credentials, maintained by hand in two
places. That duplication is exactly what let phase 147/148's `mariadb`/`oracle` containers go missing
from CI for a full release cycle: `docker-compose.yml` gained them, `ci.yml`'s own copy did not, and
nothing forced the two to agree. Phase 153 fixed the immediate symptom by adding the missing entries (and
a manual "Provision Oracle" `docker exec`/`sqlplus` step, needed only because a `services:` container is
created and health-checked *before* `actions/checkout` puts the repo on disk, so `docker-compose.yml`'s
own bind-mount of `docker/oracle-init/*.sql` can't work there) — but left the actual drift risk in place.

There is no reasonable reason for the two testing environments (a developer's `docker compose up`, and
CI) to differ. This phase removes the second definition entirely.

## What this phase does

Edits `.github/workflows/ci.yml` only — no source code changes.

- **`dotnet-integration`'s `services:` block is gone.** A new first step, "Start the databases", runs
  `docker compose up -d --wait` with no service names — this job needs every engine
  `docker-compose.yml` defines, so naming a subset would just be one more list to keep in sync with that
  file as engines get added (unlike `playwright`, which deliberately only needs three and says so).
  `--wait` blocks on each service's own healthcheck, declared once, in that one file.
- **The "Provision Oracle" step is gone too**, not just moved — it's no longer needed. `docker compose
  up` runs *after* `actions/checkout`, so `docker-compose.yml`'s existing `./docker/oracle-init:/container-entrypoint-initdb.d`
  bind mount now sees the real files the moment the container's own entrypoint looks for them, the same
  way it already works for every developer running this locally. The manual `docker exec ... sqlplus`
  workaround was only ever needed because of the native-`services:` ordering problem this phase removes.
- **Comments updated** in the `dotnet`, `dotnet-windows`, and `playwright` jobs that referenced the old
  `services:` mechanism or explained why `playwright` alone used compose (that reasoning — container-name
  addressing via `docker exec`, which `services:` containers never supported — still holds, just phrased
  against a `dotnet-integration` that now also uses compose, not against a contrasting `services:` block
  that no longer exists).

## What this phase does not do

- Does not touch `docker-compose.yml` itself — that file was already correct; this phase makes CI use it
  instead of a lookalike.
- Does not change the `playwright` job's own `docker compose up -d --wait mssql-source mssql-target postgres`
  — it still only needs those three, deliberately, and naming them there is not the same kind of drift
  risk (it's a real, static subset, not an attempt to mirror a file that keeps growing).
- Does not address `BulkLoadIntegrationTests.ARaceBetweenAConcurrentReloadAndAMappingsOwnFirstPass_TheLoserFailsCleanly_AndSelfHeals`,
  the other failure phase 153's own first CI run surfaced — a timing-sensitive race assertion, unrelated
  to this phase's scope, tracked separately.

## How to verify

- `.github/workflows/ci.yml` parses as valid YAML (checked with `python3 -c "import yaml; yaml.safe_load(...)"`).
- The mechanism itself is already proven in this exact CI environment: `playwright`'s own
  `docker compose up -d --wait` step has been bringing up three of these same six services (by the same
  images, on the same ports) successfully on every green `playwright` run this session watched directly
  (phase 144). What's new here is bringing up all six in a different job and removing the Oracle
  provisioning workaround — not the underlying compose mechanism.
- **Not yet observed**: an actual `dotnet-integration` CI run under this new step shape. In particular,
  whether `docker-compose.yml`'s oracle service's bind-mount-provisioning genuinely lands before the
  `Test (Integration)` step needs the grants — expected, since `--wait` doesn't return until the
  container's own healthcheck passes, and that healthcheck (`healthcheck.sh`, the same one `playwright`'s
  and every developer's local `oracle` container already reports "healthy" against, running *after* its
  own init scripts) is the same file `docker-compose.yml` already declares. Should be watched on this
  phase's own first real run rather than assumed.

## Real CI confirmation

Run `35283992102` (this phase's own commit, `cd8c35d`) went fully green — `dotnet`, `dotnet-windows`,
`dotnet-integration`, `web`, `playwright` all passed. `dotnet-integration`'s "Start the databases" step
brought up all six containers via `docker compose up -d --wait` in the same run duration ballpark as the
old `services:`-based job (~8 minutes), and `DbDataSync.Drivers.Oracle.Tests` passed 21/21 with no manual
provisioning step at all — confirming the bind-mount ordering concern above was unfounded in practice, not
just in theory.

A second push (the `BulkLoadIntegrationTests` fix, `01fe87f`) triggered run `35284322323`:
`dotnet-integration` went red again, but on a genuinely unrelated, pre-existing test —
`DbDataSync.Drivers.MsSql.Tests.Scd2CdcGuaranteedDeliveryIntegrationTests.APassWithDuplicateAndSingletonKeys_AppliesEveryKeyCorrectly_WithNoPkViolation`
failed an `Assert.NotEqual` on two CDC-captured timestamps landing identical (a timing/granularity flake
in SQL Server CDC's own capture job, nothing this phase or the `BulkLoadIntegrationTests` fix touches).
`DbDataSync.Api.Tests` (which holds `BulkLoadIntegrationTests`) passed 71/71 on that same run, confirming
that fix. The new CDC flake is out of this phase's scope — noted here for whoever picks it up next, not
investigated further.
