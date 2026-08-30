# syntax=docker/dockerfile:1

# ── The SPA ────────────────────────────────────────────────────────────────────
FROM node:22-bookworm-slim AS web
WORKDIR /src/DataSync.Web
COPY src/DataSync.Web/package*.json ./
RUN npm ci
COPY src/DataSync.Web/ ./
RUN npm run build

# ── The .NET side ──────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY DataSync.slnx ./
COPY src/ ./src/
COPY tests/ ./tests/
COPY tools/ ./tools/

# The SPA is built in its own stage above, so this one needs no node. SkipWebBuild is what stops the
# publish target shelling out to npm inside an image that does not have it.
RUN dotnet publish src/DataSync.Cli/DataSync.Cli.csproj \
    -c Release -o /app -p:SkipWebBuild=true

COPY --from=web /src/DataSync.Web/dist/ /app/wwwroot/

# ── The image ──────────────────────────────────────────────────────────────────
# Debian, not Alpine. LibGit2Sharp, Microsoft.Data.Sqlite and DuckDB.NET all ship glibc natives, and
# a musl base finds that out as a DllNotFoundException at run time — the worst place to learn it.
FROM mcr.microsoft.com/dotnet/aspnet:10.0

# One mount is a complete deployment: the config repository and the state database live together, so
# a backup of this directory is a backup of everything that is not the image.
ENV DATASYNC_HOME=/var/lib/datasync
VOLUME ["/var/lib/datasync"]

WORKDIR /app
COPY --from=build /app/ ./

EXPOSE 8080

# The tool checks itself. The runtime image has no curl and no bash, so a shell-based check has to
# pick its way around both — the first attempt used /dev/tcp under /bin/sh, which is a bash feature
# under dash, and reported a healthy container as unhealthy every time.
HEALTHCHECK --interval=30s --timeout=10s --start-period=20s --retries=3 \
    CMD ["dotnet", "/app/DataSync.Cli.dll", "health", "--url", "http://127.0.0.1:8080"]

ENTRYPOINT ["dotnet", "/app/DataSync.Cli.dll", "serve", "--url", "http://0.0.0.0:8080"]
CMD ["--repo", "/var/lib/datasync"]
