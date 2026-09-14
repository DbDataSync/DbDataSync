using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDataSync.Drivers.MsSql.Tests;

/// <summary>
/// Phase 132's staging half, against a real server: <see cref="MsSqlStagingTableProvider"/> inspects
/// the first staged row's schema for both <see cref="ChangeOrdering"/> columns by name — no new
/// interface member, no new option — and adds two matching nullable staging columns only when it finds
/// them. A live server is what actually proves the temp table's DDL and the round-tripped values, which
/// a pure statement-shape test cannot: this provider builds its <c>CREATE TABLE</c> from the *target's
/// live catalog*, not from a dialect-neutral builder.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MsSqlStagingTableProviderChangeOrderingTests(MsSqlTestDatabase db)
    : IClassFixture<MsSqlTestDatabase>, IAsyncLifetime
{
    private readonly MsSqlStagingTableProvider _staging = new();
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
        _targetTable = $"StageOrderingTgt_{Guid.NewGuid():N}";
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

    private static async IAsyncEnumerable<ChangeRow> RowsWithOrdering()
    {
        var schema = new ChangeSchema(["Id", "Name", ChangeOrdering.OrderingColumn, ChangeOrdering.ChangedAtColumn]);
        await Task.Yield();
        yield return new ChangeRow(
            ChangeOperation.Insert, schema, [1, "Alice", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", new DateTime(2026, 1, 1, 3, 4, 5)]);
        yield return new ChangeRow(
            ChangeOperation.Insert, schema, [2, "Bob", "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB", new DateTime(2026, 1, 2, 6, 7, 8)]);
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
            _connection, Target(), RowsWithOrdering(), Mappings, "mapping", [], new Dictionary<string, string>(),
            CancellationToken.None);

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
        Assert.Equal("BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB", reader.GetString(1));
        Assert.Equal(new DateTime(2026, 1, 2, 6, 7, 8), reader.GetDateTime(2));

        Assert.False(await reader.ReadAsync());
    }

    /// <summary>
    /// Every reader but <c>MsSqlCdcReader</c> stages exactly as it did before this phase — proved here
    /// by asking the staging table for a column that must not exist rather than merely checking a flag,
    /// since "the column is genuinely absent" is the actual "byte-for-byte unchanged" claim.
    /// </summary>
    [Fact]
    public async Task ASchemaWithoutTheColumns_StagesExactlyAsBefore_WithHasChangeOrderingFalse()
    {
        var staged = await _staging.StageAsync(
            _connection, Target(), RowsWithoutOrdering(), Mappings, "mapping", [], new Dictionary<string, string>(),
            CancellationToken.None);

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
}
