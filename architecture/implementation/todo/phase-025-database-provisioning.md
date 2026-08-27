# Phase 25 — Database provisioning

**Status**: Planned, not started
**Plan reference**: `architecture/planning/done/database-provisioning-and-lifecycle-scripts.md`. Answers
the open product question carried by `architecture/planning/todo/change-tracking-odbc-jdbc.md`
("Should DataSync create triggers on a source at all?") for the whole class of DDL, not just triggers.

## What this phase will build

Setting up a replication currently has two dead ends that the product offers no way out of:

- a source without Change Tracking fails at run time — `MsSqlChangeTrackingReader.cs:97` throws
  *"Change Tracking is not enabled for table 'dbo.Orders' in database 'Sales'"* and nothing in the UI
  can fix it
- a target table that does not exist yet fails at staging — `"Target column 'X' was not found on
  'dbo.Orders'"` — and there is no CREATE TABLE generator anywhere outside the staging providers, which
  build from the *target's* types and so cannot help create the target

This phase makes both a previewable, applyable action: DataSync generates the script, shows it, and runs
it when a human says so.

### 1. The driver contract — `IProvisioner`

Opt-in, in the same shape as `IConnectionTester` and `ISegmentExpandingReader`, for the same reason: a
driver reaching an arbitrary engine through ODBC has no DDL it can name, and requiring the method would
mean implementing something it cannot honour. Callers ask `driver is IProvisioner`; capability discovery
reports the answer so the UI hides an affordance that could never work.

```csharp
public interface IProvisioner
{
    /// Which actions this driver can plan. Declared, never string-matched by callers.
    IReadOnlyList<string> SupportedActions { get; }

    /// Inspects current state and returns the statements that would change it. Runs no DDL.
    Task<ProvisioningPlan> PlanAsync(
        DbConnection connection, ProvisioningRequest request, CancellationToken cancellationToken);
}
```

**Planning and applying are separate calls, and only the host applies.** This is the same rule the
scripting slots follow — *a provisioner returns a description of what to do; the host does it* — and it
is what makes the preview honest: the text shown to the operator is the text that will be executed,
because there is no second code path that could produce different SQL.

```csharp
public sealed record ProvisioningPlan(
    string Action,
    ProvisioningState State,               // Satisfied | Missing | Unsupported | Unknown
    IReadOnlyList<ProvisioningStep> Steps,
    IReadOnlyList<string> Warnings);

/// <param name="Scope">Which database the step must run against — some steps are not in the same
/// database context as the rest of the plan (see "the database-scoped step" below).</param>
public sealed record ProvisioningStep(
    string Title,                          // "Enable Change Tracking on database [Sales]"
    string CommandText,
    string? Rationale,
    ProvisioningStepScope Scope);
```

Steps rather than one blob because the UI has to explain each one, because they do not all run in the
same database context, and because a partial failure has to name which statement failed.

Actions are string constants, not an enum, for the reason `ScriptSlots` gives — they are persisted in
URLs and an action added later must not renumber existing ones:

```csharp
public static class ProvisioningActions
{
    public const string EnableSourceChangeCapture = "enableSourceChangeCapture";
    public const string CreateTargetTable         = "createTargetTable";
}
```

`EnableSourceChangeCapture`, not `EnableChangeTracking`: the same action is a publication on Postgres, a
shadow table plus triggers for the generic `TriggerAudit` reader the ODBC/JDBC doc proposes, and CDC if
that reader ever exists. What needs enabling depends on **which reader kind is configured**, so the
reader kind is on the request and the plan is a function of it — a driver whose configured reader needs
no source cooperation (`Watermark`, `BatchReload`) returns `State = Satisfied` with no steps.

### 2. The canonical type system

`CREATE TABLE` on the target needs the **source's** column types translated. Nothing in the codebase
does this; the only SQL Server → Postgres translation that exists is five hand-written columns in
`tools/DataSync.DevHarness/TargetEngine.cs:232`.

