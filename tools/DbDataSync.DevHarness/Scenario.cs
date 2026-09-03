using Microsoft.Data.SqlClient;

namespace DbDataSync.DevHarness;

/// <summary>
/// The kinds of column a generated table is built from. Deliberately a short, closed list of
/// *representative* types rather than an attempt at coverage: enough that a wide table exercises more
/// than one arithmetic on both engines, few enough that every one of them can be seeded, updated,
/// provisioned and compared without a per-type special case scattered around the harness.
/// </summary>
public enum HarnessColumnType
{
    /// <summary>The integer primary key. Exactly one per table, always first — <c>verify</c>
    /// merge-joins on it and every other verb addresses rows by it.</summary>
    Key,

    /// <summary>A low-cardinality string. Low-cardinality on purpose: a list segment needs a column
    /// with few enough distinct values to enumerate, and this is the harness's only one.</summary>
    Text,

    Decimal,
    Int,
    Bool,
    Date,

    /// <summary>The watermark column. Exactly one per table, always last.</summary>
    Timestamp,
}

public sealed record HarnessColumn(string Name, HarnessColumnType Type);

/// <summary>
/// One generated table: its name, its columns in the order every verb reads them, and the mapping
/// name the replication knows it by.
/// </summary>
public sealed record HarnessTable(int Index, string Name, IReadOnlyList<HarnessColumn> Columns)
{
    public string QualifiedSource => $"[{Scenario.Schema}].[{Name}]";

    /// <summary>Lower-cased because a mapping name is an identifier in config and in URLs, and the
    /// table name is title-cased.</summary>
    public string MappingName => Name.ToLowerInvariant();

    public IEnumerable<string> ColumnNames => Columns.Select(c => c.Name);

    /// <summary>The first <see cref="HarnessColumnType.Text"/> column, which every table has —
    /// filler 1 is always Text. <c>drift</c> and the workload's update both need one column they can
    /// count on finding whatever the table's width.</summary>
    public string TextColumn => Columns.First(c => c.Type == HarnessColumnType.Text).Name;

    public string TimestampColumn => Columns.Last(c => c.Type == HarnessColumnType.Timestamp).Name;
}

/// <summary>
/// The dev scenario this harness stands up: <c>--tables N</c> independent tables of increasing width,
/// replicated from the source instance to the target instance.
/// <para>
/// Generated rather than hand-described. Table <i>i</i> carries <c>Id</c>, <i>i</i> filler columns
/// cycling through the representative types, and <c>UpdatedAtUtc</c> — so width grows predictably with
/// index, every type gets exercised across the set, and "table 7" names a specific, reproducible shape
/// without anyone having to write seven table definitions. There is deliberately **no** relational
/// structure between them: this is about variety and volume, not a related set.
/// </para>
/// <para>
/// The column set is still chosen so that every segment mode has something to bite on — an integer key
/// for range and auto segments, a low-cardinality string for list segments, and a decimal and a
/// timestamp so bucket boundaries get exercised on types where the arithmetic is not just integers.
/// </para>
/// </summary>
public static class Scenario
{
    public const string DatabaseName = "DbDataSyncDev";
    public const string Schema = "dbo";

    public const string SourceConnectionName = "dev-source";
    public const string TargetConnectionName = "dev-target";
    public const string ReplicationName = "dev-sync";

    public const string SourceHost = "localhost";
    public const int SourcePort = 14330;
    public const string TargetHost = "localhost";
    public const int TargetPort = 14331;

    public static readonly string[] Regions = ["EU", "US", "APAC", "LATAM"];

    /// <summary>
    /// The filler cycle, in order. Text first so that **every** table has a low-cardinality string
    /// column: table 1 has exactly one filler, and a harness where the narrowest table could not be
    /// list-segmented would have lost something the single-table scenario had.
    /// </summary>
    private static readonly HarnessColumnType[] FillerCycle =
        [HarnessColumnType.Text, HarnessColumnType.Decimal, HarnessColumnType.Int, HarnessColumnType.Bool, HarnessColumnType.Date];

    public const int DefaultTableCount = 1;

    /// <summary>
    /// How many tables this invocation is about.
    /// <para>
    /// Read the same way <see cref="TargetEngine.Resolve"/> reads its engine, and for the same reason:
    /// every verb has to agree. <c>verify</c> checking three tables that <c>up</c> never created
    /// reports a difference that is really a forgotten flag, so the environment variable exists to be
    /// set once.
    /// </para>
    /// </summary>
    public static int TableCount(HarnessArgs args)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("DBDATASYNC_HARNESS_TABLES");
        var fallback = int.TryParse(fromEnvironment, out var parsed) ? parsed : DefaultTableCount;
        var count = args.Int("tables", fallback);

