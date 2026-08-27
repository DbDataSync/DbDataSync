using System.Diagnostics;
using DataSync.Benchmarks;
using Microsoft.Data.SqlClient;

// Compares candidate in-memory shapes for a batch of changes, through a real sink.
// See architecture/planning/todo/optimize-in-memory-data-column-oriented.md for what it established.

var args_ = Environment.GetCommandLineArgs();
string? Arg(string name)
{
    var i = Array.IndexOf(args_, name);
    return i >= 0 && i + 1 < args_.Length ? args_[i + 1] : null;
}
bool Flag(string name) => Array.IndexOf(args_, name) >= 0;

if (Flag("--help") || Flag("-h"))
{
    Console.WriteLine("""
        DataSync benchmarks — measures the cost of how a batch of changes is held in memory.

        Usage: tools/benchmarks [options]

          --rows N        rows to write            (default 200000)
          --columns N     columns per row          (default 50)
          --batch N       batch size               (default 10000)
          --server S      SQL Server connection    (default: the docker-compose source instance,
                                                    or $DATASYNC_TEST_MSSQL_SERVER)

        Each representation is measured in its own process, so peak working set belongs to that
        variant alone, against two sinks: a real SqlBulkCopy, and a consumer that reads typed values.
        The difference between them is the boxing floor a sink's object-per-cell contract imposes.
        """);
    return 0;
}

var rows = int.Parse(Arg("--rows") ?? "200000");
var columns = int.Parse(Arg("--columns") ?? "50");
var batch = int.Parse(Arg("--batch") ?? "10000");
var server = Arg("--server")
    ?? Environment.GetEnvironmentVariable("DATASYNC_TEST_MSSQL_SERVER")
    ?? "Data Source=localhost,14330;User ID=sa;Password=DataSync_Test_Pw1;TrustServerCertificate=True";

// ---- child mode: run exactly one variant and report on stdout -----------------------------------
if (Arg("--variant") is { } variantName)
{
    var representation = Enum.Parse<Representation>(variantName, ignoreCase: true);
    var typedSink = Arg("--sink") == "typed";
    var database = Arg("--database")!;
    var table = $"Bench_{representation}_{(typedSink ? "typed" : "bulk")}";

    var childBuilder = new SqlConnectionStringBuilder(server) { InitialCatalog = database };
    await using var childConnection = new SqlConnection(childBuilder.ConnectionString);
    await childConnection.OpenAsync();

    if (!typedSink)
    {
        await ExecuteAsync(childConnection, $"IF OBJECT_ID('dbo.{table}') IS NOT NULL DROP TABLE dbo.[{table}];");
        await ExecuteAsync(childConnection, BenchmarkSchema.CreateTableSql(table, columns));
    }

    using var reader = representation switch
    {
        Representation.Dictionary => (RepresentationReader)new DictionaryReader(rows, columns),
        Representation.RowArray => new RowArrayReader(rows, columns),
        Representation.Columnar => new ColumnarReader(rows, columns, batch),
        _ => throw new ArgumentOutOfRangeException(nameof(representation)),
    };

    var label = $"{representation.ToString().ToLowerInvariant()} / {(typedSink ? "typed" : "bulk-copy")}";
    var produced = 0L;

    var measurement = await Measured.RunAsync(label, async () =>
    {
        if (typedSink)
        {
            // Stands in for a sink that can take typed values — a Parquet/Arrow writer, a Postgres
            // binary COPY. Nothing here asks for `object`.
            long sink = 0;
            while (reader.Read())
            {
                for (var c = 0; c < columns; c++)
                    sink += BenchmarkSchema.KindOf(c) switch
                    {
                        ColumnKind.Int => reader.GetInt32(c) >= 0 ? 1 : 0,
                        ColumnKind.Money => reader.GetDecimal(c) >= 0 ? 1 : 0,
                        ColumnKind.Timestamp => reader.GetDateTime(c) > DateTime.MinValue ? 1 : 0,
                        _ => reader.GetString(c).Length,
                    };
                produced++;
            }

            if (sink < 0)
                throw new InvalidOperationException("unreachable, but keeps the loop from being elided");
        }
        else
        {
            using var bulkCopy = new SqlBulkCopy(childConnection)
            {
                DestinationTableName = $"dbo.[{table}]",
                BatchSize = batch,
                BulkCopyTimeout = 600,
            };
            for (var c = 0; c < columns; c++)
                bulkCopy.ColumnMappings.Add(BenchmarkSchema.NameOf(c), BenchmarkSchema.NameOf(c));
            await bulkCopy.WriteToServerAsync(reader);
            produced = rows;
        }
    });

    if (produced != rows)
        throw new InvalidOperationException($"{label}: produced {produced} rows, expected {rows}");

    // Proving the data actually landed matters: a variant that silently wrote nothing would
    // otherwise look like the fastest one.
    if (!typedSink)
    {
        await using var verify = new SqlConnection(childBuilder.ConnectionString);
        await verify.OpenAsync();
        await using var count = verify.CreateCommand();
        count.CommandText = $"SELECT COUNT(*) FROM dbo.[{table}];";
        var written = (int)(await count.ExecuteScalarAsync())!;
        if (written != rows)
            throw new InvalidOperationException($"{label}: wrote {written} rows, expected {rows}");
    }

    Console.WriteLine(measurement.ToWire());
    return 0;
}

