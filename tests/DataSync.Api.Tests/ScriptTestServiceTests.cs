using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataSync.Api.Services;
using DataSync.Core.Config;

namespace DataSync.Api.Tests;

/// <summary>
/// Compiling proves a script is C#. It proves nothing about whether it produces the SQL, the value or
/// the rows the operator meant — and for a change query, being wrong is not a crash but a target that
/// silently disagrees with its source. These are the tests for the button in between.
/// <para>
/// Asserted on the *shape* of the generated input rather than on exact values, so choosing a different
/// characteristic value later is not a test failure. The one exception is the delete, whose shape is
/// the whole point of it.
/// </para>
/// </summary>
public sealed class ScriptTestServiceTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _client = factory.CreateClient();

    private async Task<ScriptTestResult> TestAsync(string kind, string entryType, string code, string? connectionName = null)
    {
        var name = $"test-{Guid.NewGuid():N}";
        var response = await _client.PostAsJsonAsync($"/api/scripts/{name}/test", new
        {
            script = new ScriptDefinition
            {
                Manifest = new ScriptConfig { Name = name, Kind = kind, EntryType = entryType },
                Code = code,
            },
            connectionName,
        }, JsonOptions);

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ScriptTestResult>(JsonOptions))!;
    }

    [Fact]
    public async Task ASqlColumnExpression_ShowsTheExpressionItWouldGenerateForEachColumn()
    {
        var result = await TestAsync("sqlColumnExpression", "Upper", """
            using DataSync.Scripting.Abstractions;

            public sealed class Upper : ISqlColumnExpression
            {
                public string? RenderSql(SqlColumnExpressionContext c) =>
                    c.SourceColumn == "Name" ? $"UPPER({c.ColumnReference})" : null;
            }
            """);

        Assert.Null(result.Error);
        Assert.Equal("generated", result.Mode);
        Assert.StartsWith("generated sample", result.Source);

        var name = Assert.Single(result.Cases, c => c.Input.StartsWith("Name"));
        Assert.Contains("UPPER(", name.Output!);

        // Returning null is a real answer — "leave the column as it would have been" — and is shown as
        // one rather than as an empty cell.
        var id = Assert.Single(result.Cases, c => c.Input.StartsWith("Id"));
        Assert.Null(id.Output);
        Assert.Contains("left as it would have been", id.Note!);
    }

    [Fact]
    public async Task AValueTransform_IsRunOverCharacteristicValuesIncludingNullAndEmpty()
    {
        var result = await TestAsync("valueColumnExpression", "Trim", """
            using System.Collections.Generic;
            using DataSync.Scripting.Abstractions;

            public sealed class Trim : IValueColumnExpression
            {
                public IReadOnlyList<string> DeclareColumns(ValueColumnDeclarationContext c) => ["Name"];

                public object? Evaluate(object? value, ValueColumnExpressionContext c) =>
                    value is string s ? s.ToUpperInvariant() : value;
            }
            """);

        Assert.Null(result.Error);
        Assert.All(result.Cases, c => Assert.StartsWith("Name = ", c.Input));

        // The values that find the bug: an empty string, and a null on a nullable column.
        Assert.Contains(result.Cases, c => c.Input == "Name = ''");
        Assert.Contains(result.Cases, c => c.Input == "Name = NULL");
        Assert.Contains(result.Cases, c => c.Output == "'SAMPLE'");
    }

    /// <summary>
    /// The case a row transform is most likely to mishandle, because it is the one nobody types into
    /// their head while writing the happy path: a delete carries its key and nothing else.
    /// </summary>
    [Fact]
    public async Task ARowTransform_ReceivesADeleteCarryingOnlyItsKey()
    {
        var result = await TestAsync("rowTransform", "Passthrough", """
            using System.Threading;
            using System.Threading.Tasks;
            using DataSync.Drivers.Abstractions;
            using DataSync.Scripting.Abstractions;

            public sealed class Passthrough : IRowTransform
            {
                public ChangeSchema DeclareSchema(ChangeSchema input, RowTransformContext c) => input;

                public ValueTask<ChangeRow?> TransformAsync(ChangeRow row, RowTransformContext c, CancellationToken ct)
                    => ValueTask.FromResult<ChangeRow?>(row);
            }
            """);

        Assert.Null(result.Error);
        Assert.Equal(["Insert", "Update", "Delete"], result.Cases.Select(c => c.Input.Split(':')[0]));

        var delete = Assert.Single(result.Cases, c => c.Input.StartsWith("Delete"));
        Assert.Contains("Id=", delete.Input);
        Assert.Contains("Name=NULL", delete.Input);
    }

    [Fact]
    public async Task ARowTransformThatDropsARow_SaysSo()
    {
        var result = await TestAsync("rowTransform", "DropDeletes", """
            using System.Threading;
            using System.Threading.Tasks;
            using DataSync.Drivers.Abstractions;
            using DataSync.Scripting.Abstractions;

            public sealed class DropDeletes : IRowTransform
            {
                public ChangeSchema DeclareSchema(ChangeSchema input, RowTransformContext c) => input;

                public ValueTask<ChangeRow?> TransformAsync(ChangeRow row, RowTransformContext c, CancellationToken ct)
                    => ValueTask.FromResult<ChangeRow?>(row.Operation == ChangeOperation.Delete ? null : row);
            }
            """);

        var delete = Assert.Single(result.Cases, c => c.Input.StartsWith("Delete"));
        Assert.Null(delete.Output);
        Assert.Equal("dropped", delete.Note);
    }

    /// <summary>
    /// The test is also where an operator finds out their script throws on a null. A blank panel would
    /// leave them to guess.
    /// </summary>
    [Fact]
    public async Task AScriptThatThrows_ReportsItsException()
    {
        var result = await TestAsync("valueColumnExpression", "Careless", """
            using System.Collections.Generic;
            using DataSync.Scripting.Abstractions;

            public sealed class Careless : IValueColumnExpression
            {
                public IReadOnlyList<string> DeclareColumns(ValueColumnDeclarationContext c) => ["Name"];

                // No null check: the generated sample includes one, which is the point.
                public object? Evaluate(object? value, ValueColumnExpressionContext c) => ((string)value!).Substring(3);
            }
            """);

        Assert.NotNull(result.Error);
        Assert.Empty(result.Cases);
    }

    [Fact]
    public async Task AScriptThatDoesNotCompile_ReportsItsDiagnosticsRatherThanRunning()
    {
        var result = await TestAsync("sqlColumnExpression", "Broken", "this is not C#;");

        Assert.NotNull(result.Error);
        Assert.Contains("did not compile", result.Error);
    }

    [Fact]
    public async Task AScriptWhoseTypeDoesNotImplementTheSlotsContract_SaysWhichContract()
    {
        var result = await TestAsync("rowTransform", "NotATransform", """
            public sealed class NotATransform { }
            """);

        Assert.NotNull(result.Error);
        Assert.Contains("IRowTransform", result.Error);
    }

    /// <summary>
    /// The safety property, asserted rather than assumed: a live test happens only because a
    /// connection was named, and the result says which one. There is no fallback from generated to
    /// live, so nothing can query a real system without the operator having asked.
    /// </summary>
    [Fact]
    public async Task WithoutAConnection_TheResultSaysItIsGenerated()
    {
        var result = await TestAsync("sqlColumnExpression", "Nothing", """
            using DataSync.Scripting.Abstractions;
            public sealed class Nothing : ISqlColumnExpression
            {
                public string? RenderSql(SqlColumnExpressionContext c) => null;
            }
            """);

        Assert.Equal("generated", result.Mode);
        Assert.StartsWith("generated sample", result.Source);
        Assert.DoesNotContain("live", result.Source);

        // And it says which quoting it used, because with no connection chosen the identifier the
        // script sees is not necessarily the one its engine would hand it.
        Assert.Contains("no connection chosen", result.Source);
    }

    [Fact]
    public async Task ALifecycleHook_ShowsWhichPointsItWantsAndWhatItWouldEmitAtEach()
    {
        var result = await TestAsync("lifecycleHook", "Truncate", """
            using System.Collections.Generic;
            using DataSync.Drivers.Abstractions;
            using DataSync.Scripting.Abstractions;

            public sealed class Truncate : ILifecycleHook
            {
                public IReadOnlyList<string> DeclarePoints(LifecycleHookContext c) => ["beforeStage", "afterLoad"];

                public IReadOnlyList<HookStatement> BuildStatements(string point, LifecycleHookContext c) =>
                    point == "beforeStage"
                        ? [new HookStatement($"TRUNCATE TABLE {c.TargetDialect.QuoteIdentifier(c.Target.Table)};", [])]
                        : [];
            }
            """);

        Assert.True(result.Error is null, result.Error);
        var before = Assert.Single(result.Cases, c => c.Input == "beforeStage");
        Assert.Contains("TRUNCATE TABLE", before.Output!);

        // Declaring a point and emitting nothing for it is the portable spelling of "not this pass",
        // and reads as that rather than as a blank row.
        var after = Assert.Single(result.Cases, c => c.Input == "afterLoad");
        Assert.Null(after.Output);
        Assert.Contains("emits nothing", after.Note!);
    }

    [Fact]
    public async Task ASourceQueryBuilder_ShowsBothStatementsAndHowItSaysToReadTheResult()
    {
        var result = await TestAsync("sourceQueryBuilder", "Audit", """
            using DataSync.Scripting.Abstractions;

            public sealed class Audit : ISourceQueryBuilder
            {
                public SourceQuery? BuildWatermarkQuery(SourceQueryContext c) =>
                    SourceQuery.Text("SELECT MAX(seq) FROM Audit;");

                public SourceQuery BuildReadQuery(SourceQueryContext c) =>
                    new("SELECT * FROM Audit WHERE seq > @since AND seq <= @until;",
                        [new ScriptQueryParameter("since", c.PreviousWatermark), new ScriptQueryParameter("until", c.EndWatermark)]);

                public SourceQueryShape DescribeResult(SourceQueryContext c) =>
                    new(OperationColumn: "op", ExcludeColumns: ["seq", "op"]);
            }
            """);

        Assert.Null(result.Error);

        var watermark = Assert.Single(result.Cases, c => c.Input == "Watermark query");
        Assert.Equal("SELECT MAX(seq) FROM Audit;", watermark.Output);

        // Shown with a previous watermark, because the statement that matters is the incremental one —
        // a builder shown only its first-pass form is a builder half checked.
        var read = Assert.Single(result.Cases, c => c.Input == "Read query");
        Assert.Contains("seq > @since", read.Output!);
        Assert.Contains("since='1000'", read.Note!);

        var shape = Assert.Single(result.Cases, c => c.Input == "Result shape");
        Assert.Contains("op", shape.Output!);
        Assert.Contains("dropped before staging", shape.Note!);
    }

    /// <summary>
    /// Described, not run: a verification query reads from live tables, and running one because a
    /// button says Test is a choice an operator should make rather than have made for them.
    /// </summary>
    [Fact]
    public async Task AVerificationQueryBuilder_ShowsBothSidesStatementsAndTheShapeTheyComeBackIn()
    {
        var result = await TestAsync("verificationQueryBuilder", "Counts", """
            using DataSync.Scripting.Abstractions;

            public sealed class Counts : IVerificationQueryBuilder
            {
                public SourceQuery BuildQuery(VerificationQueryContext c) =>
                    SourceQuery.Text($"SELECT COUNT(*) AS n FROM {c.Dialect.QuoteIdentifier(c.Table.Table)} -- {c.Side}");

                public VerificationQueryShape DescribeResult(VerificationQueryContext c) => new([], ["n"]);
            }
            """);

        Assert.True(result.Error is null, result.Error);

        // Asked once per side, which is the point of the contract: a builder that answers the same for
        // both could have been a generic SQL check.
        var source = Assert.Single(result.Cases, c => c.Input == "Source query");
        var target = Assert.Single(result.Cases, c => c.Input == "Target query");
        Assert.Contains("-- Source", source.Output!);
        Assert.Contains("-- Target", target.Output!);

        var shape = Assert.Single(result.Cases, c => c.Input == "Result shape");
        Assert.Contains("measuring n", shape.Output!);
    }

    /// <summary>
    /// The one slot with no generated mode, and the refusal says why rather than producing something
    /// meaningless: its contract hands the script a live connection, so testing it means choosing one.
    /// </summary>
    [Fact]
    public async Task ACatalogProviderWithoutAConnection_SaysThatChoosingOneIsWhatTestingItMeans()
    {
        var name = $"test-{Guid.NewGuid():N}";
        var response = await _client.PostAsJsonAsync($"/api/scripts/{name}/test", new
        {
            script = new ScriptDefinition
            {
                Manifest = new ScriptConfig { Name = name, Kind = "metadataProvider", EntryType = "X" },
                Code = """
                    using System.Collections.Generic;
                    using System.Threading;
                    using System.Threading.Tasks;
                    using DataSync.Drivers.Abstractions;
                    using DataSync.Scripting.Abstractions;

                    public sealed class X : IMetadataProvider
                    {
                        public Task<IReadOnlyList<string>> ListDatabasesAsync(MetadataContext c, CancellationToken ct) => c.DriverDatabases(ct);
                        public Task<IReadOnlyList<TableMetadata>> ListTablesAsync(MetadataContext c, string d, CancellationToken ct) => c.DriverTables(d, ct);
                        public Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(MetadataContext c, string d, string s, string t, CancellationToken ct) => c.DriverColumns(d, s, t, ct);
                    }
                    """,
            },
        }, JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("choosing a connection", await response.Content.ReadAsStringAsync());
    }
}
