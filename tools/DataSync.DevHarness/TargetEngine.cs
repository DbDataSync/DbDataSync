using System.Data.Common;
using System.Globalization;
using DataSync.Core.Config;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DataSync.DevHarness;

/// <summary>
/// Which engine the harness replicates *into*. The source is always SQL Server — Change Tracking is
/// what makes the incremental story demonstrable, and no other engine in scope has an equivalent yet.
/// <para>
/// Selected with <c>--target-engine postgres</c>, or by setting
/// <c>DATASYNC_HARNESS_TARGET_ENGINE</c> once so every subsequent verb agrees without repeating the
/// flag — <c>verify</c> and <c>drift</c> have to reach the same target <c>up</c> configured, and a
/// flag silently forgotten on one of them reports a difference that is really a misconfiguration.
/// </para>
/// </summary>
public abstract class TargetEngine
{
    public static TargetEngine MsSql { get; } = new MsSqlTarget();
    public static TargetEngine Postgres { get; } = new PostgresTarget();

    public static TargetEngine Resolve(HarnessArgs args)
    {
        var name = args.String("target-engine")
            ?? Environment.GetEnvironmentVariable("DATASYNC_HARNESS_TARGET_ENGINE")
            ?? "mssql";

        return name.ToLowerInvariant() switch
        {
            "mssql" or "sqlserver" => MsSql,
            "postgres" or "postgresql" or "pg" => Postgres,
            _ => throw new HarnessException($"Unknown --target-engine '{name}' (use mssql or postgres)."),
        };
    }

    public abstract string Name { get; }
    public abstract ConnectionDriverType DriverType { get; }
    public abstract string Host { get; }
    public abstract int Port { get; }
    public abstract string UserId { get; }
    public abstract string Password { get; }

    /// <summary>The database a connection lands in when the scenario's own does not exist yet.</summary>
    public abstract string AdminDatabase { get; }

    public abstract string ConnectionString(string? database = null);
    public abstract DbConnection Create(string connectionString);
    public abstract string Quote(string identifier);

    /// <summary>This engine's spelling for one harness column type. The pair of this and
    /// <see cref="Scenario.SourceColumnType"/> is what keeps the target's shape identical in *meaning*
    /// to the source's: the point of the harness is to make divergence visible, which needs a target
    /// whose shape cannot be the explanation.</summary>
    public abstract string ColumnType(HarnessColumnType type);

    public string CreateTableSql(HarnessTable table)
    {
        var columns = table.Columns.Select(c => $"    {Quote(c.Name)} {ColumnType(c.Type)}");
        return $"CREATE TABLE {QualifiedTable(table)} (\n{string.Join(",\n", columns)}\n);";
    }

    /// <summary>
    /// The staging provider and writer Kinds for a replication into this engine. The *reader* is not
    /// here: it belongs to the source, which is always SQL Server.
    /// </summary>
    public abstract (string Reader, string Cache, string Writer) Pipeline { get; }

    /// <summary>Whether this engine's pipeline replaces the target's rows wholesale on every pass
    /// rather than applying just the changes. True for a reload pipeline, which is what a target with
    /// no upsert writer of its own has to use — see the phase 20 retrospective.</summary>
    public abstract bool IsFullReload { get; }

    public string QualifiedTable(HarnessTable table) => $"{Quote(SchemaName)}.{Quote(table.Name)}";
    public abstract string SchemaName { get; }

    /// <summary>The scenario database's name *as this engine spells it*. Postgres folds unquoted
    /// identifiers to lower case, so its copy is lower-cased and every connection string, endpoint and
    /// DROP has to agree on that.</summary>
    public virtual string TargetDatabaseName => Scenario.DatabaseName;

    public async Task<DbConnection> OpenAsync(string? database, CancellationToken cancellationToken)
    {
        var connection = Create(ConnectionString(database));
        try
        {
            await connection.OpenAsync(cancellationToken);
        }
        catch (DbException ex)
        {
            await connection.DisposeAsync();
            throw new HarnessException(
                $"Could not connect to the {Name} target: {ex.Message}\nHave you run `tools/dev-harness up`?");
        }

        return connection;
    }

