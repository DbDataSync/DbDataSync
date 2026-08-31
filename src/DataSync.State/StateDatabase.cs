using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace DataSync.State;

/// <summary>
/// The central state database (architecture/detailed-design.md §3.7), shared by DataSync.Api and every
/// DataSync.TaskRunner process.
/// <para>
/// SQLite by default and on any deployment that has not said otherwise — phase 63 added SQL Server and
/// PostgreSQL as alternatives for operators who would rather this lived on infrastructure they already
/// run. What engine is in use changes nothing above this class: the stores are written against one SQL
/// text and one parameter spelling, and <see cref="StateDialect"/> renders the differences.
/// </para>
/// </summary>
public sealed partial class StateDatabase
{
    /// <summary>
    /// Matches the <c>$name</c> placeholders every store writes.
    /// <para>
    /// The stores keep writing SQLite's spelling and this rewrites it, rather than each query being
    /// assembled from dialect calls. That is deliberate: a query written as one readable string is a
    /// query somebody can check against what the database will run, and phase 63 was a port of ~2,400
    /// lines that had to be provably unchanged in meaning. Interpolating a dialect call per parameter
    /// would have made every one of those lines a new thing to verify.
    /// </para>
    /// </summary>
    [GeneratedRegex(@"\$([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex ParameterPlaceholder { get; }

    private const int MaxRetryAttempts = 5;

    private readonly string _connectionString;

    public StateDialect Dialect { get; }

    /// <summary>The SQLite path constructor, unchanged from before there was a choice — an existing
    /// deployment reaches this and nothing about its database moves.</summary>
    public StateDatabase(string sqliteFilePath)
        : this(StateEngine.Sqlite, SqliteConnectionString(sqliteFilePath))
    {
    }

    public StateDatabase(StateEngine engine, string connectionString)
    {
        Dialect = StateDialect.For(engine);
        _connectionString = connectionString;

        using var connection = OpenRawConnection();
        EnsureSchema(connection);
    }

    /// <summary>
    /// Pooling=False: Microsoft.Data.Sqlite pools native sqlite3 handles by connection string by
    /// default. Combined with this file being written by *other processes* (DataSync.TaskRunner), a
    /// pooled handle reused across separate OpenConnection() calls risks observing a stale snapshot
    /// from whenever it was first opened instead of picking up commits made by other processes since —
    /// each caller here already treats a connection as fully short-lived (open, one query, dispose),
    /// so there is no pooling benefit worth that risk.
    /// <para>
    /// Not applied to the other two engines, where a connection reaches a server rather than a file
    /// and pooling is how they are meant to be used.
    /// </para>
    /// </summary>
    private static string SqliteConnectionString(string sqliteFilePath)
    {
        var dir = Path.GetDirectoryName(sqliteFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        return new SqliteConnectionStringBuilder { DataSource = sqliteFilePath, Pooling = false }.ToString();
    }

    /// <summary>
    /// Opens a connection and re-verifies the schema exists on *every* call, not just once at
    /// construction — cheap (a single version read in the common case) and makes this self-healing
    /// against a real, still not fully root-caused problem observed in this project's sandboxed dev
    /// environment: a long-lived process (DataSync.Api) that spawns child processes
    /// (DataSync.TaskRunner) writing to this same file could, after the child had been running for
    /// tens of seconds, start seeing an apparently-schema-less view of the database on a fresh
    /// connection ("no such table"), despite migration having already run successfully at startup and
    /// other processes' writes to the same path being independently verifiable on disk. Disabling
    /// pooling and WAL mode did not fully resolve it either. Given the underlying cause sits below
    /// this application (most likely something about how this sandbox's process/filesystem isolation
    /// interacts with a Node-launched process tree spawning further dotnet child processes — see
    /// architecture/implementation/done/phase-006-spa.md), re-checking and re-applying the schema on
    /// every open is a pragmatic, low-cost way to make correctness not depend on fully understanding
    /// that cause.
    /// </summary>
    public DbConnection OpenConnection()
    {
        var connection = OpenRawConnection();
        EnsureSchema(connection);
        return connection;
    }

    private DbConnection OpenRawConnection()
    {
        var connection = Dialect.CreateConnection(_connectionString);
        connection.Open();
        Dialect.OnConnectionOpened(connection);
        return connection;
    }

    /// <summary>
    /// A command whose <c>$name</c> placeholders have been rewritten for this engine — what every
    /// store creates instead of calling <c>connection.CreateCommand()</c> directly.
    /// </summary>
    public DbCommand Command(DbConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = Dialect.Engine == StateEngine.Sqlite
            ? sql
            : ParameterPlaceholder.Replace(sql, match => Dialect.Parameter(match.Groups[1].Value));
        return command;
    }

    /// <summary>The same, enlisted in a transaction — the form every multi-statement write uses.</summary>
    public DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql)
    {
        var command = Command(connection, sql);
        command.Transaction = transaction;
        return command;
    }

    /// <summary>Caps a result set — <c>LIMIT</c>, or SQL Server's <c>OFFSET/FETCH</c>. Interpolated
    /// into a query's text, which is why it takes a parameter *name* rather than a value.</summary>
    public string Limit(string parameterName) => Dialect.Limit(parameterName);

    /// <summary>
    /// Runs a unit of work, retrying it if the engine says it is worth retrying.
    /// <para>
    /// **Only SQLite ever says so.** Its retry loop exists because a writer holds a lock on the whole
    /// file and every other process is refused rather than queued. A client-server engine queues that
    /// contention internally, so there is nothing here for an application-level loop to improve — the
    /// plan doc suspected as much and this is the confirmation, not an omission.
    /// </para>
    /// </summary>
    public T Retry<T>(Func<T> action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (Exception ex) when (Dialect.ShouldRetry(ex) && attempt < MaxRetryAttempts)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(50 * attempt));
            }
        }
    }

    public void Retry(Action action) => Retry<object?>(() =>
    {
        action();
        return null;
    });

    private void EnsureSchema(DbConnection connection)
    {
        var scripts = Migrations.ScriptsFor(Dialect);
        var currentVersion = Dialect.GetSchemaVersion(connection);

        for (var i = currentVersion; i < scripts.Count; i++)
        {
            using (var transaction = connection.BeginTransaction())
            {
                foreach (var statement in scripts[i])
                {
                    using var cmd = connection.CreateCommand();
                    cmd.Transaction = transaction;
                    cmd.CommandText = statement;
                    cmd.ExecuteNonQuery();
                }

                transaction.Commit();
            }

            Dialect.SetSchemaVersion(connection, i + 1);
        }
    }
}

/// <summary>Parameter binding, in the one shape every store uses.</summary>
public static class StateCommandExtensions
{
    /// <summary>
    /// Binds a value, taking the parameter's bare name — the <c>$</c>, <c>@</c> or nothing is the
    /// engine's business, not the caller's.
    /// </summary>
    public static DbCommand Bind(this DbCommand command, StateDatabase database, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = database.Dialect.ParameterName(name);
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
        return command;
    }
}