Two new hooks on `SqlDialect`, via a canonical intermediate so N engines cost 2N implementations rather
than N²:

```csharp
public abstract CanonicalType     ToCanonicalType(string nativeType);
public abstract RenderedColumnType RenderColumnType(CanonicalType type);

public sealed record CanonicalType(
    CanonicalTypeKind Kind, int? Length, int? Precision, int? Scale, bool IsUnicode, bool IsMax);

/// <param name="Fidelity">Null when the mapping is faithful. Otherwise a sentence naming exactly what
/// is lost — this is the output that stops a silent truncation, so it is a return value, not a doc
/// comment.</param>
public sealed record RenderedColumnType(string Sql, string? Fidelity);
```

`CanonicalTypeKind`: `Boolean, Int8, Int16, Int32, Int64, Decimal, Float, Double, String, Binary, Date,
Time, Timestamp, TimestampTz, Guid, Json, Xml, Unmappable`.

The pairs that must be right, and are the tests:

| SQL Server | canonical | Postgres | fidelity |
| --- | --- | --- | --- |
| `bit` | Boolean | `boolean` | faithful |
| `tinyint` | Int8 | `smallint` | widened; Postgres has no 1-byte integer |
| `int`, `bigint` | Int32/64 | `integer`, `bigint` | faithful |
| `decimal(18,2)`, `money` | Decimal | `numeric(18,2)`, `numeric(19,4)` | `money` loses its currency semantics, keeps its scale |
| `nvarchar(50)` | String(50, unicode) | `varchar(50)` | faithful |
| `nvarchar(max)` | String(max) | `text` | **no length bound at the target** |
| `datetime2(7)` | Timestamp(7) | `timestamp(6)` | **one digit of sub-second precision lost** |
| `datetime` | Timestamp(3) | `timestamp(3)` | faithful |
| `datetimeoffset` | TimestampTz | `timestamptz` | faithful |
| `uniqueidentifier` | Guid | `uuid` | faithful |
| `varbinary(max)` | Binary(max) | `bytea` | faithful |
| `sql_variant`, `hierarchyid`, `geography` | Unmappable | — | plan reports `Unsupported` |

and the reverse direction (`text` → `nvarchar(max)`, `boolean` → `bit`, `uuid` → `uniqueidentifier`,
`jsonb` → `nvarchar(max)` with a fidelity note, array types → `Unmappable`).

**`Unmappable` produces a plan in `Unsupported` state naming the column and its type — never a guess.**
A wrong guess here is a target column that silently truncates or refuses every row, discovered in
production.

### 3. The concrete provisioners

The contract above is not the deliverable on its own. This phase builds the real implementations, and
the statements they emit are specified here rather than left to be invented during implementation.

#### `MsSqlProvisioner` — `enableSourceChangeCapture`

State detection, in order, each one a reason the plan can stop:

```sql
-- precondition: Change Tracking requires a primary key (MsSqlChangeTrackingReader.cs:133 refuses
-- without one, so a plan that ignored this would generate DDL whose success still leaves a broken run)
SELECT 1 FROM sys.key_constraints
 WHERE parent_object_id = OBJECT_ID(@qualifiedName) AND type = 'PK';

-- database level
SELECT 1 FROM sys.change_tracking_databases WHERE database_id = DB_ID(@database);

-- table level (must be evaluated in the source database's own context)
SELECT 1 FROM sys.change_tracking_tables WHERE object_id = OBJECT_ID(@qualifiedName);

-- only when the mapping's reader options set snapshotIsolation=true
SELECT snapshot_isolation_state FROM sys.databases WHERE database_id = DB_ID(@database);
```

No primary key produces `State = Unsupported` naming the table, not a plan that would half-work.

The steps, each emitted only when its check says it is missing:

```sql
ALTER DATABASE [Sales] SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 7 DAYS, AUTO_CLEANUP = ON);
ALTER DATABASE [Sales] SET ALLOW_SNAPSHOT_ISOLATION ON;   -- only if snapshotIsolation is configured
ALTER TABLE [dbo].[Orders] ENABLE CHANGE_TRACKING WITH (TRACK_COLUMNS_UPDATED = OFF);
```

