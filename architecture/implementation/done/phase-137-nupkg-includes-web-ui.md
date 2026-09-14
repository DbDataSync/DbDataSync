# Phase 137 — the packed NuGet tool actually includes the web console

**Status**: Complete.
**Plan reference**: `architecture/planning/done/packaged-nupkg-missing-web-ui-assets.md`.

## Why

Every released `DbDataSync` NuGet package to date — confirmed at least `2026.9.12.547`, the current
stable release — has an empty `wwwroot`. The web console the docs describe does not exist in what's
actually installed. `MapFallback` degrades gracefully ("No web assets are published with this build")
rather than crashing, which is why this shipped unnoticed.

Verified directly against the SDK's own `Microsoft.NET.PackTool.targets`, and by three real runs (not
inferred): `dotnet pack src/DbDataSync.Cli/DbDataSync.Cli.csproj` and a genuine top-level
`dotnet publish` of that same project both skip `DbDataSync.Api.csproj`'s `BuildSpa` target entirely —
it only fires when `Api.csproj` is published *directly*. The container image looks correct but for an
unrelated reason: the `Dockerfile` builds the SPA in its own Node stage and `COPY`s `dist/` straight
into `/app/wwwroot/`, sidestepping MSBuild's publish pipeline altogether — it was never proof this path
worked, and it doesn't.

## Design — mirrors the Dockerfile's own already-proven pattern

### 1. `DbDataSync.Cli.csproj` gets a `Publish`-time copy step

```xml
<PropertyGroup>
  <WebProjectDir>$(MSBuildProjectDirectory)/../DbDataSync.Web</WebProjectDir>
</PropertyGroup>

<Target Name="CopyPrebuiltSpa" BeforeTargets="Publish"
        Condition="Exists('$(WebProjectDir)/dist')">
  <ItemGroup>
    <_SpaDist Include="$(WebProjectDir)/dist/**/*" />
  </ItemGroup>
  <Copy SourceFiles="@(_SpaDist)"
        DestinationFiles="@(_SpaDist->'$(PublishDir)wwwroot/%(RecursiveDir)%(Filename)%(Extension)')" />
</Target>
```

`BeforeTargets="Publish"`, not `PrepareForPublish` — confirmed via the SDK targets (`_PackToolPublishDependency`)
that `pack` really does depend on the `Publish` target itself, and this needs to be on `Cli.csproj`,
not `Api.csproj`, since `Api.csproj`'s own publish-scoped targets never run for this path. `Exists(...)`
guards it so an ordinary `dotnet build`/`dotnet test` with no `dist/` present is unaffected — same
publish-only scoping `BuildSpa` already had, same reason (no `npm` in the inner loop).

