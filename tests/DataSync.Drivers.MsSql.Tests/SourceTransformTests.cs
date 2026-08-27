using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;
using DataSync.Drivers.Generic;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DataSync.Drivers.MsSql.Tests;

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
            _connection, Source(), null, Transformed, new Dictionary<string, string>(), CancellationToken.None);

        AssertTransformed(Assert.Single(await CollectAsync(read.Rows)));
    }

    [Fact]
    public async Task WatermarkReader_AppliesTheTransform()
    {
        await SeedAsync();
        var reader = new WatermarkReader(MsSqlDialect.Instance, MsSqlCatalog.Instance, MsSqlValueBinding.Instance);
        var options = new Dictionary<string, string> { ["watermarkColumn"] = "ModifiedAt" };

        var read = await reader.ReadChangesAsync(_connection, Source(), null, Transformed, options, CancellationToken.None);

        AssertTransformed(Assert.Single(await CollectAsync(read.Rows)));
    }

    [Fact]
    public async Task GenericBatchReloadReader_AppliesTheTransform()
    {
        await SeedAsync();
        var reader = new BatchReloadReader(MsSqlDialect.Instance, MsSqlCatalog.Instance, MsSqlValueBinding.Instance);

        var read = await reader.ReadChangesAsync(
            _connection, Source(), null, Transformed, new Dictionary<string, string>(), CancellationToken.None);

        AssertTransformed(Assert.Single(await CollectAsync(read.Rows)));
    }

    [Fact]
    public async Task ChangeTrackingReader_AppliesTheTransformOnItsFullLoadPath()
    {
        await SeedAsync();
        var reader = new MsSqlChangeTrackingReader();

        var read = await reader.ReadChangesAsync(
            _connection, Source(), null, Transformed, new Dictionary<string, string>(), CancellationToken.None);

        AssertTransformed(Assert.Single(await CollectAsync(read.Rows)));
    }

    [Fact]
    public async Task ChangeTrackingReader_AppliesTheTransformOnItsIncrementalPath()
    {
        // The one the token exists for. The incremental statement joins the table as `base`, so an
        // unqualified column reference would be ambiguous against CHANGETABLE's own copy of the key.
        var reader = new MsSqlChangeTrackingReader();
        var baseline = await reader.ReadChangesAsync(
            _connection, Source(), null, Transformed, new Dictionary<string, string>(), CancellationToken.None);
        await CollectAsync(baseline.Rows);

        await SeedAsync();

        var read = await reader.ReadChangesAsync(
            _connection, Source(), baseline.NewWatermark, Transformed, new Dictionary<string, string>(), CancellationToken.None);

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
            _connection, Source(), null, Transformed, new Dictionary<string, string>(), CancellationToken.None);
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
            _connection, Source(), null, [], new Dictionary<string, string>(), CancellationToken.None);
        var row = Assert.Single(await CollectAsync(read.Rows));

        Assert.Contains("Ignored", row.Schema.ColumnNames);
    }
}