Three things about that list are decisions, not transcription:

- **`ALLOW_SNAPSHOT_ISOLATION` is in scope and is the reason this action is more than two statements.**
  `phase-012-change-tracking-read-consistency.md` lists *"Enabling `ALLOW_SNAPSHOT_ISOLATION` from the
  application"* under what it explicitly did **not** build, and notes that `dev-harness up` does not
  enable it either — so `MsSqlChangeTrackingReader`'s `snapshotIsolation` option, which exists to stop a
  concurrent delete stranding a reported insert, is today unusable without someone running that
  statement by hand and knowing to. Closing that is most of the practical value of this action.
- **`TRACK_COLUMNS_UPDATED = OFF`**, because nothing reads `CHANGETABLE`'s column mask —
  `MsSqlChangeTrackingStatement` projects mapped columns unconditionally — and `ON` costs write-path
  storage on every tracked table for a feature no reader uses.
- **The two `ALTER DATABASE` statements carry `ProvisioningStepScope.Database`** and the `ALTER TABLE`
  carries `Scope.Table`: the first two are database-scoped settings shared by every mapping on that
  database, and the third must execute in the source database's own context.

**Permissions are the interesting failure here, and are not papered over.**
`MsSqlChangeTrackingReader`'s type doc states the driver *"only ever queries it, never enables it (keeps
the app's own DB permissions to read-only + VIEW CHANGE TRACKING, per §6's least-privilege guidance)"* —
and that stays true: the *runner* never runs DDL. But provisioning applies through the **same
`ConnectionConfig` and the same credential**, so against a correctly least-privileged connection Apply
will fail with SQL Server's own permission error. That is the expected and correct outcome, not a defect:
it is exactly the case **Copy** exists for, and it is why Copy is specified as equal to Apply rather than
as a convenience. A separate elevated provisioning credential is an open question below, not a silent
assumption. Update the reader's type doc comment in this phase so it says the *driver* never enables
Change Tracking rather than implying nothing in the product does.

Because the two `ALTER DATABASE` statements are database-wide, `enableSourceChangeCapture` is also the
action that fixes the harness gap: `tools/dev-harness up` should call the same provisioner rather than
keep its own hand-written copy in `SqlBootstrap.cs`, which is how the two stay from drifting.

#### `PostgresProvisioner`

`enableSourceChangeCapture` returns `State = Satisfied` with **zero steps** — the Postgres driver
registers only the generic pipeline (`Watermark`, `BatchReload`), and neither reader needs any source
cooperation. That is not a stub: "this reader requires nothing" is the answer the UI needs in order to
show a green badge rather than an empty card, and the same code path serves MsSql's `Watermark` and
`BatchReload` readers. Logical replication publications fill this action in whenever
`change-tracking-postgres.md` becomes a phase.

`createTargetTable` is fully implemented on both drivers.

### 4. What `createTargetTable` generates, and what it deliberately does not

From the source's `ColumnMetadata` (already available via `IDriver.ListColumnsAsync`), for the columns
the mapping actually maps, named by their **target** column names:

- the translated type, and `NOT NULL` from the source's `IsNullable`
- the primary key, from `IsPrimaryKey` — **required**, not optional: `MsSqlChangeTrackingReader.cs:133`
  refuses a table without one and both MERGE writers key on it, so a target table created without a PK
  is a table that fails on its first run
- everything quoted with the target dialect's `QuoteIdentifier`, which is what keeps a mixed-case column
  name working on Postgres

