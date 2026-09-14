using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using Microsoft.Data.SqlClient;
using Xunit;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.MsSql.Tests;

/// <summary>
/// The same phase-132 staging behaviour as <see cref="MsSqlStagingTableProviderChangeOrderingTests"/>,
/// against <see cref="BatchInsertStagingProvider"/> instead — the engine-neutral real-table path, run
/// here against SQL Server the same way <see cref="GenericPipelineTests"/> proves the rest of that
/// pipeline.
/// </summary>
[Trait("Category", "Integration")]
public sealed class BatchInsertStagingProviderChangeOrderingTests(MsSqlTestDatabase db)
    : IClassFixture<MsSqlTestDatabase>, IAsyncLifetime
{
    private readonly BatchInsertStagingProvider _staging = new(MsSqlDialect.Instance, MsSqlCatalog.Instance);
    private SqlConnection _connection = null!;
    private string _targetTable = null!;

    private static readonly List<ColumnMapping> Mappings =
    [
        new() { SourceColumn = "Id", TargetColumn = "Id" },
        new() { SourceColumn = "Name", TargetColumn = "Name" },
    ];

    public async Task InitializeAsync()
    {
        _connection = db.OpenConnection();
        _targetTable = $"BatchStageOrderingTgt_{Guid.NewGuid():N}";
        await ExecuteAsync($"CREATE TABLE dbo.[{_targetTable}] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);");
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

    private TableRef Target() => new()
    {
        ConnectionName = "tgt", Database = db.DatabaseName, Schema = "dbo", Table = _targetTable,
    };

    private static List<CachedColumn> TargetColumns() =>
    [
        new("Id", "int", false, true, false),
        new("Name", "nvarchar(50)", false, false, false),
    ];

    private static async IAsyncEnumerable<ChangeRow> RowsWithOrdering()
    {
        var schema = new ChangeSchema(["Id", "Name", ChangeOrdering.OrderingColumn, ChangeOrdering.ChangedAtColumn]);
        await Task.Yield();
        yield return new ChangeRow(
            ChangeOperation.Insert, schema,
            [1, "Alice", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", new DateTime(2026, 1, 1, 3, 4, 5)]);
        yield return new ChangeRow(
            ChangeOperation.Insert, schema,
            [2, "Bob", "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB", new DateTime(2026, 1, 2, 6, 7, 8)]);
    }

    private static async IAsyncEnumerable<ChangeRow> RowsWithoutOrdering()
    {
        var schema = new ChangeSchema(["Id", "Name"]);
        await Task.Yield();
        yield return new ChangeRow(ChangeOperation.Insert, schema, [1, "Alice"]);
    }

    [Fact]
    public async Task ASchemaWithBothColumns_StagesThemAndReportsHasChangeOrderingTrue()
    {
        var staged = await _staging.StageAsync(
            _connection, Target(), RowsWithOrdering(), Mappings, "mapping", TargetColumns(),
            new Dictionary<string, string>(), CancellationToken.None);
        try
        {
            Assert.True(staged.HasChangeOrdering);
            Assert.Equal(2, staged.RowCount);

            await using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                $"SELECT [Id], [__DS_ChangeOrdering], [__DS_ChangedAtUtc] FROM {staged.StagingLocation} ORDER BY [Id];";
            await using var reader = await cmd.ExecuteReaderAsync();

            Assert.True(await reader.ReadAsync());
            Assert.Equal(1, reader.GetInt32(0));
            Assert.Equal("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", reader.GetString(1));
            Assert.Equal(new DateTime(2026, 1, 1, 3, 4, 5), reader.GetDateTime(2));

            Assert.True(await reader.ReadAsync());
            Assert.Equal(2, reader.GetInt32(0));
            Assert.False(await reader.ReadAsync());
        }
        finally
        {
            await _staging.CleanupAsync(_connection, staged, CancellationToken.None);
        }
    }

    /// <summary>Every pairing but CDC stages exactly as it did before this phase — proved by asking the
    /// staged table for a column that must not exist rather than merely checking a flag.</summary>
    [Fact]
    public async Task ASchemaWithoutTheColumns_StagesExactlyAsBefore_WithHasChangeOrderingFalse()
    {
        var staged = await _staging.StageAsync(
            _connection, Target(), RowsWithoutOrdering(), Mappings, "mapping", TargetColumns(),
            new Dictionary<string, string>(), CancellationToken.None);
        try
        {
            Assert.False(staged.HasChangeOrdering);
            Assert.Equal(1, staged.RowCount);

            var ex = await Assert.ThrowsAsync<SqlException>(async () =>
            {
                await using var cmd = _connection.CreateCommand();
                cmd.CommandText = $"SELECT [__DS_ChangeOrdering] FROM {staged.StagingLocation};";
                await cmd.ExecuteScalarAsync();
            });
            Assert.Contains("Invalid column name", ex.Message);
        }
        finally
        {
            await _staging.CleanupAsync(_connection, staged, CancellationToken.None);
        }
    }
}
