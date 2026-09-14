# The published NuGet package ships with no web console

**Resolved 2026-09-14.** Turned into `architecture/implementation/todo/phase-137-nupkg-includes-web-ui.md`
— a `Publish`-time copy target on `DbDataSync.Cli.csproj` (not `Api.csproj`) pulling in an
already-built `dist/`, an explicit `npm ci`/`npm run build` step added to `ci.yml`/`release.yml` ahead
of their existing pack step, and real CI assertions (nupkg content, and a running instance's actual
response) replacing the current "the tool runs and answers `--help`" smoke test.

Confirmed this session, unrelated to the Windows service investigation that surfaced it: every released
version of the `DbDataSync` NuGet package to date — including `2026.9.12.547`, currently the newest
stable release — has an empty `wwwroot`. The web console the docs describe does not exist in the
installed tool. `MapFallback` (`DbDataSyncHost.cs`) degrades gracefully ("No web assets are published
with this build...") rather than crashing, which is exactly why this shipped unnoticed rather than as an
obvious, loud failure.

**Decided 2026-09-14**: no urgent out-of-band release once fixed — the normal release cadence is fine.
CI needs a real assertion for this going forward (not just "the tool runs and answers `--help`") — both
that the packed output actually contains the web assets, and that a running instance actually serves
real content, not just the graceful-fallback message.

## Root cause, verified directly against the SDK's own targets

`DbDataSync.Api.csproj` builds the SPA into `wwwroot` via a target scoped to publish:

```xml
<Target Name="BuildSpa" BeforeTargets="PrepareForPublish" Condition="'$(SkipWebBuild)' != 'true'">
  <Exec Command="npm ci" WorkingDirectory="$(WebProjectDir)" />
  <Exec Command="npm run build" WorkingDirectory="$(WebProjectDir)" />
  ...
</Target>
```

Both `release.yml` and `ci.yml`'s `package` job run `dotnet pack src/DbDataSync.Cli/DbDataSync.Cli.csproj`.
Read the SDK's own `Microsoft.NET.PackTool.targets` directly (not inferred — the actual file, from the
locally installed 10.0.112 SDK) to find out what `PackAsTool=true` packing actually depends on:

```xml
<_PackToolPublishDependency Condition="... and '$(GeneratePackageOnBuild)' != 'true' ...">Publish</_PackToolPublishDependency>
...
<ItemGroup>
  <_PublishFiles Condition="'$(_ToolPackageShouldIncludeImplementation)' == 'true'" Include="$(PublishDir)/**/*" />
</ItemGroup>
```

So `pack` genuinely does depend on `Publish` (neither project sets `GeneratePackageOnBuild`, confirmed by
grep), and package content really is globbed from `$(PublishDir)`. That much matches the obvious
assumption. What doesn't: three separate real tests this session, each run to completion, not inferred —

| what was actually run | did `BuildSpa` fire? | `wwwroot` in the output? |
| --- | --- | --- |
| `dotnet msbuild src/DbDataSync.Cli/DbDataSync.Cli.csproj -t:Pack` | no | — |
| `dotnet publish src/DbDataSync.Cli/DbDataSync.Cli.csproj` (a genuine top-level publish) | no | no |
| `dotnet publish src/DbDataSync.Api/DbDataSync.Api.csproj` directly | **yes** | yes |

`BuildSpa` only ever fires when `DbDataSync.Api.csproj` is the project actually being published.
Publishing (or packing) `DbDataSync.Cli.csproj` — the thing we actually ship — never triggers a
*referenced* project's own `PrepareForPublish`; MSBuild's standard project-reference handling for
`Publish` builds a reference's assembly output, it doesn't invoke the reference's own publish-scoped
targets. This is a real, general MSBuild behavior, not something specific to this repo's setup.

## The container image was never actually testing this path either

Corrected mid-session: I'd claimed the container image was unaffected "because it uses `dotnet publish`"
— true that it publishes, but that reasoning was wrong regardless, since publishing `Cli.csproj` doesn't
trigger `BuildSpa` no matter what. The `Dockerfile` actually sidesteps the whole MSBuild mechanism
entirely:

```dockerfile
FROM node:22-bookworm-slim AS web
...
RUN npm ci
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
...
RUN dotnet publish src/DbDataSync.Cli/DbDataSync.Cli.csproj -c Release -o /app -p:SkipWebBuild=true
COPY --from=web /src/DbDataSync.Web/dist/ /app/wwwroot/
```

The SPA is built in its own Docker stage with plain `npm`, and `COPY`'d straight into the published
output's `wwwroot/` — `SkipWebBuild=true` here is just making sure the (already-dead, for this path)
`BuildSpa` target doesn't redundantly try. The container image happens to be correct, but by an entirely
separate mechanism than anything MSBuild's publish/pack pipeline does for the `Cli.csproj` project.

## The fix: mirror the Dockerfile's own proven pattern

Not three undecided options anymore — one validated design:

1. **Build the SPA as an explicit step**, before packing, in whatever CI/release workflow does the
   packing — the exact `npm ci && npm run build` in `src/DbDataSync.Web` the Docker `web` stage already
   does successfully.
2. **A new small MSBuild target on `DbDataSync.Cli.csproj` itself** (not `Api.csproj` — that project's
   `BuildSpa` never gets a chance to run for this path and doesn't need to), hooked `BeforeTargets="Publish"`,
   copying `$(WebProjectDir)/dist/**` into `$(PublishDir)wwwroot/`. Since step 1 already built `dist/`
   and `Publish` is confirmed (from the SDK targets above) to genuinely run as part of `pack`, this lands
   the SPA in exactly the directory `PackTool`'s own `$(PublishDir)/**/*` glob picks up — for `dotnet pack`
   *and* a direct `dotnet publish src/DbDataSync.Cli/...`, both, since both really do invoke `Publish`.

This needs `dist/` to already exist before packing runs (same precondition the Docker build already has),
so CI/release workflows need the explicit `npm ci`/`npm run build` step added before their existing
`dotnet pack` step — they don't have one today, which is the actual reason this shipped broken. Local
dev (`dotnet build`/`dotnet test`) stays untouched — the new target only fires on `Publish`, matching
`BuildSpa`'s own original publish-only scoping and its stated reason (not putting a `npm ci` in front of
an ordinary unit test run).

## CI verification (decided 2026-09-14, not yet designed)

Two real assertions needed, replacing/extending `ci.yml`'s `package` job (today only checks
`dbdatasync version`/`--help`/`health`):

- The packed tool's `tools/<tfm>/any/wwwroot/` actually contains real files (not just that the directory
  exists — a build that silently produced an empty `dist/` should still fail this).
- The running service actually serves real SPA content at `/`, not `MapFallback`'s "No web assets are
  published with this build" message — i.e. fetch `/` from the already-running-and-healthy container/tool
  the job starts today, and assert on its content rather than only its status code.
