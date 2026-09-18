# DbDataSync

[![NuGet](https://img.shields.io/nuget/v/DbDataSync.svg?label=NuGet)](https://www.nuget.org/packages/DbDataSync)

**DbDataSync** · [Install](https://github.com/DbDataSync/DbDataSync/blob/main/docs/install.md) · [Configuration](https://github.com/DbDataSync/DbDataSync/blob/main/docs/configuration.md) · [Getting started](https://github.com/DbDataSync/DbDataSync/blob/main/docs/getting-started.md) · [Replication concepts](https://github.com/DbDataSync/DbDataSync/blob/main/docs/replication-concepts.md) · [Drivers and libraries](https://github.com/DbDataSync/DbDataSync/blob/main/docs/drivers-and-libraries.md) · [State database](https://github.com/DbDataSync/DbDataSync/blob/main/docs/state-database.md) · [Building from source](https://github.com/DbDataSync/DbDataSync/blob/main/docs/development.md)

DbDataSync is a cross-database replication tool. You define a replication in a web UI — the source
table, the target table, column mappings, a schedule, and how changes are processed — and DbDataSync
keeps the target in sync. It does a full initial load, then applies incremental changes on a schedule
or on demand.

A replication's source and target can be different engines — a SQL Server source can feed a
PostgreSQL target, for example. Support varies by engine:

| Feature                       | SQL Server                     | PostgreSQL                | MySQL / MariaDB           | Oracle                            | DuckDB           |
| ------------------------------ | ------------------------------- | -------------------------- | --------------------------- | ----------------------------------- | ------------------ |
| Source (read from)            | Yes                             | Yes                        | Yes                         | Yes                                  | Yes (query only) |
| Target (write to)             | Yes                             | Yes                        | Yes                         | Yes                                  | No               |
| Incremental sync              | Native (Change Tracking / CDC)  | Native (logical decoding)  | Watermark column             | Native (Flashback Version Query)    | N/A              |
| Backfill / batch reload       | Yes                             | Yes                        | Yes                         | Yes                                  | N/A              |
| Bulk staging                  | Native (SqlBulkCopy)            | Native (binary COPY)       | Generic (batched insert)     | Generic (batched insert)            | N/A              |
| Delete detection (reconcile)  | Yes                             | Yes                        | Yes                         | Yes                                  | N/A              |
| SCD2 target                   | Yes                             | Yes                        | Yes                         | Yes                                  | N/A              |

Every engine above also offers `TriggerAudit`, a portable trigger-based option — see
[Replication concepts](https://github.com/DbDataSync/DbDataSync/blob/main/docs/replication-concepts.md#reader-kinds).
DuckDB is a source only, for query-based sources like Parquet, CSV, an S3 glob, or an attached
database. It is not a replication target. See `architecture/planning/done/overview.md` for the
broader ambition and `architecture/detailed-design.md` for the full system design.

The table above is the five built-in drivers. Anything else reachable through an ADO.NET
provider — SQLite, Firebird, anything ODBC — plugs in as a source without a DbDataSync rebuild, through
a YAML descriptor. See [Drivers and libraries](https://github.com/DbDataSync/DbDataSync/blob/main/docs/drivers-and-libraries.md).

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
- [**Replication concepts**](https://github.com/DbDataSync/DbDataSync/blob/main/docs/replication-concepts.md) —
  change detection, bulk loading, and delete reconciliation.
- [**Drivers and libraries**](https://github.com/DbDataSync/DbDataSync/blob/main/docs/drivers-and-libraries.md) —
  add another engine (MySQL, Oracle, ...) without a rebuild.
- [**State database**](https://github.com/DbDataSync/DbDataSync/blob/main/docs/state-database.md) —
  what DbDataSync tracks about its own runs, and where it lives.
- [**Building from source**](https://github.com/DbDataSync/DbDataSync/blob/main/docs/development.md) —
  the dev loop, tests, and repository layout.
