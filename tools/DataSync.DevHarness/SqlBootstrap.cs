using System.Globalization;
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
    public static async Task CreateAsync(CancellationToken cancellationToken)
    {
        Log.Step($"Creating the '{Scenario.DatabaseName}' database on both instances");

        await CreateSourceAsync(cancellationToken);
        await CreateTargetAsync(cancellationToken);

        Log.Ok($"{Scenario.QualifiedTable} exists on source (change tracking on) and target");
    }

    public static async Task DropAsync(CancellationToken cancellationToken)
    {
        Log.Step($"Dropping the '{Scenario.DatabaseName}' database on both instances");
        foreach (var (label, connectionString) in Servers())
        {
            try
            {
                await using var connection = await OpenAsync(connectionString, cancellationToken);
                await ExecuteAsync(connection, DropDatabaseSql, cancellationToken);
            }
            catch (SqlException ex)
            {
                Log.Warn($"could not drop the database on {label}: {ex.Message}");
            }
        }
    }

    private static async Task CreateSourceAsync(CancellationToken cancellationToken)
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
        await ExecuteAsync(db, Scenario.CreateTableSql, cancellationToken);
        await ExecuteAsync(db, $"ALTER TABLE {Scenario.QualifiedTable} ENABLE CHANGE_TRACKING;", cancellationToken);
    }

    private static async Task CreateTargetAsync(CancellationToken cancellationToken)
    {
        await using (var master = await OpenAsync(Scenario.TargetConnectionString(), cancellationToken))
        {
            await ExecuteAsync(master, DropDatabaseSql, cancellationToken);
            await ExecuteAsync(master, $"CREATE DATABASE [{Scenario.DatabaseName}];", cancellationToken);
        }

        // No change tracking on the target: nothing reads changes from it, and enabling it would
        // quietly suggest otherwise.
        await using var db = await OpenAsync(Scenario.TargetConnectionString(Scenario.DatabaseName), cancellationToken);
        await ExecuteAsync(db, Scenario.CreateTableSql, cancellationToken);
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
    public static async Task SeedAsync(int rows, CancellationToken cancellationToken)
    {
        if (rows < 1)
            throw new HarnessException("--rows must be at least 1.");

        await using var connection = await OpenAsync(Scenario.SourceConnectionString(Scenario.DatabaseName), cancellationToken);

        var startId = await MaxIdAsync(connection, cancellationToken) + 1;
        Log.Step($"Seeding {rows:N0} row(s) into the source, from Id {startId:N0}");

        var random = new Random(startId);
        const int batchSize = 10_000;
        var written = 0;

        while (written < rows)
        {
            var batch = Math.Min(batchSize, rows - written);
            var table = new System.Data.DataTable();
            foreach (var column in Scenario.Columns)
                table.Columns.Add(column);

            for (var i = 0; i < batch; i++)
            {
                var id = startId + written + i;
                table.Rows.Add(
                    id,
                    Scenario.Regions[random.Next(Scenario.Regions.Length)],
                    $"Customer {id:D6}",
                    Math.Round((decimal)(random.NextDouble() * 5000), 2),
                    DateTime.UtcNow);
            }

            using var bulkCopy = new SqlBulkCopy(connection) { DestinationTableName = Scenario.QualifiedTable };
            foreach (var column in Scenario.Columns)
                bulkCopy.ColumnMappings.Add(column, column);
            await bulkCopy.WriteToServerAsync(table, cancellationToken);

            written += batch;
            if (rows > batchSize)
                Log.Info($"{written:N0} / {rows:N0}");
        }

        Log.Ok($"source now holds {await CountAsync(connection, cancellationToken):N0} row(s)");
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
                $"Could not connect to SQL Server: {ex.Message}\nHave you run `scripts/dev-harness up`?");
        }

        return connection;
    }

    public static async Task ExecuteAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task<int> CountAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {Scenario.QualifiedTable};";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public static async Task<int> MaxIdAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT ISNULL(MAX(Id), 0) FROM {Scenario.QualifiedTable};";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static IEnumerable<(string Label, string ConnectionString)> Servers() =>
    [
        ("source", Scenario.SourceConnectionString()),
        ("target", Scenario.TargetConnectionString()),
    ];

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