    public async Task ExecuteAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> CountAsync(DbConnection connection, HarnessTable table, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {QualifiedTable(table)};";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public async Task<int> MaxIdAsync(DbConnection connection, HarnessTable table, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COALESCE(MAX({Quote("Id")}), 0) FROM {QualifiedTable(table)};";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    /// <summary>Recreates the scenario database and every generated table, dropping whatever was
    /// there.</summary>
    public abstract Task RecreateAsync(IReadOnlyList<HarnessTable> tables, CancellationToken cancellationToken);

    public abstract Task DropDatabaseAsync(CancellationToken cancellationToken);

    /// <summary>Deletes <paramref name="count"/> arbitrary rows. <c>DELETE TOP (n)</c> and
    /// <c>DELETE … LIMIT n</c> are not the same statement, which is the whole reason this is virtual
    /// rather than a format string.</summary>
    public abstract Task<int> DeleteSomeAsync(
        DbConnection connection, HarnessTable table, int count, CancellationToken cancellationToken);

    public abstract Task<int> AlterSomeAsync(
        DbConnection connection, HarnessTable table, int count, CancellationToken cancellationToken);

    private sealed class MsSqlTarget : TargetEngine
    {
        public override string Name => "mssql";
        public override ConnectionDriverType DriverType => ConnectionDriverType.MsSql;
        public override string Host => "localhost";
        public override int Port => 14331;
        public override string UserId => "sa";
        public override string Password => Scenario.SaPassword;
        public override string AdminDatabase => "master";
        public override string SchemaName => "dbo";

        public override string ConnectionString(string? database = null) =>
            Scenario.ServerConnectionString(Host, Port, database);

        public override DbConnection Create(string connectionString) => new SqlConnection(connectionString);

        public override string Quote(string identifier) => $"[{identifier}]";

        /// <summary>Identical to the source's, since the source is the same engine.</summary>
        public override string ColumnType(HarnessColumnType type) => Scenario.SourceColumnType(type);

        // Change Tracking on the source, MERGE into the target: the incremental path this harness was
        // built to demonstrate.
        public override (string Reader, string Cache, string Writer) Pipeline =>
            ("MsSqlChangeTracking", "MsSqlStagingTable", "MsSqlMerge");

        public override bool IsFullReload => false;

        public override async Task RecreateAsync(IReadOnlyList<HarnessTable> tables, CancellationToken cancellationToken)
        {
            await using (var master = await OpenAsync(null, cancellationToken))
            {
                await ExecuteAsync(master, DropSql, cancellationToken);
                await ExecuteAsync(master, $"CREATE DATABASE [{Scenario.DatabaseName}];", cancellationToken);
            }

            // No change tracking on the target: nothing reads changes from it, and enabling it would
            // quietly suggest otherwise.
            await using var db = await OpenAsync(Scenario.DatabaseName, cancellationToken);
            foreach (var table in tables)
                await ExecuteAsync(db, CreateTableSql(table), cancellationToken);
        }

        public override async Task DropDatabaseAsync(CancellationToken cancellationToken)
        {
            await using var master = await OpenAsync(null, cancellationToken);
            await ExecuteAsync(master, DropSql, cancellationToken);
        }

        private static string DropSql => $"""
            IF DB_ID('{Scenario.DatabaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{Scenario.DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{Scenario.DatabaseName}];
            END
            """;

        public override async Task<int> DeleteSomeAsync(
            DbConnection connection, HarnessTable table, int count, CancellationToken cancellationToken)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"DELETE TOP ({count}) FROM {QualifiedTable(table)};";
            return await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        public override async Task<int> AlterSomeAsync(
            DbConnection connection, HarnessTable table, int count, CancellationToken cancellationToken)
        {
            // The text column, because every generated table has one whatever its width — table 1 has
            // a single filler and it is always Text.
            var text = Quote(table.TextColumn);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"UPDATE TOP ({count}) {QualifiedTable(table)} SET {text} = CONCAT('DRIFTED ', {text});";
            return await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private sealed class PostgresTarget : TargetEngine
    {
        public override string Name => "postgres";
        public override ConnectionDriverType DriverType => ConnectionDriverType.Postgres;
        public override string Host => "localhost";
        public override int Port => 15432;
        public override string UserId => "datasync";
        public override string Password =>
            Environment.GetEnvironmentVariable("DATASYNC_POSTGRES_PASSWORD") ?? "DataSync_Test_Pw1";
        public override string AdminDatabase => "postgres";
        public override string SchemaName => "public";

        public override string TargetDatabaseName => Scenario.DatabaseName.ToLowerInvariant();

        private string Database => TargetDatabaseName;

        public override string ConnectionString(string? database = null) =>
            new NpgsqlConnectionStringBuilder
            {
                Host = Host,
                Port = Port,
                Username = UserId,
                Password = Password,
                Database = database is null ? AdminDatabase : database.ToLowerInvariant(),
                Timeout = 5,
            }.ConnectionString;

        public override DbConnection Create(string connectionString) => new NpgsqlConnection(connectionString);

        public override string Quote(string identifier) => $"\"{identifier}\"";

        public override string ColumnType(HarnessColumnType type) => type switch
        {
            HarnessColumnType.Key => "integer NOT NULL PRIMARY KEY",
            HarnessColumnType.Text => "varchar(20) NOT NULL",
            HarnessColumnType.Decimal => "numeric(18,2) NOT NULL",
            HarnessColumnType.Int => "integer NOT NULL",
            HarnessColumnType.Bool => "boolean NOT NULL",
            HarnessColumnType.Date => "date NOT NULL",
            HarnessColumnType.Timestamp => "timestamp(3) NOT NULL",
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown harness column type."),
        };

        /// <summary>
        /// Reload, not incremental. There is no generic upsert writer yet, and the only reconciling
        /// writer a non-SQL-Server target has is <c>DeleteInsert</c> — which replaces everything in
        /// scope. Pairing that with an incremental reader would delete the target and re-insert only
        /// the rows that changed, so the reader has to be the one that re-reads everything.
        /// </summary>
        public override (string Reader, string Cache, string Writer) Pipeline =>
            ("BatchReload", "StagingTable", "DeleteInsert");

        public override bool IsFullReload => true;

        public override async Task RecreateAsync(IReadOnlyList<HarnessTable> tables, CancellationToken cancellationToken)
        {
            await using (var admin = await OpenAsync(null, cancellationToken))
            {
                // WITH (FORCE) drops the database even if a pooled connection is still parked on it,
                // which is the normal state after any earlier verb.
                await ExecuteAsync(admin, $"DROP DATABASE IF EXISTS \"{Database}\" WITH (FORCE);", cancellationToken);
                await ExecuteAsync(admin, $"CREATE DATABASE \"{Database}\";", cancellationToken);
            }

            await using var db = await OpenAsync(Database, cancellationToken);
            foreach (var table in tables)
                await ExecuteAsync(db, CreateTableSql(table), cancellationToken);
        }

        public override async Task DropDatabaseAsync(CancellationToken cancellationToken)
        {
            await using var admin = await OpenAsync(null, cancellationToken);
            await ExecuteAsync(admin, $"DROP DATABASE IF EXISTS \"{Database}\" WITH (FORCE);", cancellationToken);
        }

        public override async Task<int> DeleteSomeAsync(
            DbConnection connection, HarnessTable table, int count, CancellationToken cancellationToken)
        {
            var qualified = QualifiedTable(table);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                DELETE FROM {qualified}
                WHERE "Id" IN (SELECT "Id" FROM {qualified} LIMIT {count});
                """;
            return await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        public override async Task<int> AlterSomeAsync(
            DbConnection connection, HarnessTable table, int count, CancellationToken cancellationToken)
        {
            var qualified = QualifiedTable(table);
            var text = Quote(table.TextColumn);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                UPDATE {qualified}
                SET {text} = 'DRIFTED ' || {text}
                WHERE "Id" IN (SELECT "Id" FROM {qualified} ORDER BY "Id" DESC LIMIT {count});
                """;
            return await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
