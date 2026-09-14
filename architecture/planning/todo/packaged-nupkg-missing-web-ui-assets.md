# The published NuGet package ships with no web console

Confirmed this session, unrelated to the Windows service investigation that surfaced it: every released
version of the `DbDataSync` NuGet package to date — including `2026.9.12.547`, currently the newest
stable release — has an empty `wwwroot`. The web console the docs describe does not exist in the
installed tool. `MapFallback` (`DbDataSyncHost.cs`) degrades gracefully ("No web assets are published
with this build...") rather than crashing, which is exactly why this shipped unnoticed rather than as an
obvious, loud failure.

## Root cause, verified directly

`DbDataSync.Api.csproj` builds the SPA into `wwwroot` via a target scoped to publish only:

```xml
<Target Name="BuildSpa" BeforeTargets="PrepareForPublish" Condition="'$(SkipWebBuild)' != 'true'">
  <Exec Command="npm ci" WorkingDirectory="$(WebProjectDir)" />
  <Exec Command="npm run build" WorkingDirectory="$(WebProjectDir)" />
  ...
</Target>
```

Both `release.yml` and `ci.yml`'s `package` job pack, not publish:

```
dotnet pack src/DbDataSync.Cli/DbDataSync.Cli.csproj -c Release -o artifacts
```

`dotnet pack` does not cascade `PrepareForPublish` into project references. Verified empirically by
running `dotnet msbuild src/DbDataSync.Cli/DbDataSync.Cli.csproj -t:Pack -v:normal`: `PrepareForPublish`
fires for the top-level `Cli` project itself, but `BuildSpa`'s own logged message
("Building the SPA in ...") never appears — the target, defined on the *referenced* `Api` project,
never runs. `npm ci`/`npm run build` never execute; `wwwroot` never gets populated; the packed tool
ships without it.

## Scope: this affects the tool package, not the container image

The Dockerfile uses `dotnet publish` (`Dockerfile:21`), which *does* trigger `PrepareForPublish` and
therefore `BuildSpa` correctly — the container image is unaffected. Only the `dotnet tool install`
path is broken, which is the primary documented install method (`docs/install.md`'s User-local and
Windows/Linux machine-wide sections all use it) — most real installs go through it.

## Fix directions — not yet decided, needs MSBuild research before this becomes a phase doc

`PackAsTool=true` packing assembles `tools/<tfm>/any/` from the project's regular *build* output, not
publish output — so the fix isn't simply "call publish somewhere in the chain," it's making sure the
SPA's built files land wherever `PackAsTool`'s own target actually sources package content from. Three
candidate directions:

| option | idea | main risk |
| --- | --- | --- |
| a | Force `PrepareForPublish`/`BuildSpa` to run as an explicit pre-step before `dotnet pack` (in the release/ci workflows, or via a target dependency), then get that output into the tool's build/pack content | Doesn't yet resolve build-output vs publish-output mismatch — needs confirming publish output actually reaches `PackAsTool`'s package content |
| b | Retarget `BuildSpa` to also fire on a plain `Build` | Reintroduces the exact cost the publish-only scoping was chosen to avoid — an `npm ci`/`npm run build` on every inner-loop `dotnet build`/test run, which `BuildSpa`'s own comment already rules out for that reason |
| c | A narrower condition — trigger only when actually packing a tool (`PackAsTool`-specific), leaving plain `build`/`test` untouched | Needs identifying which MSBuild target `PackAsTool=true` actually depends on for `tools/<tfm>/any/` content, not yet researched |

## Open questions

- Every released version to date is affected. Once fixed, does that justify an out-of-band release
  rather than waiting for the next normal one? Not this doc's call.
- Does `ci.yml`'s `package` job's smoke test need a real assertion here (e.g. checking `wwwroot` is
  non-empty in the packed tool, or fetching `/` and checking for real SPA content rather than the
  fallback message) so this class of regression can't ship silently again?
