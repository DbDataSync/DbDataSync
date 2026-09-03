using DbDataSync.Api.Controllers;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataSync.Core.Config;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// The global script registry. The rule that matters here is phase 16's, applied to the thing where it
/// matters most: a script that will not compile is rejected while the operator is still looking at it,
/// not discovered at the first run.
/// </summary>
public sealed class ScriptsControllerTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _client = factory.CreateClient();

    private sealed record Diagnostic(int Line, int Column, string Message);
    private sealed record CompileResult(ScriptConfig Manifest, List<Diagnostic> Diagnostics, bool Compiles);

    private const string ValidCode = """
        using DbDataSync.Scripting.Abstractions;

        public sealed class Upper : ISqlColumnExpression
        {
            public string? RenderSql(SqlColumnExpressionContext c) => $"UPPER({c.ColumnReference})";
        }
        """;

    private static ScriptDefinition Definition(string name, string code, string entryType = "Upper") => new()
    {
        Manifest = new ScriptConfig
        {
            Name = name,
            Kind = "sqlColumnExpression",
            EntryType = entryType,
            Description = "Upper-cases a column at the source.",
        },
        Code = code,
    };

    [Fact]
    public async Task AValidScript_SavesAndRoundTrips()
    {
        var name = $"upper-{Guid.NewGuid():N}";

        var put = await _client.PutAsJsonAsync($"/api/scripts/{name}", Definition(name, ValidCode), JsonOptions);
        put.EnsureSuccessStatusCode();

        var loaded = await _client.GetFromJsonAsync<ScriptDefinition>($"/api/scripts/{name}", JsonOptions);

        Assert.Equal("Upper", loaded!.Manifest.EntryType);
        // The code comes back byte for byte — it lives in its own .cs file precisely so a serializer
        // never gets to re-indent it.
        Assert.Equal(ValidCode, loaded.Code);
    }

    [Fact]
    public async Task ScriptsThatDoNotCompile_AreRejectedWithLineNumbers()
    {
        var name = $"broken-{Guid.NewGuid():N}";

        var response = await _client.PutAsJsonAsync(
            $"/api/scripts/{name}",
            Definition(name, ValidCode.Replace("$\"UPPER({c.ColumnReference})\"", "nonsense")),
            JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("did not compile", await response.Content.ReadAsStringAsync());

        // And nothing was written — a broken script must not be reachable by a binding.
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/scripts/{name}")).StatusCode);
    }

    [Fact]
    public async Task AScriptReachingOutsideItsSandbox_IsRejected()
    {
        var name = $"nosy-{Guid.NewGuid():N}";

        var response = await _client.PutAsJsonAsync(
            $"/api/scripts/{name}",
            Definition(name, """
                using System.IO;
                using DbDataSync.Scripting.Abstractions;

                public sealed class Upper : ISqlColumnExpression
                {
                    public string? RenderSql(SqlColumnExpressionContext c) => File.ReadAllText("/etc/passwd");
                }
                """),
            JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("does not read files", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AnUnknownKind_IsRejectedAndSaysWhatIsKnown()
    {
        var name = $"odd-{Guid.NewGuid():N}";
        var definition = Definition(name, ValidCode);
        definition.Manifest.Kind = "notASlot";

        var response = await _client.PutAsJsonAsync($"/api/scripts/{name}", definition, JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("sqlColumnExpression", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Compile_ChecksWithoutSaving()
    {
        var name = $"trial-{Guid.NewGuid():N}";

        var response = await _client.PostAsJsonAsync($"/api/scripts/{name}/compile", Definition(name, ValidCode), JsonOptions);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<CompileResult>(JsonOptions);

        Assert.True(result!.Compiles);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/scripts/{name}")).StatusCode);
    }

    [Fact]
    public async Task Compile_ReportsDiagnosticsWithoutFailingTheRequest()
    {
        var name = $"trial-{Guid.NewGuid():N}";

        var response = await _client.PostAsJsonAsync(
            $"/api/scripts/{name}/compile",
            Definition(name, ValidCode.Replace("$\"UPPER({c.ColumnReference})\"", "nonsense")),
            JsonOptions);

        // 200 with diagnostics, not 400: the operator asked "does this compile", and "no" is an answer.
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<CompileResult>(JsonOptions);

        Assert.False(result!.Compiles);
        Assert.True(result.Diagnostics.Single().Line > 0);
    }

    [Fact]
    public async Task ListAndDelete()
    {
        var name = $"upper-{Guid.NewGuid():N}";
        (await _client.PutAsJsonAsync($"/api/scripts/{name}", Definition(name, ValidCode), JsonOptions)).EnsureSuccessStatusCode();

        var listed = await _client.GetFromJsonAsync<List<ScriptListItem>>("/api/scripts", JsonOptions);
        var script = Assert.Single(listed!, s => s.Manifest.Name == name);

        // A script nobody has bound reports so, which is the whole point of the column (phase 37).
        Assert.Empty(script.UsedBy);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/scripts/{name}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/scripts/{name}")).StatusCode);
    }

    private sealed record SlotInfo(string Slot, List<string> Levels, string Label, string Description);

    [Fact]
    public async Task Slots_ReportsWhatThisBuildSupports_AndWhereEachOneBinds()
    {
        var slots = await _client.GetFromJsonAsync<List<SlotInfo>>("/api/scripts/slots", JsonOptions);

        // Driven by the build rather than hardcoded in the SPA, the same way driver capabilities are.
        var transform = Assert.Single(slots!, s => s.Slot == "sqlColumnExpression");
        Assert.Equal(["connection", "replication", "mapping"], transform.Levels);

        // Metadata describes an engine, not a mapping — and the pickers ask before a mapping exists.
        // The SPA renders each slot only where it means something, from this.
        var metadata = Assert.Single(slots!, s => s.Slot == "metadataProvider");
        Assert.Equal(["connection"], metadata.Levels);

        // And each carries a name a person can read: `sqlColumnExpression` is a good key and a bad
        // label, and which one the SPA shows should not be the SPA's guess about the server's slots.
        Assert.Equal("Source SQL for a column", transform.Label);
        Assert.NotEmpty(metadata.Description);
    }
}
