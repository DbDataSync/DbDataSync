using DbDataSync.Core.Sql;
using Npgsql;

namespace DbDataSync.Drivers.Generic.Tests;

/// <summary>
/// <see cref="QueryCatalog"/> — the <c>driver.yaml</c> <c>catalog: query</c> escape hatch — against a
/// real table, not a mock: column-name matching (not position), the documented default for each
/// optional field when a query omits it, and the required-column failure. Uses Postgres only as a
/// convenient real database to run arbitrary SQL against; nothing here is Postgres-specific — the whole
/// point of this class is that the query can be anything.
/// </summary>
[Trait("Category", "Integration")]
public sealed class QueryCatalogTests : IAsyncLifetime
{
    private static string ServerConnectionString =>
        Environment.GetEnvironmentVariable("DBDATASYNC_TEST_POSTGRES_SERVER")
        ?? "Host=localhost;Port=15432;Username=dbdatasync;Password=DbDataSync_Test_Pw1;Database=postgres";

    private readonly string _tableName = $"query_catalog_spike_{Guid.NewGuid():N}";
    private NpgsqlConnection _connection = null!;

    public async Task InitializeAsync()
    {
        _connection = new NpgsqlConnection(ServerConnectionString);
        await _connection.OpenAsync();
        await ExecuteAsync($"""
            CREATE TABLE public."{_tableName}" (
                id integer primary key,
                name varchar(50) not null,
                amount numeric(10,2));
            """);
    }

    public async Task DisposeAsync()
    {
        await ExecuteAsync($"DROP TABLE IF EXISTS public.\"{_tableName}\";");
        await _connection.DisposeAsync();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task ListTablesAsync_MatchesColumnsByNameNotPosition()
    {
        // table_name before table_schema — the reverse of the "natural" order — proves this is a
        // by-name lookup, not positional.
        var catalog = new QueryCatalog(
            tableQuery: "SELECT table_name, table_schema FROM information_schema.tables WHERE table_type = 'BASE TABLE'",
            columnQuery: "SELECT column_name, data_type FROM information_schema.columns WHERE table_schema = {{schema}} AND table_name = {{table}}");

        var tables = await catalog.ListTablesAsync(_connection, CancellationToken.None);

        Assert.Contains(tables, t => t.Schema == "public" && t.Table == _tableName);
    }

    [Fact]
    public async Task GetColumnsAsync_OmittedOptionalColumns_GetTheirDocumentedDefaults()
    {
        // Only the two required columns — every optional one (character_maximum_length,
        // numeric_precision, numeric_scale, is_nullable, is_primary_key, is_identity) is absent from
        // the SELECT entirely, not just null-valued.
        var catalog = new QueryCatalog(
            tableQuery: "SELECT table_name, table_schema FROM information_schema.tables",
            columnQuery: "SELECT column_name, data_type FROM information_schema.columns " +
                         "WHERE table_schema = {{schema}} AND table_name = {{table}} ORDER BY ordinal_position");

        var columns = await catalog.GetColumnsAsync(_connection, "public", _tableName, CancellationToken.None);

        var id = columns.Single(c => c.Name == "id");
        // IsNullable defaults toward nullable (true) when the query doesn't say — the safe direction.
        Assert.True(id.IsNullable);
        Assert.False(id.IsPrimaryKey);
        Assert.False(id.IsIdentity);
        // No length/precision/scale columns supplied, so FormatType gets none of them — the bare type name.
        Assert.Equal("integer", id.NativeType);
    }

    [Fact]
    public async Task GetColumnsAsync_SuppliedOptionalColumns_AreUsedInstead()
    {
        var catalog = new QueryCatalog(
            tableQuery: "SELECT table_name, table_schema FROM information_schema.tables",
            columnQuery: "SELECT column_name, data_type, character_maximum_length, " +
                         "CASE WHEN column_name = 'id' THEN 'NO' ELSE 'YES' END AS is_nullable, " +
                         "(column_name = 'id') AS is_primary_key " +
                         "FROM information_schema.columns WHERE table_schema = {{schema}} AND table_name = {{table}}");

        var columns = await catalog.GetColumnsAsync(_connection, "public", _tableName, CancellationToken.None);

        var id = columns.Single(c => c.Name == "id");
        Assert.False(id.IsNullable);
        Assert.True(id.IsPrimaryKey);

        var name = columns.Single(c => c.Name == "name");
        // information_schema.columns.data_type reports the SQL-standard spelling ("character varying"),
        // not Postgres's short "varchar" — the same thing InformationSchemaQueries itself would report,
        // reading the identical column.
        Assert.Equal("character varying(50)", name.NativeType);
    }

    [Fact]
    public async Task GetColumnsAsync_MissingRequiredColumn_FailsClearlyNamingIt()
    {
        // data_type is required and simply isn't in the SELECT list.
        var catalog = new QueryCatalog(
            tableQuery: "SELECT table_name, table_schema FROM information_schema.tables",
            columnQuery: "SELECT column_name FROM information_schema.columns " +
                         "WHERE table_schema = {{schema}} AND table_name = {{table}}");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.GetColumnsAsync(_connection, "public", _tableName, CancellationToken.None));

        Assert.Contains("columnQuery", ex.Message);
        Assert.Contains("data_type", ex.Message);
    }
}
