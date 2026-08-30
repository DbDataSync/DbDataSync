# Phase 51 — Distribution: a global tool, a Windows service, and a container

**Status**: Done
**Plan reference**: `architecture/planning/done/distribution-and-hosting.md`

## What this phase builds

One artifact that a person who did not write this can install and run, in three shapes: a .NET global
tool, that tool registered as a Windows service, and a Docker image. The three share almost all of
their work, which is why they are one phase.

## 0. The prerequisite: one process serves the whole product

Today the SPA is served by Vite on 5173 and proxies `/api` and `/hubs` to the API on 5183. Nothing in
`src/` serves the built app. Every target below is a single artifact that has to serve both, so this
comes first.

- **`src/DataSync.Api/DataSync.Api.csproj`** gains an MSBuild target that runs `npm ci && npm run
  build` in `src/DataSync.Web` on publish and copies `dist/` into `wwwroot/`. Gated behind a property
  (`SkipWebBuild`) so a developer's inner loop and the test suite do not pay for it — the Playwright
  suite runs the API and Vite separately and must keep doing so.
- **`Program.cs`** gains `app.UseStaticFiles()` and a `MapFallbackToFile("index.html")` **after**
  `MapControllers` and the hub. The fallback must not swallow `/api` or `/hubs`: an unmatched API route
  returning `index.html` with a 200 is the failure mode where the SPA silently parses HTML as JSON.
  Assert that with a test, not by reading the routing table.
- **`app.UseHttpsRedirection()` becomes conditional.** With no certificate configured it redirects to a
  port nothing is listening on, which is exactly the container case. Redirect only when an HTTPS URL is
  actually bound.

**Verification**: a published API, run with no Vite anywhere, serves the app at `/`, deep-links
(`/replications/x/mappings/y`) return the app rather than a 404, and `/api/does-not-exist` returns a
404 rather than HTML.

## 1. `DataSync.Cli` — the global tool

A new project, because the tool is a *command surface* and the API is a web host. Making the web host
itself the tool would mean `datasync --help` starting Kestrel to print text.

```
datasync serve      [--repo <path>] [--state-db <path>] [--url http://localhost:5000]
datasync service    install | uninstall | status      (Windows only)
datasync version
```

- `PackAsTool` / `ToolCommandName=datasync` on the project. `dotnet tool install -g DataSync`.
- **`serve` starts the same `WebApplication` the API project builds today.** The composition root moves
  out of `Program.cs` into a method the CLI and the existing entry point both call, so there is one
  wiring and not two that drift. `WebApplicationFactory<Program>` in the API tests must keep working —
  that is the constraint that decides how far the move goes.
- **`TaskRunnerDllPath` resolution is fixed.** The current default string-replaces `DataSync.Api/bin`
  with `DataSync.TaskRunner/bin`, which is true only of this repo's dev layout. New order: the
  configured value; then `DataSync.TaskRunner.dll` **beside the running assembly**, which is what every
  published layout looks like; then the dev-layout guess, kept so the repo's own inner loop is
  unchanged. Failing to find it must be a startup error naming the paths tried, not a first-run
  failure hours later.
- **RID-agnostic, framework-dependent.** LibGit2Sharp, SQLite and DuckDB.NET all ship their native
  assets under `runtimes/<rid>/`, and a framework-dependent tool carries all of them and resolves at
  run time. Self-contained is ruled out anyway: `ProcessSupervisor` spawns `dotnet exec`, which needs
  the muxer on `PATH`.

**Defaults matter here.** `dotnet tool install -g DataSync && datasync serve` has to work with no
arguments and no config file, so the defaults must land somewhere a user owns —
`%LOCALAPPDATA%/DataSync` or `~/.local/share/datasync`, not `Directory.GetCurrentDirectory()`, which is
what `ApiOptions` does today and which would scatter a config repo wherever the shell happened to be.
First run creates the repo, `git init`s it, and says on the console where it put things.

## 2. `datasync service` — Windows service registration

- `Microsoft.Extensions.Hosting.WindowsServices`, and `builder.Host.UseWindowsService()` applied only
  when the process is actually running as a service (`WindowsServiceHelpers.IsWindowsService()`), so
  `datasync serve` from a terminal is unaffected.
- `datasync service install` shells `sc.exe create` against **the tool's own apphost shim** — the
  `datasync.exe` in `%USERPROFILE%\.dotnet\tools` — with `serve` and the resolved paths as arguments,
  and `start= auto`. `uninstall` is `sc.exe delete`. `status` is `sc.exe query`, parsed into a sentence.