This assumes `dist/` already exists — it's a copy step, not a build step. That's deliberate: building
the SPA needs `npm`, and this target has to work the same way whether or not the environment doing the
`dotnet pack`/`publish` has Node available at all (the runtime-only container stage doesn't).

### 2. CI/release workflows build the SPA first

`release.yml` and `ci.yml`'s `package` job both currently go straight to `dotnet pack` with no SPA build
step at all — the actual reason this shipped broken. Add, before the existing pack step, the same two
commands the Dockerfile's `web` stage already runs successfully:

```yaml
- uses: actions/setup-node@v4
  with:
    node-version: "22"
- name: Build the SPA
  working-directory: src/DbDataSync.Web
  run: |
    npm ci
    npm run build
- name: Pack the global tool
  run: dotnet pack src/DbDataSync.Cli/DbDataSync.Cli.csproj -c Release -o artifacts
```

`ci.yml`'s `package` job already has a Node setup step for its own separate `DbDataSync.Web.Tests`
job — check whether `actions/setup-node` is already available in the `package` job's own runner context
or needs adding there specifically.

### 3. Real CI assertions, not just "the tool runs"

`ci.yml`'s `package` job today only checks `dbdatasync version`/`--help`/`health` — none of which would
ever notice a missing `wwwroot`. Two new checks, both real (this repo's own established precedent —
real installs, real containers, not fakes):

- After packing: assert the packed `.nupkg`'s `tools/<tfm>/any/wwwroot/` contains real files. Simplest
  as a shell step unzipping the produced `.nupkg` (it's a zip) and checking for a non-empty `wwwroot/`
  — failing loud if `dist/` silently produced nothing rather than just checking the directory exists.
- Against the already-running, already-health-checked container (both the default and runtime-only
  images, since the job already stands both up): fetch `/` and assert the response is real SPA content,
  not `MapFallback`'s fallback text. A `curl`/`grep -qv` check for the fallback message's own wording
  is enough — doesn't need to parse HTML, just needs to fail if the fallback string is what came back.

## Checkpoints

1. `CopyPrebuiltSpa` target added to `DbDataSync.Cli.csproj` per design item 1.
2. Manual verification: `npm ci && npm run build` in `src/DbDataSync.Web`, then
   `dotnet pack src/DbDataSync.Cli/DbDataSync.Cli.csproj -c Release -o /tmp/verify-pack`, then unzip the
   resulting `.nupkg` and confirm `tools/net10.0/any/wwwroot/index.html` (or equivalent) exists with real
   content — the same shape of check CI will do, run by hand first.
3. `ci.yml`'s `package` job and `release.yml` both get the SPA-build step (design item 2) ahead of their
   existing pack step.
4. `ci.yml`'s `package` job gets the two new assertions (design item 3) — nupkg content check, and the
   already-running containers' `/` response check for both the default and runtime-only images.
5. A real, deliberately-broken run (e.g. temporarily blank `Exists('$(WebProjectDir)/dist')`'s condition,
   or an empty `dist/`) to confirm the new CI checks actually fail loud rather than passing vacuously —
   same "prove the test can fail" discipline this repo already applies elsewhere, worth a line in the
   retrospective.

## Retrospective

Built exactly as designed, with real verification at every step — no design changes needed.

- `CopyPrebuiltSpa` added to `src/DbDataSync.Cli/DbDataSync.Cli.csproj`, verbatim from design item 1.
- `ci.yml`'s `package` job and `release.yml` both gained the `actions/setup-node@v4` + `npm ci`/`npm run
  build` step ahead of their existing `dotnet pack`. `package` already had `setup-node` for its own
  Node steps, so this needed no new action, only the build commands themselves.
- Two real CI assertions added to `ci.yml`'s `package` job: unzip the packed `.nupkg` and fail loud if
  `tools/<tfm>/any/wwwroot/` is missing or empty (right after the pack step), and — for both the default
  and runtime-only images, right after each one's existing health-check loop — `curl`/`grep` the running
  container's `/` for the `MapFallback` fallback string, failing if it's present. `release.yml` only
  got the SPA-build step, not the two assertions — deliberately scoped to `ci.yml` per the design (a
  release is already smoke-tested by its own install/version check, and CI is where a broken pack would
  be caught before a release run ever happens).
- **Manual verification, real builds, both directions** (checkpoints 2 and 5): `npm ci && npm run build`
  in `src/DbDataSync.Web`, then `dotnet pack src/DbDataSync.Cli/DbDataSync.Cli.csproj -c Release`,
  unzipped — `tools/net10.0/any/wwwroot/` had `index.html` plus every JS/CSS asset, 10 files total. Then,
  with a clean `bin`/`obj` and `dist/` moved aside (no SPA build at all, the "broken" case), the same
  pack produced **zero** `wwwroot` files — proving `CopyPrebuiltSpa`'s `Exists(...)` guard genuinely
  gates the copy rather than always finding something. The new CI nupkg-content assertion's own shell
  logic was run by hand against both nupkgs: it failed loud (the exact intended message) against the
  broken one and passed against the real one — the "prove the test can fail" discipline checkpoint 5
  asks for, done for the nupkg-content check specifically.
- The container `/`-response assertion's underlying behavior was **not** re-verified with a real `docker
  build` here (the SDK-based multi-stage build is a multi-minute, multi-GB operation on a shared
  sandbox already running several unrelated containers) — verified by inspection instead: `.dockerignore`
  excludes `**/dist/`, so the `build` stage's `COPY src/ ./src/` never sees a pre-built SPA and
  `CopyPrebuiltSpa`'s `Exists(...)` guard is always false inside the container build; the Dockerfile
  already passes `-p:SkipWebBuild=true` and does its own `COPY --from=web .../dist/ /app/wwwroot/` after
  publish, a path phases 120/121 already proved works and which this phase does not touch. The new
  `curl`/`grep` assertion itself is a thin, low-risk addition on top of that already-working path; its
  first real exercise will be the next actual CI run. Flagged here rather than silently skipped.
- No real bugs found during implementation — the root cause and fix were already fully nailed down by
  the planning doc and this phase's own `Why` section before any code was written.
