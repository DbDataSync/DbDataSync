using DbDataSync.Core.Config;
using Xunit;

namespace DbDataSync.Drivers.Postgres.Tests;

/// <summary>
/// <c>EstimateRowCountAsync</c> reads <c>pg_class.reltuples</c> — the planner's own estimate, set by
/// ANALYZE/VACUUM, never a scan. A table that has never been analysed reports <c>-1</c>, which comes
/// back as null rather than as a count.
/// </summary>
public sealed class PostgresRowEstimateTests(PostgresTestDatabase db) : IClassFixture<PostgresTestDatabase>, IAsyncLifetime
{
    private readonly PostgresDriver _driver = new();
    private Npgsql.NpgsqlConnection _connection = null!;
    private string _table = null!;

    public async Task InitializeAsync()
    {
        _connection = db.OpenConnection();
        _table = $"rowestimate_{Guid.NewGuid():N}";

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            CREATE TABLE public."{_table}" (id int primary key, name text);
            INSERT INTO public."{_table}" (id, name)
            SELECT g, 'n' || g FROM generate_series(1, 500) g;
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    private TableRef Table(string? name = null) => new()
    {
        ConnectionName = "c", Database = db.DatabaseName, Schema = "public", Table = name ?? _table,
    };

    [Fact]
    public async Task AFreshTable_HasNoEstimateUntilItIsAnalysed_ThenReportsOne()
    {
        Assert.Null(await _driver.EstimateRowCountAsync(_connection, Table(), CancellationToken.None));

        await using (var analyze = _connection.CreateCommand())
        {
            analyze.CommandText = $"ANALYZE public.\"{_table}\";";
            await analyze.ExecuteNonQueryAsync();
        }

        var estimate = await _driver.EstimateRowCountAsync(_connection, Table(), CancellationToken.None);
        Assert.NotNull(estimate);
        Assert.InRange(estimate!.Value, 400, 600);
    }

    [Fact]
    public async Task AMissingTable_IsNull()
    {
        Assert.Null(await _driver.EstimateRowCountAsync(
            _connection, Table($"nope_{Guid.NewGuid():N}"), CancellationToken.None));
    }
}
