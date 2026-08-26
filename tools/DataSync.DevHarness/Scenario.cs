using Microsoft.Data.SqlClient;

namespace DataSync.DevHarness;

/// <summary>
/// The one dev scenario this harness stands up: a single <c>Orders</c> table replicated from the
/// source instance to the target instance.
/// <para>
/// The column set is chosen so that every segment mode has something to bite on — an integer key for
/// range and auto segments, a low-cardinality string for list segments, and a decimal and a timestamp
/// so bucket boundaries get exercised on types where the arithmetic is not just integers. It is also
/// what makes the watermark reader usable here without a second table.
/// </para>
/// </summary>
public static class Scenario
{
    public const string DatabaseName = "DataSyncDev";
    public const string Schema = "dbo";
    public const string Table = "Orders";

    public const string SourceConnectionName = "dev-source";
    public const string TargetConnectionName = "dev-target";
    public const string ReplicationName = "dev-sync";
    public const string MappingName = "orders";

    public const string SourceHost = "localhost";
    public const int SourcePort = 14330;
    public const string TargetHost = "localhost";
    public const int TargetPort = 14331;

    public static readonly string[] Regions = ["EU", "US", "APAC", "LATAM"];

    /// <summary>Columns in the order both sides are read for comparison. Id first: <c>verify</c>
    /// merge-joins on it.</summary>
    public static readonly string[] Columns = ["Id", "Region", "CustomerName", "Amount", "UpdatedAtUtc"];

    public static string QualifiedTable => $"[{Schema}].[{Table}]";

    public static string SaPassword =>
        Environment.GetEnvironmentVariable("DATASYNC_MSSQL_SA_PASSWORD") ?? "DataSync_Test_Pw1";

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
    /// Creates the table on either side. Deliberately identical on both: the point of the harness is
    /// to make divergence *visible*, which needs a target whose shape can't be the explanation.
    /// </summary>
    public static string CreateTableSql => $"""
        CREATE TABLE {QualifiedTable} (
            Id INT NOT NULL PRIMARY KEY,
            Region NVARCHAR(20) NOT NULL,
            CustomerName NVARCHAR(100) NOT NULL,
            Amount DECIMAL(18,2) NOT NULL,
            UpdatedAtUtc DATETIME2(3) NOT NULL
        );
        """;
}