Explicitly **not** propagated: **identity/generated columns**. The source's `IsIdentity` is dropped and
the target column is created plain, with a warning saying so. DataSync writes explicit values into the
target; an identity column there means every single write has to bracket itself with `SET
IDENTITY_INSERT` or `OVERRIDING SYSTEM VALUE` (phase 20's finding, `SqlDialect.WriteWithGeneratedColumn
OverrideAsync`). Recreating an identity on a replication target is a foot-gun with no upside — the values
come from the source.

The renderer itself lives in `DataSync.Drivers.Generic` as `CreateTableStatement.Build(dialect,
qualifiedTable, columns)` — beside `StagingStatement.BuildCreate`, which is the existing precedent for
"a CREATE TABLE that every driver shares" and which it should not duplicate.

### 5. The one automatic case

`TableMappingConfig` gains:

```csharp
public ProvisioningConfig Provisioning { get; set; } = new();

public sealed class ProvisioningConfig
{
    /// Target-side, additive only, never ALTER, never source DDL. Off by default.
    public bool CreateTargetTableIfMissing { get; set; }
}
```

Checked in `RunExecutor.RunMappingAsync` before the segment loop: if the flag is set and the target table
is absent, plan and apply `createTargetTable`, logging **every statement it ran** at Info into the run's
log. If the table exists, nothing happens — no ALTER, no column reconciliation; a missing mapped column
still fails with the existing staging error. That boundary is the whole safety argument: the only DDL
DataSync ever runs unattended creates something that does not exist.

`enableSourceChangeCapture` has no automatic mode at any setting.

### 6. API

```
GET  /api/replications/{replicationName}/table-mappings/{mappingName}/provisioning
     -> { source: ProvisioningPlan, target: ProvisioningPlan }
POST /api/replications/{replicationName}/table-mappings/{mappingName}/provisioning/{action}/apply
     -> { steps: [{ title, succeeded, error, elapsedMs }], state: ProvisioningState }
```

Both sides in one GET because the Setup card shows both and two round trips would let them disagree.
Apply re-plans first and executes what it just planned, rather than trusting a plan the client is holding
— the source may have changed since the preview, and applying stale DDL is exactly the failure the
preview is meant to prevent.

`GET /api/connections/{name}/capabilities` gains `supportedProvisioningActions: string[]`, alongside the
existing `supportsConnectionTest`, from the same declarative source.

New `src/DataSync.Api/Services/ProvisioningService.cs` — opens both connections, reads the source's
column metadata, builds the request, delegates to each driver's provisioner. Follows
`MetadataService`/`DriverConnectionFactory` for connection and credential handling.

### 7. SPA

A **Setup** card on the table mapping form (`pages/replication-detail/TableMappingForm.tsx`), below the
column mapping editor, appearing once both sides are picked. Per side:

- a state badge — `Satisfied` / `Missing` / `Unsupported`, reusing `StatusBadge`
- fidelity warnings, above the SQL, where they cannot be scrolled past
- the generated SQL in a read-only monospace block
- **Copy** and **Apply**. Copy matters as much as Apply: the enterprise-normal path is a DBA who will
  not grant DataSync `ALTER DATABASE` and wants the script to run themselves.
- Apply confirms first, naming the connection and database it is about to change

Plus the `Create target table if missing` checkbox, with the boundary stated inline ("creates the table
only if it does not exist; never alters an existing one").

## How it will be verified

**Unit** (`DataSync.Drivers.Generic.Tests`, `DataSync.Drivers.MsSql.Tests`, `DataSync.Drivers.Postgres.Tests`)
- table-driven round trips for every pair in the type table above, both directions, asserting the
  rendered SQL **and** the presence/absence of a fidelity note — a faithful mapping that emits a warning
  is as much a bug as a lossy one that does not
- `Unmappable` types produce `Unsupported`, naming the column
- `CREATE TABLE` render: PK present, nullability correct, identity dropped with a warning, a column named
  `Order]Id` quoted so it cannot break out, a mixed-case name surviving to Postgres
- a plan for a reader kind that needs no source cooperation is `Satisfied` with zero steps

- `MsSqlProvisioner` plan shapes, against faked catalog results: no PK → `Unsupported`; CT already on at
  both levels → `Satisfied` with zero steps; database on, table off → exactly one `ALTER TABLE` step;
  `snapshotIsolation` configured and `snapshot_isolation_state = 0` → the `ALLOW_SNAPSHOT_ISOLATION`
  step present, and absent when the option is off
- `PostgresProvisioner.enableSourceChangeCapture` → `Satisfied`, zero steps

**Integration** (`Category=Integration`, the two containers plus the Postgres one)
- enable Change Tracking on a fresh table via the plan, then run `MsSqlChangeTrackingReader` against it
  successfully — the end-to-end proof that the generated DDL is the DDL that was needed
- the same, with `snapshotIsolation=true` on the reader: the plan's `ALLOW_SNAPSHOT_ISOLATION` step runs
  and the reader's snapshot transaction then succeeds instead of failing with error 3952. This is the
  test that proves the gap `phase-012` left open is actually closed
- `createTargetTable` MsSql → MsSql, then a full mapping run through the created table
- `createTargetTable` MsSql → Postgres, same
- `CreateTargetTableIfMissing` on a mapping whose target is absent: the run succeeds and logs the DDL;
  re-running is a no-op
- applying with a least-privileged credential fails with SQL Server's own permission message surfaced
  intact, and the plan is still readable and copyable — the enterprise-normal path must not be a
  dead end

**E2E** (`tests/DataSync.Web.Tests`)
- the Setup card on a mapping whose source lacks Change Tracking shows `Missing` and the DDL; Apply flips
  it to `Satisfied`

## Decisions taken before implementation

- **Plan and apply are separate, and apply re-plans.** Stale DDL applied from a held preview is the
  precise failure the preview exists to prevent.
- **Fidelity warnings are a return value, not documentation.** The cross-engine decision was taken
  knowing this is where silent truncation lives; a warning the operator has to go and read is not a
  mitigation.
- **Identity is never recreated on the target.** See above.
- **Actions are keyed to the configured reader kind**, so this generalises to Postgres publications and
  the `TriggerAudit` shadow table without a second mechanism.

## Out of scope

- `ALTER` of an existing target table — adding a missing column, widening a type, adding a PK. The
  existing "target column not found" error stands. This is the obvious phase 27 and should stay separate:
  ALTER is where destructive mistakes live, and it needs its own diff-and-confirm interaction.
- Index creation beyond the primary key; permission grants; `CREATE DATABASE` / `CREATE SCHEMA`.
- CDC (as opposed to Change Tracking), and the `TriggerAudit` trigger DDL — those arrive with their
  readers, using this contract.
- Any authorization model around who may press Apply. The product has none yet; this phase does not
  invent one, and that gap should be stated in the UI copy rather than papered over.

## Open questions to resolve during implementation

- **The database-scoped step.** `ALTER DATABASE Sales SET CHANGE_TRACKING = ON` is shared by every
  mapping on that database, but the plan is per-mapping. Current intent: include it in every plan and let
  the live state check make it disappear once any mapping has run it, so the operator never has to know
  which mapping "owns" it. Confirm that reads naturally in the UI before building anything cleverer.
- **What retention to generate.** `7 DAYS, AUTO_CLEANUP = ON` is written above as a placeholder so the
  statement is concrete, not because seven is right. The harness uses `2 DAYS, AUTO_CLEANUP = OFF`, which
  is a harness choice. Retention shorter than the replication's schedule interval guarantees the
  `Change Tracking history no longer covers watermark` failure at `MsSqlChangeTrackingReader.cs:59`;
  deriving it from `SchedulingConfig` with an override is the likely answer, but the multiplier is a
  judgement call worth making explicitly rather than inheriting from a placeholder.
- **Does a connection need a separate provisioning credential?** Applying uses the connection's own
  credential, which for a correctly least-privileged connection cannot run `ALTER DATABASE`. Copy covers
  that path, and adding a second credential means a second secret to manage and a second way to
  misconfigure. Worth deciding explicitly rather than discovering that every real deployment uses Copy.
- **Where `createTargetTable` gets its schema when the target schema does not exist either.** Creating it
  is one more statement and arguably still "additive only" — but it is a wider grant than `CREATE TABLE`
  and may deserve to stay manual.
