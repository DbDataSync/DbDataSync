using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataSync.Core.Config;

namespace DbDataSync.DevHarness;

/// <summary>
/// Configures DbDataSync through its own REST API rather than by writing config files directly. That
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
    public async Task ConfigureScenarioAsync(
        TargetEngine target, IReadOnlyList<HarnessTable> tables, CancellationToken cancellationToken)
    {
        Log.Step($"Configuring connections, replication and {tables.Count} table mapping(s) (target: {target.Name})");

        await PutAsync($"/api/connections/{Scenario.SourceConnectionName}", new ConnectionInput
        {
            Name = Scenario.SourceConnectionName,
            DriverType = DriverIds.MsSql,
            Host = Scenario.SourceHost,
            Port = Scenario.SourcePort,
            Database = Scenario.DatabaseName,
            AuthMode = AuthMode.SqlAuth,
            UserId = "sa",
            Password = Scenario.SaPassword,
        }, cancellationToken);

        await PutAsync($"/api/connections/{Scenario.TargetConnectionName}", new ConnectionInput
        {
            Name = Scenario.TargetConnectionName,
            DriverType = target.DriverType,
            Host = target.Host,
            Port = target.Port,
            Database = target.TargetDatabaseName,
            AuthMode = AuthMode.SqlAuth,
            UserId = target.UserId,
            Password = target.Password,
        }, cancellationToken);

        await PutAsync($"/api/replications/{Scenario.ReplicationName}", new ReplicationTaskConfig
        {
            Name = Scenario.ReplicationName,
            Enabled = true,
            // Short enough to watch a workload land without waiting around, long enough not to drown
            // the log tail in scheduler ticks.
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 15 },
            ChangeProcessing = new ChangeProcessingConfig
            {
                // From the target engine, not hardcoded: a Postgres target has no upsert writer yet,
                // so it runs a reload pipeline while a SQL Server target runs the incremental one.
                Reader = new ReaderConfig { Kind = target.Pipeline.Reader },
                Cache = new CacheConfig { Kind = target.Pipeline.Cache },
                Writer = new WriterConfig { Kind = target.Pipeline.Writer },
            },
            // Endpoints on the replication; the mapping below inherits both and states only its table.
            Endpoints = new TaskEndpoints
            {
                Source = new EndpointRef { ConnectionName = Scenario.SourceConnectionName, Database = Scenario.DatabaseName },
                Target = new EndpointRef { ConnectionName = Scenario.TargetConnectionName, Database = target.TargetDatabaseName },
            },
        }, cancellationToken);

        // One mapping per generated table. They share the replication's endpoints and pipeline and
        // differ only in their table and column list, which is what makes N mappings a loop rather
        // than N configurations.
        foreach (var table in tables)
        {
            await PutAsync($"/api/replications/{Scenario.ReplicationName}/table-mappings/{table.MappingName}",
                new TableMappingConfig
                {
                    Name = table.MappingName,
                    Sources = [new SourceTableSpec
                    {
                        Schema = Scenario.Schema,
                        Table = table.Name,
                    }],
                    Targets = [new TableSpec
                    {
                        // The one thing the target side cannot inherit: `dbo` and `public` are not the
                        // same word, so a cross-engine mapping states its target schema.
                        Schema = target.SchemaName,
                        Table = table.Name,
                    }],
                    ColumnMappings =
                        [.. table.ColumnNames.Select(c => new ColumnMapping { SourceColumn = c, TargetColumn = c })],
                }, cancellationToken);
        }

        Log.Ok($"replication '{Scenario.ReplicationName}' is configured and enabled with {tables.Count} mapping(s)");
    }

    public async Task TriggerRunAsync(CancellationToken cancellationToken)
    {
        var response = await _http.PostAsync($"/api/replications/{Scenario.ReplicationName}/runs", null, cancellationToken);
        await EnsureSuccessAsync(response);
        Log.Ok("triggered a run");
    }


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
