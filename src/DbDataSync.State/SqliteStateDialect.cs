using System.Data.Common;
using DbDataSync.Core.Sql;
using Microsoft.Data.Sqlite;

namespace DbDataSync.State;

/// <summary>
/// The default engine, and the only one that needs a retry loop.
/// <para>
/// Everything here is what the state store did before phase 63 gave it a choice of engine — the SQL
/// this renders is the SQL that was hand-written, so an existing SQLite state database is opened and
/// used exactly as it always was.
/// </para>
/// </summary>
public sealed class SqliteStateDialect : StateDialect
{
    public static SqliteStateDialect Instance { get; } = new();

    private SqliteStateDialect() { }

    public override StateEngine Engine => StateEngine.Sqlite;

    public override SqlDialect Sql => SqliteSqlDialect.Instance;

    public override DbConnection CreateConnection(string connectionString) => new SqliteConnection(connectionString);

    // Microsoft.Data.Sqlite matches on the sigil, so the bound name carries it.
    public override string ParameterName(string name) => $"${name}";

    public override string Limit(string parameterName) => $"LIMIT {Parameter(parameterName)}";

    public override string InsertOrIgnore(
        string table, string columns, string values, string? conflictTarget, string? conflictWhere = null) =>
        $"INSERT INTO {table} ({columns}) VALUES ({values}) " +
        $"ON CONFLICT{(conflictTarget is null ? "" : $" ({conflictTarget})")}" +
        $"{(conflictWhere is null ? "" : $" WHERE {conflictWhere}")} DO NOTHING;";

    public override string Upsert(
        string table, string columns, string values, string conflictTarget, string updates) =>
        $"INSERT INTO {table} ({columns}) VALUES ({values}) " +
        $"ON CONFLICT ({conflictTarget}) DO UPDATE SET {updates.Replace("EXCLUDED.", "excluded.")};";

    public override string IdentityKey(string column) => $"{column} INTEGER PRIMARY KEY AUTOINCREMENT";

    public override string Text => "TEXT";

    // SQLite's column types are advisory, so there is nothing to bound and no reason to pretend
    // otherwise. See StateDialect.Text for why the distinction exists at all.
    public override string KeyText => "TEXT";

    public override string Integer => "INTEGER";

    public override int GetSchemaVersion(DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public override void SetSchemaVersion(DbConnection connection, int version)
    {
        // PRAGMA does not accept bound parameters for its value; `version` is internally controlled
        // (a migration index), never external input.
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA user_version = {version};";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Deliberately not WAL mode: WAL relies on a shared-memory (-shm) file to coordinate multiple
    /// processes reading/writing the same database, which needs working mmap + POSIX file-locking
    /// semantics between those processes. In this project's sandboxed dev environment that
    /// coordination was observed to be unreliable across the API process and its spawned
    /// DbDataSync.TaskRunner child processes. The default rollback-journal mode uses plain file locking
    /// instead — slower under contention, but that is exactly what busy_timeout and the retry loop
    /// exist to absorb, and it does not depend on shared-memory coordination working correctly on
    /// every deployment filesystem.
    /// </summary>
    public override void OnConnectionOpened(DbConnection connection)
    {
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
    }

    // SQLITE_BUSY = 5, SQLITE_LOCKED = 6. One writer holds a lock on the whole file and everyone else
    // is refused rather than queued, which is the entire reason this engine — and only this engine —
    // needs an application-level retry.
    public override bool ShouldRetry(Exception exception) =>
        exception is SqliteException { SqliteErrorCode: 5 or 6 };
}

/// <summary>
/// SQLite's answers to the mechanical variations, for the state store's own use.
/// <para>
/// There is no SQLite replication driver — SQLite is a state engine here, not a source or a target —
/// so unlike MsSql and Postgres there was no existing dialect to reuse and this is the whole of it.
/// The column-type half of <see cref="SqlDialect"/> is not implemented, because the state store's
/// schema is fixed and internal and nothing ever asks it to map a customer's column type.
/// </para>
/// </summary>
public sealed class SqliteSqlDialect : SqlDialect
{
    public static SqliteSqlDialect Instance { get; } = new();

    private SqliteSqlDialect() { }

    public override string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    public override string ParameterReference(string name) => $"${name}";

    public override CanonicalType ToCanonicalType(string nativeType) =>
        throw new NotSupportedException(
            "SQLite is a state-store engine here, not a replication source or target — nothing maps " +
            "column types through it.");

    public override RenderedColumnType RenderColumnType(CanonicalType type) =>
        throw new NotSupportedException(
            "SQLite is a state-store engine here, not a replication source or target — nothing maps " +
            "column types through it.");
}
