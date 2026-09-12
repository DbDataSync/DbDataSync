# DbDataSync

[![NuGet](https://img.shields.io/nuget/v/DbDataSync.svg?label=NuGet)](https://www.nuget.org/packages/DbDataSync)

**DbDataSync** · [Install](docs/install.md) · [Getting started](docs/getting-started.md) · [Configuration](CONFIG.md) · [Building from source](docs/development.md)

DbDataSync is a cross-database replication tool. You define a replication in a web UI — the source
table, the target table, column mappings, a schedule, and how changes are processed — and DbDataSync
keeps the target in sync. It does a full initial load, then applies incremental changes on a schedule
or on demand.

v1 supports MSSQL → MSSQL. See `architecture/planning/done/overview.md` for the broader ambition and
`architecture/detailed-design.md` for the full system design.

## Install

```sh
dotnet tool install -g DbDataSync
dbdatasync setup
```

This installs DbDataSync into your own profile and walks you through setup. See
[docs/install.md](docs/install.md) for a Windows or systemd service, a machine-wide install, or
running in a container. The package is also on
[nuget.org](https://www.nuget.org/packages/DbDataSync).

## Next steps

- [**Getting started**](docs/getting-started.md) — set up your first replication, with screenshots.
- [**Configuration**](CONFIG.md) — every flag and environment variable.
- [**Building from source**](docs/development.md) — the dev loop, tests, and repository layout.