- **It has to say what it did and what it could not.** Registering a service needs elevation; a
  non-elevated `install` must fail with "run this from an elevated prompt" rather than an access-denied
  stack trace. And the paths baked into the service's command line are resolved and printed at install
  time, because a service that starts and cannot find its repo is a service with no console to say so.
- **The account.** Default `LocalSystem`, with `--account` for a domain account. This is not cosmetic:
  phase 52's Windows auth authorises against a group, and a connection using `IntegratedAuth` connects
  *as the service account*. The install command is where an operator finds that out.

## 3. The Docker image

- A multi-stage `Dockerfile` at the repo root: a `node:22` stage building the SPA, an SDK stage
  publishing the API and the TaskRunner **into one directory**, and `mcr.microsoft.com/dotnet/aspnet:10.0`
  as the runtime.
- **Debian, not Alpine.** LibGit2Sharp and DuckDB.NET ship glibc natives. Alpine would need musl builds
  of both and would be discovered as a runtime `DllNotFoundException`, which is the worst place to find
  out.
- Volumes: one for the config repo and one for the state database, or one holding both — the state db
  defaults inside the repo root today, and the container should keep them together so a single mount
  is a complete deployment.
- `git` is not needed in the image: LibGit2Sharp is a library, not a shell-out. Confirm rather than
  assume, since a missing binary here would only surface on the first commit.
- The image runs `serve`, binds `0.0.0.0:8080`, and declares a `HEALTHCHECK` against `/api/health`,
  which already exists.
- **`docker-compose.yml` is the dev database stack and stays that way.** A second compose file (or a
  documented `docker run`) shows the product running; folding the app into the file that stands up test
  databases would make "start my test databases" also start an application.

## 4. CI

`.github/workflows/ci.yml` gains a job that builds the tool package and the image on every push, and
publishes them on a tag. Building them only at release time means the first time anyone finds out the
Dockerfile is broken is the release.

## What this phase does not build

- Any package feed decision — whether this goes to nuget.org, a private feed, or is installed from a
  local `.nupkg`. The packaging is the same either way.
- Linux service registration (systemd unit). Worth having, not asked for, and the container covers the
  same ground on Linux hosts.
- Auto-update, or a version check. A tool that phones home is a decision, not a convenience.
- Any change to how runners are spawned. `dotnet exec` stays; this phase only fixes where the path
  comes from.

## How to verify when built

- `dotnet pack` produces a tool package; installing it into a clean container and running
  `datasync serve` with no arguments starts, creates its repo, prints where, and serves the SPA.
- A replication actually runs under the tool — which is the real test of the `TaskRunnerDllPath`
  change, because that is the path a spawned worker resolves.
- `docker build` and `docker run` reach the same state, with a mounted volume surviving a container
  restart.
- Deep-linked SPA routes return the app; unmatched `/api` routes return 404 with no HTML body.
- The existing suites are untouched by the composition-root move: `WebApplicationFactory<Program>`
  still builds the same graph, and Playwright still runs Vite against the API.
- Windows service install/uninstall/status on a Windows host, including the elevation message from a
  non-elevated prompt. This one is a manual check — there is no Windows runner in CI today, and
  pretending otherwise in a test would be worse than saying so here.

## Open questions

- **Whether the tool and the container should share a version with the assemblies**, and where that
  version is set. There is no versioning scheme in the repo at all today, which is fine until something
  is distributed and then immediately is not.
- **Whether `serve` should run the SPA build's output from `wwwroot` or from an embedded resource.**
  `wwwroot` is simpler and debuggable; embedding makes the tool a single file. Start with `wwwroot`.
- **Where the state database goes in the container.** Inside the config repo mount is one volume and
  one backup, but it also means a `git status` in that repo sees a binary that changes constantly —
  which it already does in dev, so the answer may be "it is already fine", but it should be checked
  rather than inherited.

---

# Retrospective

`dotnet tool install -g DataSync && datasync serve` starts the product, and `docker build` produces an
image that does the same. Both were verified by doing exactly that rather than by reasoning about the
packaging.

## Three things that only failed when run

The plan named the gaps it could see by reading. Running it found three more, and every one of them
produced a *working-looking* process:

