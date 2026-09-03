using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataSync.Api.Services;
using DbDataSync.Core.Config;

namespace DbDataSync.Api.Tests;

/// <summary>
/// The button an operator presses while writing a DuckDB source query.
/// <para>
/// **Not tagged Integration.** Every other end-to-end test here needs a SQL Server somebody started;
/// DuckDB is embedded, so a connection whose address is <c>:memory:</c> reaches a real engine with no
/// setup at all. Which means the thing this phase's UI depends on — that a preview runs SQL and comes
/// back with real columns and rows — is asserted against a real engine in the ordinary suite rather
/// than against a fake.
/// </para>
/// </summary>
public sealed class DuckDbQueryPreviewTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _client = factory.CreateClient();

    private async Task<string> ConnectionAsync()
    {
        var name = $"duck-{Guid.NewGuid():N}";
        var response = await _client.PutAsJsonAsync($"/api/connections/{name}", new ConnectionInput
        {
            Name = name,
            DriverType = ConnectionDriverType.DuckDb,
            // The whole connection: no host, no port, no credential. An embedded engine has none of
            // them, which is why this phase added no ConnectionConfig fields to describe one.
            ConnectionString = ":memory:",
            AuthMode = AuthMode.None,
        }, JsonOptions);

        response.EnsureSuccessStatusCode();
        return name;
    }

    private async Task<QueryPreviewResult> PreviewAsync(string connectionName, string query, int? sampleRows = null)
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/connections/{connectionName}/query-preview",
            new { query, sampleRows = sampleRows ?? 20 },
            JsonOptions);

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<QueryPreviewResult>(JsonOptions))!;
    }

    [Fact]
    public async Task ReturnsTheQuerysOwnColumnsAndRows()
    {
        var connection = await ConnectionAsync();

        var result = await PreviewAsync(connection,
            "SELECT * FROM (VALUES (1, 'Ada'), (2, 'Grace')) AS t(Id, Name)");

        Assert.Null(result.Error);
        Assert.Equal(["Id", "Name"], result.Columns);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(["1", "Ada"], result.Rows[0]);
        Assert.False(result.Truncated);
    }

    /// <summary>
    /// The property the phase turns on: what runs is the text in the editor, not the mapping's last
    /// saved config. Asserted by previewing twice, against a connection that has no saved mapping at
    /// all — a preview that read config could not answer either call.
    /// </summary>
    [Fact]
    public async Task RunsTheTextItIsGiven_NotAnythingThatWasSaved()
    {
        var connection = await ConnectionAsync();

        var first = await PreviewAsync(connection, "SELECT 'draft' AS Stage");
        var second = await PreviewAsync(connection, "SELECT 'edited-since' AS Stage, 2 AS Version");

        Assert.Equal(["Stage"], first.Columns);
        Assert.Equal(["draft"], first.Rows[0]);

        Assert.Equal(["Stage", "Version"], second.Columns);
        Assert.Equal(["edited-since", "2"], second.Rows[0]);
    }

    /// <summary>
    /// A limit an operator can see beats one they cannot — and the cap is applied by stopping the
    /// read rather than by wrapping their statement, so a query with its own <c>ORDER BY</c> still
    /// returns the rows it said it would, in the order it said.
    /// </summary>
    [Fact]
    public async Task CapsTheRowsAndSaysThatItDid()
    {
        var connection = await ConnectionAsync();

        var result = await PreviewAsync(connection,
            "SELECT i AS Id FROM range(100) AS t(i) ORDER BY i", sampleRows: 3);

        Assert.Equal(3, result.Rows.Count);
        Assert.True(result.Truncated);
        Assert.Equal(["0", "1", "2"], result.Rows.Select(r => r[0]));
    }

    [Fact]
    public async Task AQueryReturningNothing_IsAnEmptyGridRatherThanAnError()
    {
        var connection = await ConnectionAsync();

        var result = await PreviewAsync(connection, "SELECT 1 AS Id WHERE false");

        Assert.Null(result.Error);
        Assert.Equal(["Id"], result.Columns);
        Assert.Empty(result.Rows);
        Assert.False(result.Truncated);
    }

    /// <summary>
    /// Somebody writing SQL gets it wrong several times on the way to right. Each of those is an
    /// answer this endpoint exists to give, not a fault of the server's — so it is a 200 carrying the
    /// engine's own message rather than a 500 saying the console is broken.
    /// </summary>
    [Fact]
    public async Task AQueryTheEngineRejects_ComesBackAsAnErrorNotAFault()
    {
        var connection = await ConnectionAsync();

        var result = await PreviewAsync(connection, "SELECT * FRM nowhere");

        Assert.NotNull(result.Error);
        Assert.Empty(result.Columns);
        Assert.Empty(result.Rows);
    }

    /// <summary>Which system it touched, in the operator's words — the same safety property a script
    /// test's Source carries, and for the same reason.</summary>
    [Fact]
    public async Task SaysWhichConnectionItRanAgainst()
    {
        var connection = await ConnectionAsync();

        var result = await PreviewAsync(connection, "SELECT 1 AS Id");

        Assert.Equal($"live query against '{connection}'", result.Source);
    }

    [Fact]
    public async Task AnEmptyQuery_SaysSoRatherThanAskingTheEngine()
    {
        var connection = await ConnectionAsync();

        var result = await PreviewAsync(connection, "   ");

        Assert.Equal("There is no query to run.", result.Error);
    }

    [Fact]
    public async Task AConnectionThatDoesNotExist_Is404()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/connections/no-such-connection/query-preview", new { query = "SELECT 1" }, JsonOptions);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Nulls stay nulls on the wire. A grid has to be able to show an empty string and a NULL
    /// differently, which the script test's one-line <c>Format</c> — where <c>NULL</c> is the word and
    /// strings are quoted — deliberately does not do.
    /// </summary>
    [Fact]
    public async Task ANullCellIsNull_NotTheWordNull()
    {
        var connection = await ConnectionAsync();

        var result = await PreviewAsync(connection, "SELECT NULL AS Missing, '' AS Empty");

        Assert.Null(result.Rows[0][0]);
        Assert.Equal("", result.Rows[0][1]);
    }

    /// <summary>The reader picker offers exactly one Kind for this engine, and it is the query
    /// reader — which is what makes the source tab's swap to an editor decidable in the SPA.</summary>
    [Fact]
    public async Task TheDriverOffersTheQueryReader()
    {
        var connection = await ConnectionAsync();

        var response = await _client.GetAsync($"/api/connections/{connection}/capabilities");
        response.EnsureSuccessStatusCode();
        var capabilities = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);

        var readers = capabilities.GetProperty("readers").EnumerateArray().ToList();
        var reader = Assert.Single(readers);
        Assert.Equal("DuckDbQuery", reader.GetProperty("kind").GetString());

        var parameter = Assert.Single(reader.GetProperty("parameters").EnumerateArray());
        Assert.Equal("query", parameter.GetProperty("name").GetString());
        Assert.Equal("Sql", parameter.GetProperty("type").GetString());
    }
}
