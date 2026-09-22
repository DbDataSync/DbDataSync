using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using Microsoft.Data.SqlClient;
using Xunit;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.MsSql.Tests;

/// <summary>
/// A <see cref="ColumnMapping.Transform"/> is a SQL expression in the *source* dialect, evaluated by
/// the source engine. These prove it reaches every reader that builds a statement — including the
/// Change Tracking reader, whose aliased join is the reason the <c>{{column}}</c> token exists.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SourceTransformTests(MsSqlTestDatabase db) : IClassFixture<MsSqlTestDatabase>, IAsyncLifetime
{
    private SqlConnection _connection = null!;
    private string _table = null!;

    private static readonly List<ColumnMapping> Transformed =
    [
        new() { SourceColumn = "Id", TargetColumn = "Id" },
        new() { SourceColumn = "Region", TargetColumn = "Region", Transform = "UPPER({{column}})" },
        new() { SourceColumn = "Amount", TargetColumn = "Amount", Transform = "{{column}} * 2" },
    ];

    public async Task InitializeAsync()
    {
        _connection = db.OpenConnection();
        _table = $"Xf_{Guid.NewGuid():N}";

        await ExecuteAsync($"""
            CREATE TABLE dbo.[{_table}] (
                Id INT NOT NULL PRIMARY KEY,
                Region NVARCHAR(20) NOT NULL,
                Amount DECIMAL(18,2) NOT NULL,
                Ignored NVARCHAR(20) NULL,
                ModifiedAt DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME());
            """);
        await ExecuteAsync($"ALTER TABLE dbo.[{_table}] ENABLE CHANGE_TRACKING;");
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private SourceTableRef Source() =>
        new() { ConnectionName = "src", Database = db.DatabaseName, Schema = "dbo", Table = _table };

    private const string MappingName = "source-transform";

    /// <summary>Matches InitializeAsync's own CREATE TABLE — WatermarkReader and the generic
    /// BatchReloadReader resolve column shape from here as of phase 91.</summary>
    private static List<CachedColumn> Columns() =>
    [
        new("Id", "int", false, true, false),
        new("Region", "nvarchar(20)", false, false, false),
        new("Amount", "decimal(18,2)", false, false, false),
        new("Ignored", "nvarchar(20)", true, false, false),
        new("ModifiedAt", "datetime2(3)", false, false, false),
    ];

    private static async Task<List<ChangeRow>> CollectAsync(IAsyncEnumerable<ChangeRow> rows)
    {
        var list = new List<ChangeRow>();
        await foreach (var row in rows)
            list.Add(row);
        return list;
    }

    private async Task SeedAsync() =>
        await ExecuteAsync($"INSERT INTO dbo.[{_table}] (Id, Region, Amount, Ignored) VALUES (1, 'eu', 10.00, 'x');");

    private static void AssertTransformed(ChangeRow row)
    {
        Assert.Equal("EU", (string)row["Region"]!);
        Assert.Equal(20.00m, (decimal)row["Amount"]!);
    }

    [Fact]
    public async Task BatchReloadReader_AppliesTheTransform()
    {
        await SeedAsync();
        var reader = new MsSqlBatchReloadReader();

        var read = await reader.ReadChangesAsync(
            _connection, Source(), null, ReadIntent.InitialLoad, Transformed, "mapping", [], new Dictionary<string, string>(), CancellationToken.None);

        AssertTransformed(Assert.Single(await CollectAsync(read.Rows)));
    }

    [Fact]
    public async Task WatermarkReader_AppliesTheTransform()
    {
        await SeedAsync();
        var reader = new WatermarkReader(MsSqlDialect.Instance, MsSqlValueBinding.Instance);
        var options = new Dictionary<string, string> { ["watermarkColumn"] = "ModifiedAt" };

        var read = await reader.ReadChangesAsync(
            _connection, Source(), null, ReadIntent.InitialLoad, Transformed, MappingName, Columns(), options, CancellationToken.None);

        AssertTransformed(Assert.Single(await CollectAsync(read.Rows)));
    }

    [Fact]
    public async Task GenericBatchReloadReader_AppliesTheTransform()
    {
        await SeedAsync();
        var reader = new BatchReloadReader(MsSqlDialect.Instance, MsSqlValueBinding.Instance);

        var read = await reader.ReadChangesAsync(
            _connection, Source(), null, ReadIntent.InitialLoad, Transformed, MappingName, Columns(), new Dictionary<string, string>(), CancellationToken.None);

        AssertTransformed(Assert.Single(await CollectAsync(read.Rows)));
    }

    // ChangeTrackingReader_AppliesTheTransformOnItsFullLoadPath removed (phase 134): this reader no
    // longer has a full-load path — RunExecutor routes InitialLoad to the Bulk Load pipeline instead,
    // ahead of ever calling ReadChangesAsync. BatchReloadReader_AppliesTheTransform above covers the
    // transform on the reader that actually performs an initial/full load now.

    [Fact]
    public async Task ChangeTrackingReader_AppliesTheTransformOnItsIncrementalPath()
    {
        // The one the token exists for. The incremental statement joins the table as `base`, so an
        // unqualified column reference would be ambiguous against CHANGETABLE's own copy of the key.
        // The baseline position is captured directly (phase 134: this reader no longer full-loads), not
        // read — nothing here needs its rows, only the version to diff the seeded insert against.
        var reader = new MsSqlChangeTrackingReader();
        var captured = await reader.CapturePositionAsync(
            _connection, Source(), new Dictionary<string, string>(), CancellationToken.None);

        await SeedAsync();

        var read = await reader.ReadChangesAsync(
            _connection, Source(), captured.Position, ReadIntent.Changes, Transformed, "mapping", [], new Dictionary<string, string>(), CancellationToken.None);

        AssertTransformed(Assert.Single(await CollectAsync(read.Rows)));
    }

    [Fact]
    public async Task AMappingCoveringSomeColumns_StopsAskingTheSourceForTheRest()
    {
        // The side effect worth pinning: losing it later would be silent, and it is the difference
        // between reading 3 columns and reading 40.
        await SeedAsync();
        var reader = new MsSqlBatchReloadReader();

        var read = await reader.ReadChangesAsync(
            _connection, Source(), null, ReadIntent.InitialLoad, Transformed, "mapping", [], new Dictionary<string, string>(), CancellationToken.None);
        var row = Assert.Single(await CollectAsync(read.Rows));

        Assert.Equal(["Id", "Region", "Amount"], row.Schema.ColumnNames);
        Assert.DoesNotContain("Ignored", row.Schema.ColumnNames);
    }

    [Fact]
    public async Task NoMappings_StillReadsTheWholeRow()
    {
        await SeedAsync();
        var reader = new MsSqlBatchReloadReader();

        var read = await reader.ReadChangesAsync(
            _connection, Source(), null, ReadIntent.InitialLoad, [], "mapping", [], new Dictionary<string, string>(), CancellationToken.None);
        var row = Assert.Single(await CollectAsync(read.Rows));

        Assert.Contains("Ignored", row.Schema.ColumnNames);
    }
}
