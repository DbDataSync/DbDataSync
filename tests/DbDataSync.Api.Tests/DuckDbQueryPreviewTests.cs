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
            DriverType = DriverIds.DuckDb,
            // The whole connection: no host, no port, no credential. An embedded engine has none of
            // them, which is why this phase added no ConnectionConfig fields to describe one.
            ConnectionString = ":memory:",
            AuthMode = AuthMode.None,
        }, JsonOptions);

        response.EnsureSuccessStatusCode();
        return name;
    }

    private async Task<QueryPreviewResult> PreviewAsync(
        string connectionName, string query, int? maxRows = null, bool? allowSubquery = null)
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/connections/{connectionName}/query-preview",
            new { query, maxRows = maxRows ?? 10, allowSubquery = allowSubquery ?? true },
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
    /// A limit an operator can see beats one they cannot. With subqueries disallowed, the cap is
    /// applied purely by stopping the read rather than by wrapping their statement, so a query with its
    /// own <c>ORDER BY</c> still returns the rows it said it would, in the order it said.
    /// </summary>
    [Fact]
    public async Task CapsTheRowsAndSaysThatItDid_WithSubqueriesDisallowed()
    {
        var connection = await ConnectionAsync();

        var result = await PreviewAsync(connection,
            "SELECT i AS Id FROM range(100) AS t(i) ORDER BY i", maxRows: 3, allowSubquery: false);

        Assert.Equal(3, result.Rows.Count);
        Assert.True(result.Truncated);
        Assert.Equal(["0", "1", "2"], result.Rows.Select(r => r[0]));
    }

    /// <summary>
    /// The default (phase 193S): the cap becomes a real <c>LIMIT</c> on a wrapped statement rather than
    /// relying only on the reader-side stop — worth doing so a large source query is never asked to
    /// produce more than the preview needs. Behaviorally indistinguishable from the unwrapped case for
    /// a well-behaved query; the next test is what actually proves wrapping happened.
    /// </summary>
    [Fact]
    public async Task CapsTheRowsAndSaysThatItDid_WithSubqueriesAllowed()
    {
        var connection = await ConnectionAsync();

        var result = await PreviewAsync(connection,
            "SELECT i AS Id FROM range(100) AS t(i) ORDER BY i", maxRows: 3, allowSubquery: true);

        Assert.Equal(3, result.Rows.Count);
        Assert.True(result.Truncated);
        Assert.Equal(["0", "1", "2"], result.Rows.Select(r => r[0]));
    }

    /// <summary>
    /// Proves the two modes are genuinely different code paths, not just different flags on the same
    /// one: a query a subquery can't hold (here, one ending in a semicolon — legal on its own, a syntax
    /// error the moment it's embedded inside <c>(...)  AS base</c>) works with subqueries disallowed and
    /// fails with them allowed. The same reasoning the whole feature rests on — a query that can't be
    /// wrapped just means the features needing a wrap don't work for it, not that nothing does.
    /// </summary>
    [Fact]
    public async Task AQueryThatCannotBeWrapped_WorksUnwrapped_AndFailsWrapped()
    {
        var connection = await ConnectionAsync();

        var unwrapped = await PreviewAsync(connection, "SELECT 1 AS Id;", allowSubquery: false);
        Assert.Null(unwrapped.Error);
        Assert.Equal(["1"], unwrapped.Rows[0]);

        var wrapped = await PreviewAsync(connection, "SELECT 1 AS Id;", allowSubquery: true);
        Assert.NotNull(wrapped.Error);
    }

    /// <summary>
    /// 0 means "shape only" — this is what serves a query-shaped source's metadata capture through the
    /// same endpoint, with no separate describe mechanism.
    /// </summary>
    [Fact]
    public async Task MaxRowsZero_ReturnsTheShapeWithNoRows()
    {
        var connection = await ConnectionAsync();

        var result = await PreviewAsync(connection, "SELECT 1 AS Id, 'x' AS Name", maxRows: 0);

        Assert.Null(result.Error);
        Assert.Equal(["Id", "Name"], result.Columns);
        Assert.Empty(result.Rows);
        Assert.False(result.Truncated);
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

    /// <summary>
    /// The reader picker offers exactly one Kind for this engine — the generic reload reader (phase
    /// 193S; DuckDB no longer has one of its own). It declares no parameters at all: unlike the retired
    /// <c>DuckDbQueryReader</c>, the query itself lives on <c>SourceTableSpec.Query</c>, not a reader
    /// option, so there is nothing here for the mapping editor to read a parameter list for.
    /// </summary>
    [Fact]
    public async Task TheDriverOffersAReloadReader()
    {
        var connection = await ConnectionAsync();

        var response = await _client.GetAsync($"/api/connections/{connection}/capabilities");
        response.EnsureSuccessStatusCode();
        var capabilities = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);

        var readers = capabilities.GetProperty("readers").EnumerateArray().ToList();
        var reader = Assert.Single(readers);
        Assert.Equal("BatchReload", reader.GetProperty("kind").GetString());
        Assert.Empty(reader.GetProperty("parameters").EnumerateArray());
    }
}
