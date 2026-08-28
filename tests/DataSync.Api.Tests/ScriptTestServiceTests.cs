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
    public async Task ASlotWithNoTesterYet_SaysSoRatherThanReturningAnEmptyResult()
    {
        var name = $"test-{Guid.NewGuid():N}";
        var response = await _client.PostAsJsonAsync($"/api/scripts/{name}/test", new
        {
            script = new ScriptDefinition
            {
                Manifest = new ScriptConfig { Name = name, Kind = "metadataProvider", EntryType = "X" },
                Code = "public sealed class X { }",
            },
        }, JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("not supported yet", await response.Content.ReadAsStringAsync());
    }
}
