using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataSync.Core.Config;

namespace DataSync.DevHarness;

/// <summary>
/// Configures DataSync through its own REST API rather than by writing config files directly. That
/// keeps the harness honest: standing up the environment exercises the same endpoints, validation and
/// git auto-commit path an operator using the UI would go through, so a break in them shows up here
/// too instead of being bypassed.
/// </summary>
public sealed class ApiClient(string baseUrl) : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _http = new() { BaseAddress = new Uri(baseUrl) };

    public void Dispose() => _http.Dispose();

    public async Task WaitUntilHealthyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        Log.Step($"Waiting for the API at {baseUrl}");
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var response = await _http.GetAsync("/api/health", cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    Log.Ok("API is up");
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new HarnessException(
            $"The API at {baseUrl} did not become healthy within {timeout.TotalSeconds:0}s. " +
            "If you started it yourself, check it's listening on that address.");
    }

    /// <summary>Creates the connections, replication and table mapping the scenario needs. Every call
    /// is a PUT, so re-running <c>up</c> against an existing config converges rather than failing.</summary>
    public async Task ConfigureScenarioAsync(CancellationToken cancellationToken)
    {
        Log.Step("Configuring connections, replication and table mapping");

        await PutAsync($"/api/connections/{Scenario.SourceConnectionName}",
            MakeConnection(Scenario.SourceConnectionName, Scenario.SourceHost, Scenario.SourcePort), cancellationToken);
        await PutAsync($"/api/connections/{Scenario.TargetConnectionName}",
            MakeConnection(Scenario.TargetConnectionName, Scenario.TargetHost, Scenario.TargetPort), cancellationToken);

        await PutAsync($"/api/replications/{Scenario.ReplicationName}", new ReplicationTaskConfig
        {
            Name = Scenario.ReplicationName,
            Enabled = true,
            // Short enough to watch a workload land without waiting around, long enough not to drown
            // the log tail in scheduler ticks.
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 15 },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = "MsSqlChangeTracking" },
                Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
                Writer = new WriterConfig { Kind = "MsSqlMerge" },
            },
            // Endpoints on the replication; the mapping below inherits both and states only its table.
            Endpoints = new TaskEndpoints
            {
                Source = new EndpointRef { ConnectionName = Scenario.SourceConnectionName, Database = Scenario.DatabaseName },
                Target = new EndpointRef { ConnectionName = Scenario.TargetConnectionName, Database = Scenario.DatabaseName },
            },
        }, cancellationToken);

        await PutAsync($"/api/replications/{Scenario.ReplicationName}/table-mappings/{Scenario.MappingName}",
            new TableMappingConfig
            {
                Name = Scenario.MappingName,
                Sources = [new SourceTableSpec
                {
                    Schema = Scenario.Schema,
                    Table = Scenario.Table,
                }],
                Targets = [new TableSpec
                {
                    Schema = Scenario.Schema,
                    Table = Scenario.Table,
                }],
                ColumnMappings = [.. Scenario.Columns.Select(c => new ColumnMapping { SourceColumn = c, TargetColumn = c })],
            }, cancellationToken);

        Log.Ok($"replication '{Scenario.ReplicationName}' is configured and enabled");
    }

    public async Task TriggerRunAsync(CancellationToken cancellationToken)
    {
        var response = await _http.PostAsync($"/api/replications/{Scenario.ReplicationName}/runs", null, cancellationToken);
        await EnsureSuccessAsync(response);
        Log.Ok("triggered a run");
    }

    private static ConnectionInput MakeConnection(string name, string host, int port) => new()
    {
        Name = name,
        DriverType = ConnectionDriverType.MsSql,
        Host = host,
        Port = port,
        Database = Scenario.DatabaseName,
        AuthMode = AuthMode.SqlAuth,
        UserId = "sa",
        Password = Scenario.SaPassword,
    };

    private async Task PutAsync<T>(string path, T body, CancellationToken cancellationToken)
    {
        var response = await _http.PutAsJsonAsync(path, body, JsonOptions, cancellationToken);
        await EnsureSuccessAsync(response);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync();
        throw new HarnessException($"{(int)response.StatusCode} {response.ReasonPhrase} from {response.RequestMessage?.RequestUri}: {body}");
    }
}
