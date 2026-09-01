using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataSync.Core.Config;
using DataSync.Core.Secrets;
using Microsoft.Data.SqlClient;

namespace DataSync.Api.Tests;

/// <summary>
/// The preview, against real databases — and the one test that keeps it honest.
/// <para>
/// A preview that could drift from what a pass runs is worse than no preview, because it looks
/// authoritative. So this does not compare the preview's SQL to a string: it **executes** the
/// statement the preview showed and asserts the pass loads exactly what that statement returns. If
/// the two ever diverge, this fails, whatever the text says.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class PreviewIntegrationTests : IClassFixture<TestApiFactory>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string ServerConnectionString =>
        Environment.GetEnvironmentVariable("DATASYNC_TEST_MSSQL_SOURCE_SERVER")
        ?? "Data Source=localhost,14330;User ID=sa;Password=DataSync_Test_Pw1;TrustServerCertificate=True";

    private readonly HttpClient _client;
    private readonly string _databaseName = $"DataSyncPreview_{Guid.NewGuid():N}";
    private readonly string _sourceTable = $"Src_{Guid.NewGuid():N}";
    private readonly string _targetTable = $"Tgt_{Guid.NewGuid():N}";
    private readonly string _connectionName = $"preview-conn-{Guid.NewGuid():N}";
    private readonly string _replicationName = $"preview-repl-{Guid.NewGuid():N}";

    public PreviewIntegrationTests(TestApiFactory factory) => _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        await using (var bootstrap = new SqlConnection(ServerConnectionString))
        {
            await bootstrap.OpenAsync();
            await ExecuteAsync(bootstrap, $"CREATE DATABASE [{_databaseName}];");
            await ExecuteAsync(bootstrap,
                $"ALTER DATABASE [{_databaseName}] SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = OFF);");
        }

        await using var connection = OpenDatabase();
        await ExecuteAsync(connection,
            $"CREATE TABLE dbo.[{_sourceTable}] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);");
        await ExecuteAsync(connection, $"ALTER TABLE dbo.[{_sourceTable}] ENABLE CHANGE_TRACKING;");
        await ExecuteAsync(connection,
            $"INSERT INTO dbo.[{_sourceTable}] (Id, Name) VALUES (1, 'alice'), (2, 'bob'), (3, 'carol');");
        await ExecuteAsync(connection,
            $"CREATE TABLE dbo.[{_targetTable}] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);");
        await ExecuteAsync(connection, "CREATE TABLE dbo.PreviewHookLog (Note NVARCHAR(100) NOT NULL);");

        Environment.SetEnvironmentVariable(
            SecretRefs.EnvironmentVariableFor(SecretRefs.ForConnection(_connectionName)), "DataSync_Test_Pw1");

        (await _client.PutAsJsonAsync($"/api/connections/{_connectionName}", new ConnectionInput
        {
            Name = _connectionName,
            DriverType = ConnectionDriverType.MsSql,
            Host = "localhost",
            Port = 14330,
            Database = _databaseName,
            AuthMode = AuthMode.SqlAuth,
            UserId = "sa",
            Password = "DataSync_Test_Pw1",
        }, JsonOptions)).EnsureSuccessStatusCode();

        (await _client.PutAsJsonAsync($"/api/replications/{_replicationName}", new ReplicationTaskConfig
        {
            Name = _replicationName,
            Enabled = true,
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 3600 },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = "MsSqlChangeTracking" },
                Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
                Writer = new WriterConfig { Kind = "MsSqlMerge" },
            },
            Endpoints = new TaskEndpoints
            {
                Source = new EndpointRef { ConnectionName = _connectionName, Database = _databaseName },
                Target = new EndpointRef { ConnectionName = _connectionName, Database = _databaseName },
            },
        }, JsonOptions)).EnsureSuccessStatusCode();

        // A literal transform and a hook, so the preview has an operator-authored statement of each
        // kind to attribute — the whole point being that the origin of every line is visible.
        (await _client.PutAsJsonAsync(
            $"/api/replications/{_replicationName}/table-mappings/main", new TableMappingConfig
            {
                Name = "main",
                Sources = [new SourceTableSpec { Schema = "dbo", Table = _sourceTable }],
                Targets = [new TableSpec { Schema = "dbo", Table = _targetTable }],
                ColumnMappings =
                [
                    new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" },
                    new ColumnMapping { SourceColumn = "Name", TargetColumn = "Name", Transform = "UPPER({{column}})" },
                ],
                Hooks = new Dictionary<string, List<HookConfig>?>
                {
                    ["beforeLoad"] = [new HookConfig { Name = "note-the-load", Sql = "INSERT INTO dbo.PreviewHookLog (Note) VALUES ('before load');" }],
                },
            }, JsonOptions)).EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable(
            SecretRefs.EnvironmentVariableFor(SecretRefs.ForConnection(_connectionName)), null);

        await using var connection = new SqlConnection(ServerConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;");
        await ExecuteAsync(connection, $"DROP DATABASE [{_databaseName}];");
    }

    [Fact]
    public async Task ThePreviewsSourceRead_ReturnsExactlyWhatThePassLoads()
    {
        var preview = await GetPreviewAsync();

        var read = Assert.Single(preview.Statements.Where(s => s.Stage == "Source read" && s.Sql is not null));
        Assert.Contains("UPPER(", read.Sql!);

        // Executed, not compared. Whatever the preview says it will read, that is what must arrive at
        // the target — and a statement that has drifted from the reader's own fails here.
        var previewRows = await QueryAsync(read.Sql!);

        await TriggerAndWaitAsync();

        var targetRows = await QueryAsync($"SELECT Id, Name FROM dbo.[{_targetTable}];");
        Assert.Equal(new Dictionary<int, string> { [1] = "ALICE", [2] = "BOB", [3] = "CAROL" }, targetRows);
        Assert.Equal(previewRows, targetRows);
    }

    [Fact]
    public async Task ThePreviewDescribesEveryStageInTheOrderAPassRunsThem_WithEachOnesOrigin()
    {
        var preview = await GetPreviewAsync();

        // The order is the point: read as a description of what happens, out-of-order would be wrong.
        var stages = preview.Statements.Select(s => s.Stage).Distinct().ToList();
        Assert.Equal(["Source read", "Staging", "Before load", "Write"], stages);
        Assert.Empty(preview.Problems);

        var hook = Assert.Single(preview.Statements.Where(s => s.Stage == "Before load"));
        Assert.Equal("Hook: note-the-load", hook.Title);
        Assert.Equal("OperatorSql", hook.Origin);
        Assert.Contains("PreviewHookLog", hook.Sql!);

        // Staging is DDL plus a bulk load that has no statement — named rather than invented.
        var staging = preview.Statements.Where(s => s.Stage == "Staging").ToList();
        Assert.Contains(staging, s => s.Sql?.StartsWith("CREATE TABLE #Staging_") == true);
        Assert.Contains(staging, s => s.Sql is null && s.Detail!.Contains("SqlBulkCopy"));

        var write = Assert.Single(preview.Statements.Where(s => s.Stage == "Write"));
        Assert.Contains("MERGE INTO", write.Sql!);
    }

    /// <summary>
    /// The reader's statement depends on the stored watermark, so the preview has to as well. A
    /// preview that always showed the first-pass form would be right exactly once.
    /// </summary>
    [Fact]
    public async Task AfterAPass_ThePreviewShowsTheIncrementalReadRatherThanTheFullLoad()
    {
        var before = await GetPreviewAsync();
        Assert.Contains(
            before.Statements,
            s => s.Stage == "Source read" && s.Title.StartsWith("Full load"));

        await TriggerAndWaitAsync();

        var after = await GetPreviewAsync();
        var reads = after.Statements.Where(s => s.Stage == "Source read" && s.Sql is not null).ToList();

        // Two statements, in the order a pass issues them: the bounded window's end position is asked
        // for first, and the read that uses it comes second. Showing only the second one left the
        // version in it looking like a number nobody could account for.
        Assert.Equal(2, reads.Count);
        Assert.Contains("CHANGE_TRACKING_CURRENT_VERSION()", reads[0].Sql!);
        Assert.StartsWith("Ask the source for its current change-tracking version", reads[0].Title);
        Assert.StartsWith("Incremental read of changes after version", reads[1].Title);
        Assert.Contains("CHANGETABLE", reads[1].Sql!);
    }

    /// <summary>
    /// Live mode, which is the half of phase 41 with a safety property: it happens only because a
    /// connection was named, it reads real rows, and the result says which system it touched. The
    /// labelling is asserted because it is the whole guarantee.
    /// </summary>
    [Fact]
    public async Task ALiveScriptTest_ReadsRealRows_AndSaysWhichConnectionItQueried()
    {
        var scriptName = $"live-test-{Guid.NewGuid():N}";
        var response = await _client.PostAsJsonAsync($"/api/scripts/{scriptName}/test", new
        {
            script = new ScriptDefinition
            {
                Manifest = new ScriptConfig
                {
                    Name = scriptName, Kind = "valueColumnExpression", EntryType = "Upper",
                },
                Code = """
                    using System.Collections.Generic;
                    using DataSync.Scripting.Abstractions;

                    public sealed class Upper : IValueColumnExpression
                    {
                        public IReadOnlyList<string> DeclareColumns(ValueColumnDeclarationContext c) => ["Name"];

                        public object? Evaluate(object? value, ValueColumnExpressionContext c) =>
                            value is string s ? s.ToUpperInvariant() : value;
                    }
                    """,
            },
            replicationName = _replicationName,
            mappingName = "main",
            connectionName = _connectionName,
            sampleRows = 3,
        }, JsonOptions);

        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<ScriptTestResultDto>(JsonOptions))!;

        Assert.Null(result.Error);
        Assert.Equal("live", result.Mode);
        Assert.Equal($"live query against '{_connectionName}'", result.Source);

        // The rows are the real ones seeded in this database — 'alice', not a generated 'sample'.
        Assert.Contains(result.Cases, c => c.Input == "Name = 'alice'" && c.Output == "'ALICE'");
    }

    /// <summary>
    /// The endpoint against a replication that has actually run — the numbers come from TaskRuns, so
    /// the only way to know they are the right ones is to produce a run and look.
    /// </summary>
    [Fact]
    public async Task Metrics_ReportTheRunThatJustHappened_AndWhenItCompleted()
    {
        var before = await GetMetricsAsync();
        Assert.Equal(0, before.Runs);
        Assert.Null(before.LastCompletedPassUtc);

        await TriggerAndWaitAsync();

        var after = await GetMetricsAsync();
        Assert.Equal(1, after.Runs);
        Assert.Equal(0, after.Failures);
        Assert.Equal(3, after.RowsWritten);
        Assert.NotNull(after.DurationP50Ms);
        Assert.NotNull(after.LastCompletedPassUtc);

        // The window is a real filter, not decoration: a one-hour window still holds a run from a
        // moment ago, and the bucket count follows what was asked for.
        var hour = await GetMetricsAsync("1h");
        Assert.Equal(1, hour.Runs);
        Assert.Equal(24, hour.Buckets.Count);
        Assert.Equal(1, hour.Buckets.Sum(b => b.Runs));

        // A backfill is a different question and is not folded into the incremental figures.
        var backfill = await GetMetricsAsync(kind: "Backfill");
        Assert.Equal(0, backfill.Runs);
    }

    [Fact]
    public async Task AnUnrecognisedWindow_IsRefusedRatherThanSilentlyDefaulted()
    {
        var response = await _client.GetAsync(
            $"/api/replications/{_replicationName}/metrics?window=90d");

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    private sealed record RunMetricsBucketDto(DateTimeOffset StartUtc, int Runs, int Failures, long RowsWritten);
    private sealed record RunMetricsDto(
        int Runs, int Failures, long RowsRead, long RowsWritten,
        double? DurationP50Ms, double? DurationP95Ms, double? DurationMaxMs,
        DateTimeOffset? LastCompletedPassUtc, List<RunMetricsBucketDto> Buckets);

    private async Task<RunMetricsDto> GetMetricsAsync(string window = "24h", string kind = "Primary")
    {
        var response = await _client.GetAsync(
            $"/api/replications/{_replicationName}/metrics?window={window}&kind={kind}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RunMetricsDto>(JsonOptions))!;
    }

    private sealed record ScriptTestCaseDto(string Input, string? Output, string? Note);
    private sealed record ScriptTestResultDto(
        string Mode, string Source, List<ScriptTestCaseDto> Cases, List<string> Log, string? Statement, string? Error);

    /// <summary>
    /// A check, end to end: enqueued as a run, executed by a real TaskRunner process, compared against
    /// a target the run itself populated, and read back out of the parquet the runner wrote.
    /// </summary>
    [Fact]
    public async Task AVerificationCheck_RunsAgainstBothSides_AndItsResultIsReadableAfterwards()
    {
        // Group by a column the two sides spell differently, so the result proves the check named it
        // once and each side's statement was derived — the whole point of naming by target column.
        await ExecuteAsync(await OpenDatabaseAsync(),
            $"ALTER TABLE dbo.[{_sourceTable}] ADD region NVARCHAR(20) NULL;");
        await ExecuteAsync(await OpenDatabaseAsync(),
            $"UPDATE dbo.[{_sourceTable}] SET region = CASE WHEN Id = 1 THEN 'north' ELSE 'south' END;");
        await ExecuteAsync(await OpenDatabaseAsync(),
            $"ALTER TABLE dbo.[{_targetTable}] ADD Region NVARCHAR(20) NULL;");

        (await _client.PutAsJsonAsync(
            $"/api/replications/{_replicationName}/table-mappings/main", new TableMappingConfig
            {
                Name = "main",
                Sources = [new SourceTableSpec { Schema = "dbo", Table = _sourceTable }],
                Targets = [new TableSpec { Schema = "dbo", Table = _targetTable }],
                ColumnMappings =
                [
                    new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" },
                    new ColumnMapping { SourceColumn = "Name", TargetColumn = "Name", Transform = "UPPER({{column}})" },
                    new ColumnMapping { SourceColumn = "region", TargetColumn = "Region" },
                ],
                Verification =
                [
                    new VerificationCheckConfig { Name = "rows-by-region", GroupBy = ["Region"] },
                    new VerificationCheckConfig { Name = "total-rows" },
                ],
            }, JsonOptions)).EnsureSuccessStatusCode();

        // Replicate first, so the two sides genuinely agree and a difference would mean something.
        await TriggerAndWaitAsync();

        var trigger = await _client.PostAsync(
            $"/api/replications/{_replicationName}/mappings/main/verify", null);
        trigger.EnsureSuccessStatusCode();

        var results = await WaitForResultsAsync(expected: 2);

        var grouped = Assert.Single(results, r => r.CheckName == "rows-by-region");
        Assert.Equal(2, grouped.GroupsCompared);
        Assert.Equal(0, grouped.DifferingGroups);

        var total = Assert.Single(results, r => r.CheckName == "total-rows");
        Assert.Equal(1, total.GroupsCompared);
        Assert.Equal(0, total.DifferingGroups);

        // The parquet the runner wrote, read back through the API — which is the only proof the file
        // is where the index says and says what the run found.
        var response = await _client.GetAsync(
            $"/api/replications/{_replicationName}/verification-results/{grouped.Id}");
        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<VerificationResultDto>(JsonOptions))!;

        Assert.Equal(["Region"], result.GroupColumns);
        Assert.Equal(["north", "south"], result.Rows.Select(r => r.Group[0]));
        Assert.All(result.Rows, r => Assert.Equal("Match", r.Status));

        // Both sides' read times, separately — the gap is what a difference has to be weighed against.
        Assert.NotEqual(default, result.SourceReadAtUtc);
        Assert.NotEqual(default, result.TargetReadAtUtc);
    }

    /// <summary>The check that catches what a row count cannot: the right number of rows carrying the
    /// wrong values.</summary>
    [Fact]
    public async Task ACheckFindsADifference_WhenTheTargetIsChangedBehindTheReplicationsBack()
    {
        (await _client.PutAsJsonAsync(
            $"/api/replications/{_replicationName}/table-mappings/main", new TableMappingConfig
            {
                Name = "main",
                Sources = [new SourceTableSpec { Schema = "dbo", Table = _sourceTable }],
                Targets = [new TableSpec { Schema = "dbo", Table = _targetTable }],
                ColumnMappings =
                [
                    new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" },
                    new ColumnMapping { SourceColumn = "Name", TargetColumn = "Name", Transform = "UPPER({{column}})" },
                ],
                Verification = [new VerificationCheckConfig { Name = "total-rows" }],
            }, JsonOptions)).EnsureSuccessStatusCode();

        await TriggerAndWaitAsync();

        // A row nobody replicated — exactly what a verification check exists to notice.
        await ExecuteAsync(await OpenDatabaseAsync(),
            $"INSERT INTO dbo.[{_targetTable}] (Id, Name) VALUES (99, 'GHOST');");

        (await _client.PostAsync($"/api/replications/{_replicationName}/mappings/main/verify", null))
            .EnsureSuccessStatusCode();

        var record = Assert.Single(await WaitForResultsAsync(expected: 1));
        Assert.Equal(1, record.DifferingGroups);

        var response = await _client.GetAsync(
            $"/api/replications/{_replicationName}/verification-results/{record.Id}");
        var result = (await response.Content.ReadFromJsonAsync<VerificationResultDto>(JsonOptions))!;

        var row = Assert.Single(result.Rows);
        Assert.Equal("Differs", row.Status);
        Assert.Equal(1, row.Differences["__rows"]);
    }

    [Fact]
    public async Task AMappingWithNoChecks_IsRefusedRatherThanQueueingARunThatDoesNothing()
    {
        var response = await _client.PostAsync(
            $"/api/replications/{_replicationName}/mappings/main/verify", null);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("no verification checks", await response.Content.ReadAsStringAsync());
    }

    private sealed record VerificationResultRowDto(
        List<string> Group, Dictionary<string, double>? Source, Dictionary<string, double>? Target,
        Dictionary<string, double> Differences, string Status);

    private sealed record VerificationResultDto(
        string CheckName, List<string> GroupColumns, List<string> MeasureColumns,
        double DifferenceThreshold, DateTimeOffset SourceReadAtUtc, DateTimeOffset TargetReadAtUtc,
        List<VerificationResultRowDto> Rows);

    private sealed record VerificationIndexDto(
        long Id, string CheckName, int GroupsCompared, int DifferingGroups, string ResultPath);

    private async Task<IReadOnlyList<VerificationIndexDto>> WaitForResultsAsync(int expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var results = await _client.GetFromJsonAsync<List<VerificationIndexDto>>(
                $"/api/replications/{_replicationName}/verification-results", JsonOptions);
            if (results!.Count >= expected)
                return results;
            await Task.Delay(250);
        }

        throw new TimeoutException($"Only saw fewer than {expected} verification result(s) within 45s.");
    }

    private async Task<SqlConnection> OpenDatabaseAsync()
    {
        var builder = new SqlConnectionStringBuilder(ServerConnectionString) { InitialCatalog = _databaseName };
        var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    /// <summary>
    /// Schema evolution against a real target: a mapped column the table lacks becomes an ALTER, and
    /// applying it makes the next plan quiet. The restraint is asserted too — a column the mapping no
    /// longer writes is never dropped.
    /// </summary>
    [Fact]
    public async Task AMappedColumnTheTargetLacks_IsPlannedAsAnAlterAndCanBeApplied()
    {
        await ExecuteAsync(await OpenDatabaseAsync(),
            $"ALTER TABLE dbo.[{_sourceTable}] ADD note NVARCHAR(30) NULL;");
        await ExecuteAsync(await OpenDatabaseAsync(),
            $"ALTER TABLE dbo.[{_targetTable}] ADD Retired NVARCHAR(10) NULL;");

        (await _client.PutAsJsonAsync(
            $"/api/replications/{_replicationName}/table-mappings/main", new TableMappingConfig
            {
                Name = "main",
                Sources = [new SourceTableSpec { Schema = "dbo", Table = _sourceTable }],
                Targets = [new TableSpec { Schema = "dbo", Table = _targetTable }],
                ColumnMappings =
                [
                    new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" },
                    new ColumnMapping { SourceColumn = "Name", TargetColumn = "Name" },
                    // On the source, absent from the target: the case this action exists for.
                    new ColumnMapping { SourceColumn = "note", TargetColumn = "Note" },
                ],
            }, JsonOptions)).EnsureSuccessStatusCode();

        var plan = await GetTargetPlanAsync();

        Assert.Equal("alterTargetTable", plan.Action);
        Assert.Equal("Missing", plan.State);
        var step = Assert.Single(plan.Steps);
        Assert.Contains("Add column Note", step.Title);
        Assert.Contains("ALTER TABLE", step.CommandText);

        // Never DROP. The target's own Retired column is not mapped and is left exactly alone — it is
        // a column something else may still be reading.
        Assert.DoesNotContain(plan.Steps, s => s.CommandText.Contains("DROP", StringComparison.OrdinalIgnoreCase));

        var applied = await _client.PostAsync(
            $"/api/replications/{_replicationName}/table-mappings/main/provisioning/alterTargetTable/apply", null);
        applied.EnsureSuccessStatusCode();

        // And the plan goes quiet, which is the only proof the statement did what it said.
        Assert.Equal("Satisfied", (await GetTargetPlanAsync()).State);

        var columns = await QueryColumnsAsync(_targetTable);
        Assert.Contains("Note", columns);
        Assert.Contains("Retired", columns);
    }

    /// <summary>
    /// A renamed target column is renamed on the target, not dropped and re-added — the whole point
    /// being that the rows keep their values. Asserted against real data, because the failure mode
    /// this guards against (an ADD beside the old column, or a DROP) still leaves a table that looks
    /// right in the catalog and is empty in the column that matters.
    /// </summary>
    [Fact]
    public async Task ARenamedTargetColumn_IsRenamedAndKeepsItsRows()
    {
        await ExecuteAsync(await OpenDatabaseAsync(),
            $"INSERT INTO dbo.[{_targetTable}] (Id, Name) VALUES (7, 'dave');");

        (await _client.PutAsJsonAsync(
            $"/api/replications/{_replicationName}/table-mappings/main", new TableMappingConfig
            {
                Name = "main",
                Sources = [new SourceTableSpec { Schema = "dbo", Table = _sourceTable }],
                Targets = [new TableSpec { Schema = "dbo", Table = _targetTable }],
                ColumnMappings =
                [
                    new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" },
                    new ColumnMapping
                    {
                        SourceColumn = "Name",
                        TargetColumn = "FullName",
                        Renames = [new RenameStep("Name", "FullName")],
                    },
                ],
            }, JsonOptions)).EnsureSuccessStatusCode();

        var plan = await GetTargetPlanAsync();
        var step = Assert.Single(plan.Steps);
        Assert.Contains("Rename Name to FullName", step.Title);

        (await _client.PostAsync(
            $"/api/replications/{_replicationName}/table-mappings/main/provisioning/alterTargetTable/apply", null))
            .EnsureSuccessStatusCode();

        var columns = await QueryColumnsAsync(_targetTable);
        Assert.Contains("FullName", columns);
        Assert.DoesNotContain("Name", columns);

        // The row that was there before the rename still has its value: renamed, not recreated.
        await using var connection = await OpenDatabaseAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT FullName FROM dbo.[{_targetTable}] WHERE Id = 7;";
        Assert.Equal("dave", (string)(await cmd.ExecuteScalarAsync())!);

        // And the plan goes quiet rather than planning the rename again on the next pass.
        Assert.Equal("Satisfied", (await GetTargetPlanAsync()).State);

        // The rename is recorded as done. Bookkeeping rather than a latch — the planner reads the
        // target either way — but a history that says a finished rename is still outstanding is a
        // history nobody can read.
        var saved = await _client.GetFromJsonAsync<TableMappingConfig>(
            $"/api/replications/{_replicationName}/table-mappings/main", JsonOptions);
        Assert.True(Assert.Single(saved!.ColumnMappings[1].Renames).Applied);
    }

    /// <summary>
    /// The column mapping editor's inferred-type read path. It is the *same* translation the DDL
    /// uses, which is the reason for the endpoint existing rather than the SPA guessing: what the
    /// editor shows and what CREATE TABLE says cannot drift apart if there is only one of them.
    /// </summary>
    [Fact]
    public async Task InferredColumnTypes_AnswerEverySourceColumn()
    {
        var inferred = await _client.GetFromJsonAsync<List<InferredColumnTypeDto>>(
            $"/api/replications/{_replicationName}/table-mappings/main/provisioning/inferred-column-types",
            JsonOptions);

        Assert.Equal(2, inferred!.Count);

        var name = Assert.Single(inferred, i => i.SourceColumn == "Name");
        Assert.Equal("nvarchar(50)", name.SourceType, ignoreCase: true);
        // Same engine on both sides here, so the inference is a round trip — which is exactly what
        // makes a wrong answer visible.
        Assert.Equal("nvarchar(50)", name.TargetType, ignoreCase: true);
        Assert.Null(name.Problem);
    }

    private sealed record InferredColumnTypeDto(
        string SourceColumn, string SourceType, string? TargetType, string? Fidelity, string? Problem);

    private sealed record ProvisioningStepDto(string Title, string CommandText);
    private sealed record ProvisioningPlanDto(
        string Action, string State, List<ProvisioningStepDto> Steps, List<string> Warnings);
    private sealed record ProvisioningPlanReportDto(ProvisioningPlanDto Source, ProvisioningPlanDto Target);

    private async Task<ProvisioningPlanDto> GetTargetPlanAsync()
    {
        var response = await _client.GetAsync(
            $"/api/replications/{_replicationName}/table-mappings/main/provisioning");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProvisioningPlanReportDto>(JsonOptions))!.Target;
    }

    private async Task<List<string>> QueryColumnsAsync(string table)
    {
        await using var connection = OpenDatabase();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT name FROM sys.columns WHERE object_id = OBJECT_ID(@t) ORDER BY name;";
        cmd.Parameters.AddWithValue("@t", $"[dbo].[{table}]");

        await using var reader = await cmd.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync())
            names.Add(reader.GetString(0));
        return names;
    }

    private sealed record PreviewStatementDto(string Stage, string Title, string? Sql, string Origin, string? Detail);
    private sealed record PreviewReportDto(List<PreviewStatementDto> Statements, List<string> Problems);

    private async Task<PreviewReportDto> GetPreviewAsync()
    {
        var response = await _client.GetAsync(
            $"/api/replications/{_replicationName}/table-mappings/main/preview");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PreviewReportDto>(JsonOptions))!;
    }

    private async Task<Dictionary<int, string>> QueryAsync(string sql)
    {
        await using var connection = OpenDatabase();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync();

        var rows = new Dictionary<int, string>();
        while (await reader.ReadAsync())
            rows[reader.GetInt32(0)] = reader.GetString(1);
        return rows;
    }

    private async Task TriggerAndWaitAsync()
    {
        var trigger = await _client.PostAsync(
            $"/api/replications/{_replicationName}/runs", new StringContent("", Encoding.UTF8, "application/json"));
        trigger.EnsureSuccessStatusCode();
        var body = await trigger.Content.ReadFromJsonAsync<JsonElement>();
        var runId = Guid.Parse(body.GetProperty("runIds").EnumerateArray().Single().GetString()!);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var response = await _client.GetAsync($"/api/runs/{runId}");
            if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                var run = await response.Content.ReadFromJsonAsync<JsonElement>();
                var status = run.GetProperty("status").GetString();
                if (status == "Succeeded")
                    return;
                if (status is "Failed" or "Cancelled")
                    throw new InvalidOperationException($"Run {runId} {status}: {run.GetProperty("errorSummary")}");
            }
            await Task.Delay(250);
        }

        throw new TimeoutException($"Run {runId} did not finish within 30s.");
    }

    private SqlConnection OpenDatabase()
    {
        var builder = new SqlConnectionStringBuilder(ServerConnectionString) { InitialCatalog = _databaseName };
        var connection = new SqlConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
