# GitHub Actions: Docker issue

**Diagnosed 2026-08-28**, from the `dotnet-integration` job's logs (e.g. run 33138877556, and every CI
run since at least 2026-08-28T03:19Z — this has been failing consistently, not intermittently).

## The failure

`.github/workflows/ci.yml` starts two `mcr.microsoft.com/mssql/server:2022-latest` service containers
(`mssql-source`, `mssql-target`) with:

```
--health-cmd "/opt/mssql-tools/bin/sqlcmd -S localhost -U sa -P DataSync_Test_Pw1 -Q 'SELECT 1'"
```

SQL Server itself starts fine inside the container — the log shows startup completing normally
("Recovery is complete. This is an informational message only.") within about 15 seconds. But
`docker inspect`'s health status never goes past `starting`, and after `--health-retries 10` at
`--health-interval 10s` it flips to `unhealthy`, so the "Initialize containers" step fails and the whole
`dotnet-integration` job is skipped (Restore/Build/Test never run).

**Root cause**: `/opt/mssql-tools/bin/sqlcmd` doesn't exist in the current `mcr.microsoft.com/mssql/server:2022-latest`
image anymore. Microsoft's SQL Server Linux images moved the CLI tools to `mssql-tools18`, at
`/opt/mssql-tools18/bin/sqlcmd` — and `sqlcmd` in that package defaults to encrypted connections, so it
also needs `-C` (trust server certificate) against the container's self-signed cert, or the health check
will fail even at the new path. The health-check command in `ci.yml` is silently failing every retry
(command not found) even though the database it's checking is healthy.

## The fix

In `.github/workflows/ci.yml`, update both `--health-cmd` entries (lines 38 and 51) from:

```
/opt/mssql-tools/bin/sqlcmd -S localhost -U sa -P DataSync_Test_Pw1 -Q 'SELECT 1'
```

to:

```
/opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P DataSync_Test_Pw1 -Q 'SELECT 1'
```

This should also explain the note on phases 25, 26 and 27 that integration/E2E coverage wasn't written
because "Docker was unavailable in the environment" — if that unavailability was this same health-check
failure (rather than Docker literally missing), those phases may be unblocked once this is fixed, and
worth revisiting.

---

# Outcome — fixed 2026-08-28

Applied, with the diagnosis verified rather than taken on trust, and one finding beyond it.

**The diagnosis is right, and both halves matter.** Verified against the image as it is today:

```
$ docker run --rm --entrypoint sh mcr.microsoft.com/mssql/server:2022-latest -c 'ls -d /opt/mssql-tools*'
/opt/mssql-tools18

$ sqlcmd -S localhost -U sa -P … -Q 'SELECT 1'          # without -C
Sqlcmd: Error: … SSL Provider: [certificate verify failed:self-signed certificate]
```

So the old path is gone *and* the new sqlcmd fails on the container's self-signed certificate without
`-C`. Fixing only the path would have moved the failure rather than removed it.

**The finding the diagnosis missed: CI was not the only place.** `docker-compose.yml`'s two health
checks and `tests/DataSync.Web.Tests/test-db.ts`'s two `docker exec` calls hardcode the same old path.
They still work on this machine only because its image is from **2024-05-02** — a `docker compose pull`
would have broken local development and the Playwright suite in exactly the same way, with the same
confusing symptom (a database that is up, reporting unhealthy).

Phase 11's own retrospective flagged this shape of risk when it wrote that helper: *"hardcodes a path
inside the image"*.

**All four sites now try the new path and fall back**, because `:2022-latest` moves under you and a
machine can be holding either generation. The Playwright helper resolves it once by asking the
container rather than guessing. Verified both ways: the E2E suite is green on this machine's 2024 image
with the new path attempted first.

**Whether this unblocks phases 25–27** is worth checking but should not be assumed. Their notes say
Docker was unavailable *in the authoring environment*, which reads like the agent had no Docker at all
rather than a failing health check. If CI goes green, their integration and E2E gaps are the obvious
follow-up.