// ---- orchestrator: one child process per variant ------------------------------------------------
var scratchDatabase = $"DataSyncBenchmark_{Guid.NewGuid():N}";
var masterBuilder = new SqlConnectionStringBuilder(server) { InitialCatalog = "master" };

Console.WriteLine($"{rows:N0} rows x {columns} columns = {(long)rows * columns:N0} cells, batch {batch:N0}");
Console.WriteLine($"scratch database: {scratchDatabase}");
Console.WriteLine();

await using (var master = new SqlConnection(masterBuilder.ConnectionString))
{
    await master.OpenAsync();
    await ExecuteAsync(master, $"CREATE DATABASE [{scratchDatabase}];");
}

try
{
    Console.WriteLine(Measurement.Header);
    Console.WriteLine(new string('-', Measurement.Header.Length));

    foreach (var sink in new[] { "bulk-copy", "typed" })
    {
        foreach (var representation in Enum.GetValues<Representation>())
            Console.WriteLine(await RunChildAsync(representation, sink, scratchDatabase));
        Console.WriteLine();
    }
}
finally
{
    await using var master = new SqlConnection(masterBuilder.ConnectionString);
    await master.OpenAsync();
    await ExecuteAsync(master,
        $"ALTER DATABASE [{scratchDatabase}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{scratchDatabase}];");
}

return 0;

async Task<Measurement> RunChildAsync(Representation representation, string sink, string database)
{
    // `dotnet exec` on our own assembly, not `dotnet run`: a separate process per variant is the
    // point (peak working set is per-process), and a run wrapper would add its own.
    var startInfo = new ProcessStartInfo
    {
        FileName = "dotnet",
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    startInfo.ArgumentList.Add("exec");
    startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "DataSync.Benchmarks.dll"));
    foreach (var arg in new[]
             {
                 "--variant", representation.ToString(), "--sink", sink == "typed" ? "typed" : "bulk",
                 "--database", database, "--rows", rows.ToString(), "--columns", columns.ToString(),
                 "--batch", batch.ToString(), "--server", server,
             })
        startInfo.ArgumentList.Add(arg);

    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Failed to start the benchmark child process.");
    var stdout = await process.StandardOutput.ReadToEndAsync();
    var stderr = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();

    if (process.ExitCode != 0)
        throw new InvalidOperationException($"{representation}/{sink} failed:\n{stderr}");

    return Measurement.FromWire(stdout.Trim());
}

static async Task ExecuteAsync(SqlConnection connection, string sql)
{
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.CommandTimeout = 300;
    await command.ExecuteNonQueryAsync();
}
