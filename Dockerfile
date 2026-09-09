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

# ── The image ──────────────────────────────────────────────────────────────────
# Debian, not Alpine. LibGit2Sharp, Microsoft.Data.Sqlite and DuckDB.NET all ship glibc natives, and
# a musl base finds that out as a DllNotFoundException at run time — the worst place to learn it.
FROM mcr.microsoft.com/dotnet/aspnet:10.0

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