- **The CLI host found no controllers at all.** `AddControllers()` discovers from the **entry**
  assembly, and under the tool the entry assembly is `DataSync.Cli`, not `DataSync.Api`. The host
  started perfectly, logged one line — "No action descriptors found" — and answered 404 to every
  route. Fixed by naming the application part explicitly, which is now the only way both entry points
  can be correct.
- **The container health check reported a healthy container as unhealthy.** It was a shell script
  using `/dev/tcp` — a bash feature — run under `/bin/sh`, which is dash on Debian. It failed every
  time. That is the specific failure that gets an orchestrator to restart something that was working.
  Replaced with `datasync health`, which needs no shell and no curl and behaves the same on any base
  image.
- **`StateOwnershipTests` asserted a filename.** Moving the composition root out of `Program.cs` broke
  it, correctly — the rule it enforces (only the API opens the state file) is unchanged and the test
  now names the file that holds the registration.

## The composition root moved, and had to

The tool's `serve` and the API's own entry point build the *same* `DataSyncHost`. Two copies would
drift the first time somebody registered a service in one of them, and the controller-discovery bug
above is what that class of drift looks like: something true of one entry point and silently false of
the other.

`WebApplicationFactory<Program>` still works, which was the constraint that decided how far the move
went.

## Defaults land somewhere a user owns

`ApiOptions` defaults to the current directory, which is right for running the API out of this repo and
wrong for a tool: `datasync serve` from wherever a shell happens to be would scatter a config repo
across a filesystem and find none of it on the second run. The tool defaults to a per-user
application-data directory, creates it, `git init`s it, and prints where — because the config repo *is*
a git repository and a first run failing on "not a repository" is not what anyone installs a tool
expecting.

## `TaskRunnerDllPath`, and how it was actually proved

The old default swapped a `DataSync.Api/bin` path segment for `DataSync.TaskRunner/bin`, which is true
of this working tree and nothing else. It now looks beside the running assembly first — which is what
every published layout is — and keeps the dev-layout guess as a fallback so the repo's inner loop is
unchanged.

Proving it needed a spawned worker, not a unit test: a replication was created against the running
container through its own API and triggered, and the run row came back `Running` with a pid. That is
the only assertion that says the path resolves *in a published layout*.

## Debian, and the reason is in the file

LibGit2Sharp, `Microsoft.Data.Sqlite` and DuckDB.NET all ship glibc natives. Alpine would find that out
as a `DllNotFoundException` at run time, which is the worst place to learn it, so the Dockerfile says
so where somebody would otherwise "optimise" the base image.

## Verification

- `ApiFallbackTests` (4) — an unmatched `/api` route is a 404 and not the page, the same for `/hubs`,
  a deep link reaches the app, and the API still answers. The exclusion is the load-bearing part: a
  fallback that returns `index.html` with a 200 makes a client parse HTML as JSON and report something
  incomprehensible, and that failure looks like a client bug from every angle except this one.
- Manual, because they are the things a test cannot claim: the tool built, installed into a clean tool
  root, and run with no arguments; the container built, serving the SPA at `/`, deep links returning
  the app, `/api/nope` returning 404, `docker inspect` reporting **healthy**, a restart finding its
  volume, and a spawned worker reporting a pid.
- CI now packs the tool, installs it into a clean tool path, runs it, builds the image and waits for it
  to answer — on every push, because the first time anyone finds out the Dockerfile is broken should
  not be the release.
- Full suite green: 796 .NET tests, 39 Playwright.

## Not built, as planned

A package feed decision, a systemd unit, auto-update, and any change to how runners are spawned.

**Windows service registration is untested.** `datasync service install|uninstall|status` is written and
its non-Windows refusal is verified, but there is no Windows runner in CI and no Windows host here.
Saying so is better than a test that asserts `sc.exe` would have been called.

## Open questions

- ~~**Versioning.**~~ `0.1.0` on the tool package, set in one place. A real scheme belongs with the
  first release, not with the first package.
- ~~**`wwwroot` or an embedded resource.**~~ `wwwroot`, published by an MSBuild target that runs only on
  publish and is skippable — `dotnet build` must not shell out to npm, or a node install sits between a
  developer and a unit test.
- ~~**Where the state database goes in the container.**~~ Inside the mounted root, so one volume is a
  complete deployment.
- **New**: the tool's `serve` has no way to run a one-off pass and exit, which is what a cron-driven
  deployment would want instead of a resident scheduler. Worth its own small command if anyone asks.
