# syntax=docker/dockerfile:1

# ── The SPA ────────────────────────────────────────────────────────────────────
FROM node:22-bookworm-slim AS web
WORKDIR /src/DbDataSync.Web
COPY src/DbDataSync.Web/package*.json ./
RUN npm ci
COPY src/DbDataSync.Web/ ./
RUN npm run build

# ── The .NET side ──────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY DbDataSync.slnx ./
COPY src/ ./src/
COPY tests/ ./tests/
COPY tools/ ./tools/

# The SPA is built in its own stage above, so this one needs no node. SkipWebBuild is what stops the
# publish target shelling out to npm inside an image that does not have it.
RUN dotnet publish src/DbDataSync.Cli/DbDataSync.Cli.csproj \
    -c Release -o /app -p:SkipWebBuild=true

COPY --from=web /src/DbDataSync.Web/dist/ /app/wwwroot/

# Phase 160: the docs, beside the SPA — the publish above ran in a stage that never had docs/ (only src, tests
# and tools are copied in), so the csproj's CopyDocs target had nothing to copy and this is what ships them.
# Phase 162: and the pictures they show — only those listed in docs/images.txt, at the same repo-relative path, so the
# docs' own `../screenshots/...` references resolve unchanged. (tr strips a CR: a Windows checkout may have CRLF endings.)
COPY docs/ /src/docs/
COPY screenshots/ /src/screenshots/
RUN mkdir -p /app/wwwroot/docs \
    && cp /src/docs/*.md /app/wwwroot/docs/ \
    && tr -d '\r' < /src/docs/images.txt | while IFS= read -r image; do \
         [ -z "$image" ] || install -D "/src/$image" "/app/wwwroot/$image"; \
       done

# Phase 121: every KnownLibraries entry, restored once at its pinned version, while this stage still
# has both the SDK and the just-published CLI (the "internal" command exists only for this — see
# InternalCommand.cs). Shipped into *both* final images below: it's a few megabytes for all seven
# entries (measured, not guessed — Oracle's the largest at ~6MB), so there's no real cost to giving
# the default (SDK) image the same no-network fast path for a catalog install that the runtime-only
# image needs to have at all.
RUN dotnet /app/DbDataSync.Cli.dll internal build-catalog-cache /app/library-cache

# ── The runtime-only image (phase 121) ─────────────────────────────────────────
# mcr.microsoft.com/dotnet/aspnet:10.0 — no SDK, so LibraryInstaller can never shell out to
# `dotnet publish`. LibraryInstaller.InstallOrDeferAsync (via SdkAvailability.HasSdk) notices and
# falls back to copying a catalog id at its pinned version from /app/library-cache above; anything
# else is written and left "pending restore" until `config library sync` runs somewhere with an SDK —
# see docs/install.md and the Libraries admin screen's own pending-restore state.
#
# This stage is deliberately NOT the last one in this file (see the final stage below) — `docker
# build .` with no --target must keep resolving to the SDK-based image, exactly as phase 120 decided.
# Build this one explicitly: `docker build --target runtime -t dbdatasync:<v>-runtime .`
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

ENV DbDataSync__RepoRoot=/var/lib/dbdatasync
VOLUME ["/var/lib/dbdatasync"]

WORKDIR /app
COPY --from=build /app/ ./

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=10s --start-period=20s --retries=3 \
    CMD ["dotnet", "/app/DbDataSync.Cli.dll", "health", "--url", "http://127.0.0.1:8080"]

ENTRYPOINT ["dotnet", "/app/DbDataSync.Cli.dll", "serve", "--url", "http://0.0.0.0:8080"]

# ── The default image ───────────────────────────────────────────────────────────
# Debian, not Alpine. LibGit2Sharp, Microsoft.Data.Sqlite and DuckDB.NET all ship glibc natives, and
# a musl base finds that out as a DllNotFoundException at run time — the worst place to learn it.
#
# The SDK, not just the ASP.NET runtime (phase 120) — LibraryInstaller shells out to `dotnet publish`
# to restore a library an operator installs from the web console, and `publish` (not `restore`) is
# what produces the flat lib/ with native assets and a .deps.json (phase 109c). Every non-container
# deployment already has the SDK (`dbdatasync` is a `dotnet tool`, which requires it); the container
# was the only place a web-triggered install could not run `dotnet publish` at all. Bigger
# (~250MB → ~750MB uncompressed base) — accepted; the `runtime` stage above is the alternative for a
# shop that only ever installs catalog drivers and wants the smaller, SDK-less footprint back.
#
# This is the true last stage in the file on purpose: `docker build .` / `docker compose -f
# docker-compose.app.yml build` with no --target picks whichever stage is positionally last, and
# phase 120 already decided the SDK image stays the default — moving it earlier and leaving `runtime`
# last would silently flip that default the next time someone edits this file without noticing the
# ordering was load-bearing.
FROM mcr.microsoft.com/dotnet/sdk:10.0

# One mount is a complete deployment: the config repository and the state database live together, so
# a backup of this directory is a backup of everything that is not the image.
#
# DbDataSync__RepoRoot is not a container-only name — it's the environment-variable form of the
# DbDataSync:RepoRoot config key (phase 112), the same one an interactive install or a Windows/Linux
# service points at its own machine-wide data directory with. This container happens to already
# resolve /var/lib/dbdatasync as CliOptions.DefaultRoot's own Linux answer, but setting it explicitly
# here means the image's behaviour doesn't depend on that coincidence continuing to hold.
ENV DbDataSync__RepoRoot=/var/lib/dbdatasync
VOLUME ["/var/lib/dbdatasync"]

WORKDIR /app
COPY --from=build /app/ ./

EXPOSE 8080

# The tool checks itself. The runtime image has no curl and no bash, so a shell-based check has to
# pick its way around both — the first attempt used /dev/tcp under /bin/sh, which is a bash feature
# under dash, and reported a healthy container as unhealthy every time.
HEALTHCHECK --interval=30s --timeout=10s --start-period=20s --retries=3 \
    CMD ["dotnet", "/app/DbDataSync.Cli.dll", "health", "--url", "http://127.0.0.1:8080"]

ENTRYPOINT ["dotnet", "/app/DbDataSync.Cli.dll", "serve", "--url", "http://0.0.0.0:8080"]
