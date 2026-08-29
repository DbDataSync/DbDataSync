# Distribution — how somebody who is not us gets this running

**Status**: Resolved. The design is `architecture/implementation/todo/phase-051-distribution-and-hosting.md`.

## The ask

Three distribution targets, in one piece of work:

1. A **.NET global tool** — `dotnet tool install -g` and then a command that starts DataSync.
2. A **Docker image**.
3. The global tool should be able to **register itself as a Windows service**, so a Windows host runs
   it the way a Windows host runs things.

## What was found when we looked

The gap is bigger than packaging, and worth stating before anyone starts:

- **The API does not serve the SPA.** There is no `UseStaticFiles` and no `MapFallback` anywhere in
  `src/`. In development the Vite dev server serves the app on 5173 and proxies `/api` and `/hubs` to
  the API on 5183 (`vite.config.ts`). Every distribution target here is one artifact that has to serve
  both, so this is a prerequisite, not a detail.
- **`ApiOptions.TaskRunnerDllPath` is dev-layout-only.** Its default takes the API's own
  `AppContext.BaseDirectory` and string-replaces `DataSync.Api/bin` with `DataSync.TaskRunner/bin`.
  That is true of this repo's build output and of nothing else. Published side by side — which is what
  every target here does — the runner sits in the same directory.
- **`ProcessSupervisor` spawns `dotnet exec <dll>`**, so the muxer has to be on `PATH`. It is, for a
  global tool and for the `aspnet` runtime images. It is not, for a self-contained publish — which
  rules that out unless the runner is spawned differently.
- **Three native dependencies**: LibGit2Sharp, SQLite, and (since the verification paging work) DuckDB.
  They decide the base image and they decide whether the tool can be RID-agnostic.
- **`app.UseHttpsRedirection()` with no certificate** redirects to a port nothing is listening on. Fine
  behind a dev proxy, wrong in a container.

None of these are hard. All of them are the difference between "packaged" and "runs".

## What was decided

One phase, because the three targets share the same prerequisite work and splitting them would mean
building the SPA-hosting and path-resolution changes for one target and then discovering them again
for the next. Windows service registration is a subcommand of the tool rather than a separate story:
it is the same binary, told to install itself.
