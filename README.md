# DbDataSync

[![NuGet](https://img.shields.io/nuget/v/DbDataSync.svg?label=NuGet)](https://www.nuget.org/packages/DbDataSync)

**DbDataSync** · [Install](https://github.com/DbDataSync/DbDataSync/blob/main/docs/install.md) · [Configuration](https://github.com/DbDataSync/DbDataSync/blob/main/docs/configuration.md) · [Getting started](https://github.com/DbDataSync/DbDataSync/blob/main/docs/getting-started.md) · [Building from source](https://github.com/DbDataSync/DbDataSync/blob/main/docs/development.md)

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
[Install](https://github.com/DbDataSync/DbDataSync/blob/main/docs/install.md) for a Windows or
systemd service, a machine-wide install, or running in a container. The package is also on
[nuget.org](https://www.nuget.org/packages/DbDataSync).

## Next steps

- [**Configuration**](https://github.com/DbDataSync/DbDataSync/blob/main/docs/configuration.md) —
  every flag and environment variable.
- [**Getting started**](https://github.com/DbDataSync/DbDataSync/blob/main/docs/getting-started.md) —
  set up your first replication, with screenshots.
- [**Building from source**](https://github.com/DbDataSync/DbDataSync/blob/main/docs/development.md) —
  the dev loop, tests, and repository layout.
