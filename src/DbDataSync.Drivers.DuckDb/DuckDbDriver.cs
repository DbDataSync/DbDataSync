using System.Data.Common;
using System.Diagnostics;
using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DuckDB.NET.Data;

namespace DbDataSync.Drivers.DuckDb;

/// <summary>
/// DuckDB, embedded, as a source.
/// <para>
/// A source only: one reader, no staging provider and no writer. That is not an omission to fill in
/// later so much as what the engine is here for — an embedded analytical database is where the
/// interesting *reads* are (Parquet, CSV, an S3 glob, an attached remote database, all reachable from
/// inside a query), and replicating *into* a process-local file is a different feature with a
/// different reason to exist.
/// </para>
/// </summary>
public sealed class DuckDbDriver : IDriver, IConnectionTester, IDialectProvider
{
    public string DriverType => DriverIds.DuckDb;

    public SqlDialect Dialect => DuckDbDialect.Instance;

    /// <summary>Null, not a number DuckDB would ignore: it is embedded and listens on nothing.</summary>
    public int? DefaultPort => null;

    public IReadOnlyList<IChangeReader> Readers { get; } = [new DuckDbQueryReader()];

    public IReadOnlyList<IStagingProvider> StagingProviders { get; } = [];

    public IReadOnlyList<IChangeWriter> Writers { get; } = [];

    /// <summary>
    /// The default when a connection names no address. DuckDB's own spelling for "a fresh database
    /// that exists only for this connection" — the useful default for a query-first source, because
    /// the data such a query reads comes from the scanners in the query itself, not from anything
    /// stored here.
    /// </summary>
    public const string InMemory = "Data Source=:memory:";

    /// <summary>
    /// **No credential is ever applied**, whatever the connection says. DuckDB has no users and no
    /// authentication — it is a library reading a file this process can already open — so the
    /// credential a connection may carry for SqlAuth is silently irrelevant rather than quietly
    /// spliced in somewhere it would not be honoured.
    /// </summary>
    public DbConnection CreateConnection(ConnectionConfig connection, string? credential) =>
        new DuckDBConnection(BuildConnectionString(connection)).WithCommandTimeout(connection);

    /// <summary>
    /// <c>DuckDBConnection</c> takes ADO.NET connection-string syntax and rejects a bare path — plain
    /// <c>:memory:</c> throws "Format of the initialization string does not conform to specification".
    /// But a bare path is exactly what an operator types, because it is what every DuckDB CLI example
    /// and every other client library takes, so a value with no <c>=</c> in it is read as the data
    /// source it plainly is rather than bounced back as a syntax error about a syntax nobody mentioned.
    /// </summary>
    public static string BuildConnectionString(ConnectionConfig connection)
    {
        var address = connection.ConnectionString?.Trim();
        var normalized = string.IsNullOrEmpty(address)
            ? InMemory
            : address.Contains('=') ? address : $"Data Source={address}";

        // Properties are the free-form bag every connection has had since phase 3, appended here the
        // way each other driver appends its own — DuckDB settings like `access_mode=READ_ONLY` and
        // `threads` are connection-string keys.
        var builder = new DuckDBConnectionStringBuilder { ConnectionString = normalized };
        foreach (var (key, value) in connection.Properties)
            builder[key] = value;

        return builder.ConnectionString;
    }

    // ---- Introspection: empty, and meant ----
    //
    // IDriver requires all three, and this driver answers all three with nothing. That is not a stub.
    // A DuckDB source is a *query*, and the tables of the database that query runs in are almost never
    // what it reads — the rows come from read_parquet, read_csv, an httpfs glob or an attached remote
    // database, none of which exist as catalog entries to browse. Listing the handful of tables that
    // do exist would offer a picker whose choices are the wrong answer to the question the operator is
    // being asked, which is worse than offering none: the mapping editor replaces the pickers with the
    // query editor for this reader anyway (MappingSide.tsx), and empty lists are what make that
    // honest rather than merely convenient.

    public Task<IReadOnlyList<string>> ListDatabasesAsync(
        DbConnection connection, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<IReadOnlyList<TableMetadata>> ListTablesAsync(
        DbConnection connection, string database, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TableMetadata>>([]);

    public Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(
        DbConnection connection, string database, string schema, string table, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ColumnMetadata>>([]);

    /// <summary>
    /// Round-trips <c>version()</c>. Worth having even for an embedded engine, where "can I reach it"
    /// is really "can this process open that file" — which is the failure an operator actually hits: a
    /// path that does not exist, a directory it cannot write a WAL into, or a database another process
    /// holds open.
    /// </summary>
    public async Task<ConnectionTestResult> TestAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var cmd = connection.CreateTimedCommand();
            cmd.CommandText = "SELECT version();";
            var version = await cmd.ExecuteScalarAsync(cancellationToken) as string;

            return new ConnectionTestResult(true, Stopwatch.GetElapsedTime(started), $"DuckDB {version}", null);
        }
        catch (DbException ex)
        {
            return new ConnectionTestResult(false, Stopwatch.GetElapsedTime(started), null, ex.Message);
        }
    }
}
