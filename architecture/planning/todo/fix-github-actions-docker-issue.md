# GitHub Actions: Docker issue

**Empty as of 2026-08-27 — nothing to plan against yet.** No error, no workflow name, no symptom.

Context that is likely relevant: most of this repository's integration tests need Docker containers
(`docker-compose.yml` runs two SQL Server instances and Postgres), and several recent phases record
that their integration and E2E coverage was **not written because Docker was unavailable in the
environment** — phases 25, 26 and 27 all say so explicitly. So a broken Docker step in CI is not a
minor annoyance; it is why a growing number of phases have unit tests where they wanted integration
tests.

**Next step**: paste the failing run's error, or the workflow file. Failing that, "which job, and what
does it say" is enough to start.
