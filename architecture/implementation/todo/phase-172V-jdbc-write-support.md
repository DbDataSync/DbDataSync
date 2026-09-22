# Phase 172V — JDBC write support: transactions are the only real blocker

**Status**: Not started.
**Plan reference**: `architecture/planning/todo/follow-up-phase-168-hand-written-jdbc-path-vs-descriptor.md`
(open question 4 there — "how much writer symmetry to preserve" — this phase answers it directly),
`architecture/implementation/done/phase-165V-jdbc-reader-spike-ikvm-postgres.md` (the "reader first, a
writer stays possible" decision this phase carries out the second half of).

## Why this is smaller than it looks

Phase 165V's own planning doc scoped writers out because "every generic *writer* opens one [a
transaction] ... `JdbcConnection.BeginDbTransaction`'s `NotImplementedException` is simply not on the
reader path." That's still exactly true, and it's still the **only** blocker — everything else a writer
needs already exists:

- **Parameter binding** — built in phase 165V (`JdbcCommand`'s name→`?`-position translation,
  `PreparedStatement` binding by `DbType`). Every generic writer binds through `ISegmentValueBinder`
  exactly like a reader does.
- **`BatchInsertStagingProvider`** (`src/DbDataSync.Drivers.Generic/BatchInsertStagingProvider.cs`) needs
  no bulk-copy API by design — its own doc comment: *"no temp-table syntax, no bulk-load API, no
  provider-specific loader ... including ODBC and JDBC which have no bulk path at all."* It's plain
  parameterized multi-row `INSERT`, dialect-rendered, transaction-free (staging commits its own DDL/DML
  outside any writer's transaction). This should already work against a JDBC connection with **zero**
  changes.
- **Every generic writer** (`DeleteInsertWriter`, `KeyReconcileDeleteWriter`, `Scd2Writer`,
  `KeyReconcileScd2CloseWriter`, `SnapshotWriter`) is pure dialect-rendered SQL through
  `ISegmentValueBinder` and `ITableCatalog` — no bulk-copy, no vendor-specific DDL beyond what
  `SqlDialect` already abstracts. `GenericDriverBase<TSpec>.BuildWriters` already switches on every one
  of these `GenericDriverKinds` — a `JdbcDriverSpec.Writers` list naming them builds real `IChangeWriter`
  instances today, with no code change needed there at all.

So the entire gap is: **`JdbcConnection` has no working `DbTransaction`.**

```csharp
// src/DbDataSync.Drivers.Jdbc/Imported/JdbcConnection.cs:77-78
protected override DbTransaction BeginDbTransaction(IsolationLevel il) => throw new NotImplementedException(
    "JdbcConnection.BeginDbTransaction: writers are out of scope for the reader-only JDBC driver (phase 165V).");
```

```csharp
// src/DbDataSync.Drivers.Jdbc/Imported/JdbcCommand.cs:54
protected override DbTransaction? DbTransaction { get => null; set => throw new NotImplementedException(); }
```

## Design

`java.sql.Connection` carries transaction state on the connection itself (`setAutoCommit`/`commit`/
`rollback`/`setTransactionIsolation`) — unlike SQL Server's client library, a JDBC statement is never
explicitly bound to a transaction object; every statement run on a connection with `autoCommit == false`
is implicitly part of the open transaction. That makes `JdbcCommand.DbTransaction`'s setter close to a
no-op (store the value so the ADO.NET contract round-trips it; nothing else reads it), and puts all the
real work in `JdbcConnection`.

```csharp
// JdbcConnection.cs
protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
{
    JavaSqlConnection.setAutoCommit(false);
    if (isolationLevel != IsolationLevel.Unspecified)
    {
        try
        {
            JavaSqlConnection.setTransactionIsolation(JavaTransactionIsolation(isolationLevel));
        }
        catch (java.sql.SQLException ex)
        {
            JavaSqlConnection.setAutoCommit(true);
            throw new InvalidOperationException(
                $"This JDBC driver rejected isolation level '{isolationLevel}' " +
                $"(java.sql.Connection.setTransactionIsolation threw: {ex.Message}). " +
                "Leave the isolation level unspecified to use this driver's own default.", ex);
        }
    }
    return new JdbcTransaction(this, isolationLevel);
}

private static int JavaTransactionIsolation(IsolationLevel level) => level switch
{
    IsolationLevel.ReadUncommitted => java.sql.Connection.TRANSACTION_READ_UNCOMMITTED,
    IsolationLevel.ReadCommitted => java.sql.Connection.TRANSACTION_READ_COMMITTED,
    IsolationLevel.RepeatableRead => java.sql.Connection.TRANSACTION_REPEATABLE_READ,
    IsolationLevel.Serializable => java.sql.Connection.TRANSACTION_SERIALIZABLE,
    _ => throw new NotSupportedException(
        $"'{level}' has no java.sql.Connection.TRANSACTION_* equivalent — " +
        "ReadUncommitted/ReadCommitted/RepeatableRead/Serializable are the only levels JDBC expresses."),
};
```

```csharp
// new file: Imported/JdbcTransaction.cs
internal sealed class JdbcTransaction(JdbcConnection connection, IsolationLevel isolationLevel) : DbTransaction
{
    private bool _completed;

    protected override DbConnection DbConnection => connection;
    public override IsolationLevel IsolationLevel => isolationLevel;

    public override void Commit()
    {
        connection.JavaSqlConnection.commit();
        Complete();
    }

    public override void Rollback()
    {
        connection.JavaSqlConnection.rollback();
        Complete();
    }

    // ADO.NET convention: a transaction disposed without Commit()/Rollback() rolls back —
    // DeleteInsertWriter's own `await using var transaction = ...` relies on exactly this for the
    // "throw partway through" case, the same way every other engine's writer already does.
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_completed)
            Rollback();
        base.Dispose(disposing);
    }

    private void Complete()
    {
        if (_completed) return;
        _completed = true;
        connection.JavaSqlConnection.setAutoCommit(true);
    }
}
```

```csharp
// JdbcCommand.cs:54
private JdbcTransaction? _transaction;
protected override DbTransaction? DbTransaction { get => _transaction; set => _transaction = (JdbcTransaction?)value; }
```

**Isolation level in practice**: every writer in this codebase calls
`targetConnection.BeginTransactionAsync(cancellationToken)` with no level argument — always
`IsolationLevel.Unspecified` today (confirmed by grep across `DeleteInsertWriter`/
`KeyReconcileDeleteWriter`/`Scd2Writer`). So the `setTransactionIsolation` branch above is defensive
correctness for the public `DbTransaction` contract, not something the current writer call sites actually
exercise — worth building right since it's cheap, but the real proof this phase needs is the
`Unspecified` path (`setAutoCommit(false)`/`commit`/`rollback` alone).

## Enabling it

Once `BeginDbTransaction` works, a JDBC-backed engine opts in the same way any `GenericDriverSpec`/
`JdbcDriverSpec` already does — nothing writer-specific to build in `JdbcDriverSpec` or
`JdbcGenericDriver.FromDescriptor`, since `Writers`/`Staging` already flow through unchanged:

```yaml
capabilities:
  readers: [Watermark, BatchReload]
  staging: [StagingTable]
  writers: [DeleteInsert]     # or KeyReconcileDelete, Snapshot, Scd2 — any/all of them
```

## What this phase does not build

- `TriggerAuditReader`/change tracking of any kind — unrelated to writers, and JDBC has no native CDC
  regardless (see `jdbc-driver-support.md`'s own "no change-tracking strategy comes free" risk).
- Any writer more specialized than the generic four — a JDBC-specific bulk/upsert writer is exactly the
  kind of "an engine with something faster gets its own prefixed provider" case
  `BatchInsertStagingProvider`'s own doc comment describes, and is out of scope here.
- Savepoints, nested transactions, or distributed transactions — no generic writer uses any of these
  today.

## How to verify when built

The parity discipline every JDBC phase so far has used: compare against the same engine's native
ADO.NET driver on identical data, not against nothing.

- A `JdbcWriterParityTests` (next to the existing `JdbcReaderParityTests`) that: creates a table on the
  live Postgres container, runs a change set through `JdbcGenericDriver`'s `DeleteInsertWriter` (staged
  via `BatchInsertStagingProvider`), and asserts the resulting rows match what `PostgresDriver`'s own
  `DeleteInsertWriter` produces from the same input.
- A test that deliberately throws mid-transaction (e.g. a staged batch with a value that violates a
  target constraint) and asserts the target is left unchanged — proving `Dispose`'s implicit rollback
  actually rolls back on a real `java.sql.Connection`, not just that the C# code path was reached.
- At least one test per writer kind actually enabled (`DeleteInsert` at minimum; `KeyReconcileDelete`/
  `Scd2`/`Snapshot` if this phase enables more than one) — each is a different SQL shape even though they
  share the same transaction primitive, and "the transaction works" doesn't by itself prove each
  writer's own generated SQL is valid against a real JDBC `PreparedStatement`.
- Existing reader tests (`JdbcCatalogTests`, `JdbcReaderParityTests`, `JdbcChangeDatabaseTests`,
  `JdbcDescriptorTests`) stay green — this phase touches `JdbcConnection`/`JdbcCommand`, which every
  reader test already exercises indirectly.