        if (count < 1)
            throw new HarnessException("--tables must be at least 1.");

        return count;
    }

    public static IReadOnlyList<HarnessTable> Tables(HarnessArgs args) => Generate(TableCount(args));

    /// <summary>
    /// The table set for a given count. Pure and deterministic: the same N always produces the same
    /// names, widths and types, which is what lets `seed` and `verify` be separate invocations.
    /// </summary>
    public static IReadOnlyList<HarnessTable> Generate(int tableCount)
    {
        var tables = new List<HarnessTable>(tableCount);
        for (var i = 1; i <= tableCount; i++)
        {
            var columns = new List<HarnessColumn>(i + 2) { new("Id", HarnessColumnType.Key) };

            for (var f = 1; f <= i; f++)
            {
                var type = FillerCycle[(f - 1) % FillerCycle.Length];
                // The ordinal is in the name, so a column identifies both what it is and where it sits
                // — which is what makes a failure report about "Decimal7" readable without the schema
                // in front of you.
                columns.Add(new HarnessColumn($"{type}{f}", type));
            }

            columns.Add(new HarnessColumn("UpdatedAtUtc", HarnessColumnType.Timestamp));
            tables.Add(new HarnessTable(i, $"Table{i}", columns));
        }

        return tables;
    }

    /// <summary>SQL Server's spelling for each column type — the source is always SQL Server, so this
    /// is not behind <see cref="TargetEngine"/>.</summary>
    public static string SourceColumnType(HarnessColumnType type) => type switch
    {
        HarnessColumnType.Key => "INT NOT NULL PRIMARY KEY",
        HarnessColumnType.Text => "NVARCHAR(20) NOT NULL",
        HarnessColumnType.Decimal => "DECIMAL(18,2) NOT NULL",
        HarnessColumnType.Int => "INT NOT NULL",
        HarnessColumnType.Bool => "BIT NOT NULL",
        HarnessColumnType.Date => "DATE NOT NULL",
        HarnessColumnType.Timestamp => "DATETIME2(3) NOT NULL",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown harness column type."),
    };

    public static string CreateTableSql(HarnessTable table)
    {
        var columns = table.Columns.Select(c => $"    [{c.Name}] {SourceColumnType(c.Type)}");
        return $"CREATE TABLE {table.QualifiedSource} (\n{string.Join(",\n", columns)}\n);";
    }

    public static string SaPassword =>
        Environment.GetEnvironmentVariable("DBDATASYNC_MSSQL_SA_PASSWORD") ?? "DbDataSync_Test_Pw1";

    public static string ServerConnectionString(string host, int port, string? database = null) =>
        new SqlConnectionStringBuilder
        {
            DataSource = $"{host},{port}",
            InitialCatalog = database ?? "master",
            UserID = "sa",
            Password = SaPassword,
            TrustServerCertificate = true,
            // Short, so waiting for a container that isn't up yet fails fast enough to retry usefully.
            ConnectTimeout = 5,
        }.ConnectionString;

    public static string SourceConnectionString(string? database = null) =>
        ServerConnectionString(SourceHost, SourcePort, database);

    public static string TargetConnectionString(string? database = null) =>
        ServerConnectionString(TargetHost, TargetPort, database);

    /// <summary>
    /// A value for one column of one row, deterministic in <paramref name="random"/> so a seeded run is
    /// reproducible. One generator for seeding, the workload and drift's phantoms, so a row written by
    /// any of them looks like a row written by the others.
    /// </summary>
    public static object Value(HarnessColumn column, int id, Random random) => column.Type switch
    {
        HarnessColumnType.Key => id,
        HarnessColumnType.Text => Regions[random.Next(Regions.Length)],
        HarnessColumnType.Decimal => Math.Round((decimal)(random.NextDouble() * 5000), 2),
        HarnessColumnType.Int => random.Next(1_000_000),
        HarnessColumnType.Bool => random.Next(2) == 1,
        HarnessColumnType.Date => DateTime.UtcNow.Date.AddDays(-random.Next(365)),
        // Unspecified rather than Utc: Postgres rejects a UTC-kinded DateTime for a `timestamp` column,
        // and the harness stores wall-clock UTC in a column with no time zone either way.
        HarnessColumnType.Timestamp => DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified),
        _ => throw new ArgumentOutOfRangeException(nameof(column), column.Type, "Unknown harness column type."),
    };
}
