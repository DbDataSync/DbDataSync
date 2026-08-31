using System.Globalization;
using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace DataSync.DevHarness;

/// <summary>
/// Creates, drops and seeds the scenario's databases and tables. Connects over TCP with
/// <see cref="SqlConnection"/> rather than shelling out to <c>docker exec … sqlcmd</c> (as
/// <c>tests/DataSync.Web.Tests/test-db.ts</c> does), which is what lets it reach *both* instances and
/// keeps it working on any machine that can see the ports — no assumption about tooling paths inside
/// the container image.
/// </summary>
public static class SqlBootstrap
{
    public static async Task CreateAsync(
        TargetEngine target, IReadOnlyList<HarnessTable> tables, CancellationToken cancellationToken)
    {
        Log.Step($"Creating the '{Scenario.DatabaseName}' database and {tables.Count} table(s) on the source and the {target.Name} target");

        await CreateSourceAsync(tables, cancellationToken);
        await target.RecreateAsync(tables, cancellationToken);

        var widths = string.Join(", ", tables.Select(t => $"{t.Name} ({t.Columns.Count} cols)"));
        Log.Ok($"on the source (change tracking on) and the {target.Name} target: {widths}");
    }

    public static async Task DropAsync(TargetEngine target, CancellationToken cancellationToken)
    {
        Log.Step($"Dropping the '{Scenario.DatabaseName}' database on the source and the {target.Name} target");

        try
        {
            await using var source = await OpenAsync(Scenario.SourceConnectionString(), cancellationToken);
            await ExecuteAsync(source, DropDatabaseSql, cancellationToken);
        }
        catch (Exception ex) when (ex is SqlException or HarnessException)
        {
            Log.Warn($"could not drop the database on the source: {ex.Message}");
        }

        try
        {
            await target.DropDatabaseAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is DbException or HarnessException)
        {
            Log.Warn($"could not drop the database on the {target.Name} target: {ex.Message}");
        }
    }

    private static async Task CreateSourceAsync(
        IReadOnlyList<HarnessTable> tables, CancellationToken cancellationToken)
    {
        await using (var master = await OpenAsync(Scenario.SourceConnectionString(), cancellationToken))
        {
            await ExecuteAsync(master, DropDatabaseSql, cancellationToken);
            await ExecuteAsync(master, $"CREATE DATABASE [{Scenario.DatabaseName}];", cancellationToken);
            await ExecuteAsync(master,
                $"ALTER DATABASE [{Scenario.DatabaseName}] SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = OFF);",
                cancellationToken);
        }

        await using var db = await OpenAsync(Scenario.SourceConnectionString(Scenario.DatabaseName), cancellationToken);
        foreach (var table in tables)
        {
            await ExecuteAsync(db, Scenario.CreateTableSql(table), cancellationToken);
            await ExecuteAsync(db, $"ALTER TABLE {table.QualifiedSource} ENABLE CHANGE_TRACKING;", cancellationToken);
        }
    }

    /// <summary>
    /// Bulk-loads rows into the source, starting after whatever key is already there so this can be
    /// called repeatedly to grow the table.
    /// <para>
    /// Via <see cref="SqlBulkCopy"/> rather than multi-row INSERT statements: a parameterised INSERT
    /// hits SQL Server's hard limit of 2100 parameters per request at only 420 rows of this table,
    /// and bulk copy is both unaffected by that and far faster at the sizes this exists to produce.
    /// </para>
    /// </summary>
    /// <summary>
    /// Bulk-loads rows into every generated table on the source, starting after whatever key is
    /// already there so this can be called repeatedly to grow them.
    /// <para>
    /// Via <see cref="SqlBulkCopy"/> rather than multi-row INSERT statements: a parameterised INSERT
    /// hits SQL Server's hard limit of 2100 parameters per request at only a few hundred rows of even
    /// a narrow table, and bulk copy is both unaffected by that and far faster at the sizes this
    /// exists to produce. It is also what makes widening a table free here — the mechanism never cared
    /// how many columns it was given, only the hardcoded column list did.
    /// </para>
    /// </summary>
    public static async Task SeedAsync(
        int rows, IReadOnlyList<HarnessTable> tables, CancellationToken cancellationToken)
    {
        if (rows < 1)
            throw new HarnessException("--rows must be at least 1.");

        await using var connection = await OpenAsync(Scenario.SourceConnectionString(Scenario.DatabaseName), cancellationToken);

        // Every table gets the same number of rows. Widths vary by design; row counts do not, so a
        // per-table difference in replication lag is about the shape rather than about the volume.
        foreach (var table in tables)
            await SeedTableAsync(connection, table, rows, cancellationToken);
    }

    private static async Task SeedTableAsync(
        SqlConnection connection, HarnessTable table, int rows, CancellationToken cancellationToken)
    {
        var startId = await MaxIdAsync(connection, table, cancellationToken) + 1;
        Log.Step($"Seeding {rows:N0} row(s) into {table.Name}, from Id {startId:N0}");

        // Seeded from the start key and the table index, so a table's data is reproducible and two
        // tables of the same width do not end up holding identical rows.
        var random = new Random(startId * 31 + table.Index);
        var columns = table.Columns.ToList();
        const int batchSize = 10_000;
        var written = 0;

        while (written < rows)
        {
            var batch = Math.Min(batchSize, rows - written);
            var data = new System.Data.DataTable();
            foreach (var column in columns)
                data.Columns.Add(column.Name);

            for (var i = 0; i < batch; i++)
            {
                var id = startId + written + i;
                data.Rows.Add([.. columns.Select(c => Scenario.Value(c, id, random))]);
            }

            using var bulkCopy = new SqlBulkCopy(connection) { DestinationTableName = table.QualifiedSource };
            foreach (var column in columns)
                bulkCopy.ColumnMappings.Add(column.Name, column.Name);
            await bulkCopy.WriteToServerAsync(data, cancellationToken);

            written += batch;
            if (rows > batchSize)
                Log.Info($"{table.Name}: {written:N0} / {rows:N0}");
        }

        Log.Ok($"{table.Name} now holds {await CountAsync(connection, table, cancellationToken):N0} row(s)");
    }

    public static async Task<SqlConnection> OpenAsync(string connectionString, CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
        }
        catch (SqlException ex)
        {
            await connection.DisposeAsync();
            throw new HarnessException(
                $"Could not connect to SQL Server: {ex.Message}\nHave you run `tools/dev-harness up`?");
        }

        return connection;
    }

    public static async Task ExecuteAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task<int> CountAsync(SqlConnection connection, HarnessTable table, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table.QualifiedSource};";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public static async Task<int> MaxIdAsync(SqlConnection connection, HarnessTable table, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT ISNULL(MAX(Id), 0) FROM {table.QualifiedSource};";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    /// <summary>SINGLE_USER first: an idle pooled connection from a previous harness invocation is
    /// enough to make a plain DROP DATABASE block indefinitely.</summary>
    private static string DropDatabaseSql => $"""
        IF DB_ID('{Scenario.DatabaseName}') IS NOT NULL
        BEGIN
            ALTER DATABASE [{Scenario.DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
            DROP DATABASE [{Scenario.DatabaseName}];
        END
        """;
}
