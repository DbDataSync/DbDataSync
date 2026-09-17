using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// The two endpoints phase 35 puts on the Version Control tab, over the wire.
/// <para>
/// What <c>ConfigRepository</c> does with a diff or a restore is
/// <c>ConfigHistoryDiffAndRestoreTests</c>'s subject, against a real git repository. These cover what
/// only crossing the boundary can: that the routes are shaped the way the client builds them, that a
/// sha which resolves to nothing is a 404 rather than a 500, that a refused restore is a 400 carrying
/// the reason, and that the enum on the wire is the string the SPA switches on rather than an integer.
/// </para>
/// </summary>
public sealed class ConfigHistoryEndpointTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private static readonly GitAuthor Author = new("Test", "test@example.com");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _client = factory.CreateClient();

    private ConfigRepository Config => factory.Services.GetRequiredService<ConfigRepository>();

    private string SetUp()
    {
        var name = $"history-{Guid.NewGuid():N}";
        Config.SaveReplicationTask(new ReplicationTaskConfig
        {
            Name = name,
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 30 },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = "MsSqlChangeTracking" },
                Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
                Writer = new WriterConfig { Kind = "MsSqlMerge" },
            },
        }, Author);
        return name;
    }

    private void SaveMapping(string replication, string targetTable) =>
        Config.SaveTableMapping(replication, new TableMappingConfig
        {
            Name = "orders",
            Sources = [new SourceTableSpec { ConnectionName = "src", Database = "App", Table = "Orders" }],
            Targets = [new TableSpec { ConnectionName = "tgt", Database = "DW", Table = targetTable }],
            ColumnMappings = [new ColumnMapping { SourceColumn = "Id", TargetColumn = "OrderId" }],
        }, Author);

    private async Task<T> GetAsync<T>(string url)
    {
        var response = await _client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(JsonOptions))!;
    }

    [Fact]
    public async Task ACommitsDiff_ComesBackWithBothSidesAndAStringChangeKind()
    {
        var name = SetUp();
        SaveMapping(name, "Orders");
        SaveMapping(name, "OrdersV2");
        var sha = Config.GetReplicationHistory(name)[0].Sha;

        // Read as raw JSON as well as typed, because "the enum is a string" is a claim about the wire
        // that a typed deserialization with a converter would hide.
        var raw = await _client.GetStringAsync($"/api/replications/{name}/history/{sha}/diff");
        Assert.Contains("\"kind\":\"Modified\"", raw);

        var diff = await GetAsync<ConfigDiff>($"/api/replications/{name}/history/{sha}/diff");
        var change = Assert.Single(diff.Changes);
        Assert.Equal(ConfigChangeKind.Modified, change.Kind);
        Assert.Contains("table: Orders", change.Before);
        Assert.Contains("table: OrdersV2", change.After);
        Assert.False(diff.Truncated);
    }

    /// <summary>The distinction the confirmation rests on, asserted across the boundary: two endpoints,
    /// two different answers for the same commit.</summary>
    [Fact]
    public async Task TheRestorePreview_AnswersADifferentQuestionFromTheCommitsOwnDiff()
    {
        var name = SetUp();
        SaveMapping(name, "Orders");
        var sha = Config.GetReplicationHistory(name)[0].Sha;
        SaveMapping(name, "OrdersV2");

        var commitDiff = await GetAsync<ConfigDiff>($"/api/replications/{name}/history/{sha}/diff");
        Assert.Equal(ConfigChangeKind.Added, Assert.Single(commitDiff.Changes).Kind);

        var preview = await GetAsync<ConfigDiff>($"/api/replications/{name}/history/{sha}/restore-preview");
        Assert.Equal(ConfigChangeKind.Modified, Assert.Single(preview.Changes).Kind);
    }

    [Fact]
    public async Task AShaThatIsNotACommit_IsA404RatherThanA500()
    {
        var name = SetUp();

        var response = await _client.GetAsync(
            $"/api/replications/{name}/history/0123456789abcdef0123456789abcdef01234567/diff");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ARestore_PutsTheConfigBackAndReportsTheNewCommit()
    {
        var name = SetUp();
        SaveMapping(name, "Orders");
        var sha = Config.GetReplicationHistory(name)[0].Sha;
        SaveMapping(name, "OrdersV2");

        var response = await _client.PostAsync($"/api/replications/{name}/history/{sha}/restore", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<ConfigRestoreResult>(JsonOptions))!;

        Assert.Equal(sha, result.RestoredFromSha);
        Assert.NotNull(result.CommitSha);
        Assert.Equal("Orders", Config.LoadTableMapping(name, "orders").Targets[0].Table);

        // The log grew by the restore rather than losing the commit it undid.
        var history = Config.GetReplicationHistory(name);
        Assert.StartsWith($"Restore replication '{name}'", history[0].Message);
        Assert.Contains(history, c => c.Sha == sha);
    }

    /// <summary>
    /// A restore to before the replication existed would mean deleting it, which is what Delete is for.
    /// Refused as a 400 carrying the reason, not a 500 — the message is the whole value of the refusal.
    /// </summary>
    [Fact]
    public async Task ARestoreThatWouldEmptyTheReplication_IsA400WithTheReason()
    {
        var first = SetUp();
        var beforeSecond = Config.GetReplicationHistory(first)[0].Sha;
        var second = SetUp();

        var response = await _client.PostAsync($"/api/replications/{second}/history/{beforeSecond}/restore", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("did not exist at commit", await response.Content.ReadAsStringAsync());
    }
}
