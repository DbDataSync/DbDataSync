using System.Net;
using System.Net.Http.Json;
using DbDataSync.Api.State;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;
using DbDataSync.State.Remote;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataSync.Api.Tests;

/// <summary>
/// The config half of the runner channel — phase 94 — over the real Kestrel socket the API starts for
/// its children, exactly as <see cref="RunnerStateEndpointTests"/> does for the state half.
/// <para>
/// What this file is really asserting is that the single-writer boundary holds while the capability
/// exists: a runner's provisioning report becomes a committed change to a mapping on disk *without the
/// runner touching the repository*, on the same loopback listener, behind the same token check.
/// </para>
/// </summary>
public sealed class RunnerConfigEndpointTests : IClassFixture<TestApiFactory>
{
    private static readonly GitAuthor Author = new("Test", "test@example.com");

    private readonly TestApiFactory _factory;
    private readonly StateHost _host;
    private readonly RunnerToken _token;
    private readonly ConfigRepository _config;
    private readonly string _replication = $"runner-config-{Guid.NewGuid():N}";

    public RunnerConfigEndpointTests(TestApiFactory factory)
    {
        _factory = factory;
        _ = factory.CreateClient(); // forces the host — and so StateHost — to start
        _host = factory.Services.GetRequiredService<StateHost>();
        _token = factory.Services.GetRequiredService<RunnerToken>();
        _config = factory.Services.GetRequiredService<ConfigRepository>();

        _config.SaveReplicationTask(new ReplicationTaskConfig
        {
            Name = _replication,
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Periodic },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = "BatchReload" },
                Cache = new CacheConfig { Kind = "StagingTable" },
                Writer = new WriterConfig { Kind = "DeleteInsert" },
            },
        }, Author);

        _config.SaveTableMapping(_replication, new TableMappingConfig
        {
            Name = "m",
            Sources = [new SourceTableSpec { ConnectionName = "src", Database = "App", Schema = "dbo", Table = "Orders" }],
            Targets = [new TableSpec { ConnectionName = "tgt", Database = "DW", Schema = "dbo", Table = "Orders" }],
            ColumnMappings = [new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" }],
        }, Author);
    }

    private HttpClient Client(string? token)
    {
        var client = new HttpClient { BaseAddress = new Uri(_host.BaseAddress) };
        if (token is not null)
            client.DefaultRequestHeaders.Add(StateProtocol.TokenHeader, token);
        return client;
    }

    private const string Route = StateProtocol.Route + "/report-provisioned-target-columns";

    [Fact]
    public void A_remote_report_becomes_a_committed_change_to_the_mapping()
    {
        using var http = Client(_token.Value);
        var runner = new RemoteRunnerConfig(http, _ => Assert.Fail("The owner was reachable; nothing should be reported."));

        runner.ReportProvisionedTargetColumns(_replication, "m",
            [new CachedColumn("Id", "int", false, true, false), new CachedColumn("Region", "nvarchar(50)", true, false, false)]);

        // Read back from disk through the owner, which is the only thing that wrote it.
        var stored = _config.LoadTableMapping(_replication, "m");
        Assert.Equal(["Id", "Region"], stored.TargetColumns.Select(c => c.Name));
        Assert.NotNull(stored.ColumnsCapturedUtc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-the-token")]
    public async Task A_report_without_the_right_token_is_refused(string? token)
    {
        // The config route sits under the state prefix precisely so RunnerStateGuard covers it. This is
        // the test that says so — a prefix of its own would have been anonymous.
        using var client = Client(token);

        var response = await client.PostAsJsonAsync(Route,
            new ReportProvisionedTargetColumnsRequest(_replication, "m", [new CachedColumn("Id", "int", false, true, false)]));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_config.LoadTableMapping(_replication, "m").TargetColumns);
    }

    /// <summary>
    /// An empty cache is a state phase 91 acts on — it throws <c>MetadataNotCachedException</c> naming
    /// the Refresh an operator should press. Accepting a report of no columns would let a bad read
    /// clear a good picture and produce that error from the other direction.
    /// </summary>
    [Fact]
    public async Task A_report_naming_no_columns_is_refused()
    {
        using var client = Client(_token.Value);

        var response = await client.PostAsJsonAsync(Route,
            new ReportProvisionedTargetColumnsRequest(_replication, "m", []));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// The behaviour that makes the report safe to be best-effort rather than journalled: a runner
    /// whose owner is unreachable says so and carries on, because the pass it belongs to has already
    /// used the shape it provisioned and the next such pass reports it again.
    /// </summary>
    [Fact]
    public void A_report_the_owner_cannot_receive_is_reported_and_stepped_over()
    {
        // A port nothing is listening on, so the connection is refused rather than answered.
        using var http = new HttpClient
        {
            BaseAddress = new Uri("http://127.0.0.1:1"),
            Timeout = TimeSpan.FromSeconds(2),
        };
        var messages = new List<string>();
        var runner = new RemoteRunnerConfig(http, messages.Add);

        runner.ReportProvisionedTargetColumns(_replication, "m",
            [new CachedColumn("Id", "int", false, true, false)]);

        var message = Assert.Single(messages);
        Assert.Contains("next auto-provisioning pass", message);
    }
}
