# Phase 65 — CI: `dotnet-integration` has no Postgres service container

**Status**: Done.
**Plan reference**: none — small enough to skip a separate planning doc, same precedent as phase 49.

## The failure

Checked the latest CI runs (`gh run list` / `gh run view`) — `dotnet-integration` has been failing
consistently, not intermittently (every run since at least "Phase 60 (1/n)", 2026-08-31T04:30). Every
single `DataSync.Drivers.Postgres.Tests` test fails, plus `DataSync.Api.Tests
.ConnectionStringAddressingTests.Postgres_TakesAConnectionStringToo`, all with the identical error:

```
Npgsql.NpgsqlException : Failed to connect to 127.0.0.1:15432
```

## Root cause

`.github/workflows/ci.yml`'s `dotnet-integration` job declares two service containers —
`mssql-source` and `mssql-target` — and **no Postgres container at all**. The Postgres-dependent tests
assume a Postgres instance reachable at `127.0.0.1:15432` (matching `docker-compose.yml`'s local dev
topology, per the job's own comment: "Same two-container topology as docker-compose.yml... so the tests'
localhost-based defaults work unchanged in CI" — a comment that was true for MSSQL and never updated when
Postgres support landed). Nothing is listening on that port in CI, so every Postgres test fails at
connection time, not from an actual regression.

## The fix

Add a third service container to `dotnet-integration`, mirroring `docker-compose.yml`'s existing
`postgres` service exactly:

```yaml
      postgres:
        image: postgres:17-alpine
        env:
          POSTGRES_PASSWORD: DataSync_Test_Pw1
          POSTGRES_USER: datasync
          POSTGRES_DB: datasync
        ports:
          - 15432:5432
        options: >-
          --health-cmd "pg_isready -U datasync"
          --health-interval 5s
          --health-timeout 5s
          --health-retries 10
```

Placed alongside `mssql-source`/`mssql-target` under `dotnet-integration`'s `services:` key. Port,
credentials, user, and database name all match `docker-compose.yml` and the tests' existing localhost
defaults exactly — no test code changes needed, this is purely a missing CI service definition.

## What this phase does not build

- Any change to the Postgres driver, its tests, or `docker-compose.yml` — those are already correct; CI
  just never stood up what they assume exists.
- Any change to the MSSQL service containers or the `dotnet`/`web`/`package` jobs.

## How to verify when built

- The next CI run on `main` shows `dotnet-integration` initializing three service containers, not two.
- Every currently-failing Postgres test (`DataSync.Drivers.Postgres.Tests.*`,
  `ConnectionStringAddressingTests.Postgres_TakesAConnectionStringToo`) passes.
- MSSQL-dependent integration tests are unaffected (regression check — the fix is additive).

## Open questions

None — this is a confirmed, minimal fix.

---

# Outcome — resolved 2026-08-31

Built exactly as planned: the `postgres` service container added verbatim to `dotnet-integration`,
alongside `mssql-source`/`mssql-target`. Timely — phase 63 (state store on MSSQL/Postgres, landed the
same day) added its own cross-engine Postgres tests, which would otherwise have been red in CI from the
moment they merged, for the same pre-existing reason as every other Postgres test. This fix covers both.
