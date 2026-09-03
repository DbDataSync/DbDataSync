using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Scripting;
using DbDataSync.Scripting.Abstractions;
using DbDataSync.Core.Sql;

namespace DbDataSync.Scripting.Tests;

/// <summary>
/// The in-process half of the transform story: values per cell, and whole rows, between the reader and
/// staging. Driven through genuinely compiled scripts, because the point of the slot is that operator
/// C# runs here.
/// </summary>
public sealed class TransformPipelineTests : IDisposable
{
    private readonly string _repoRoot = Path.Combine(Path.GetTempPath(), $"ds-tp-{Guid.NewGuid():N}");
    private readonly ScriptHost _host;
    private readonly ConfigRepository _configRepository;
    private readonly List<string> _log = [];

    public TransformPipelineTests()
    {
        Directory.CreateDirectory(_repoRoot);
        LibGit2Sharp.Repository.Init(_repoRoot);
        _configRepository = new ConfigRepository(
            Path.Combine(_repoRoot, "config"),
            new Core.Git.GitCommitService(_repoRoot),
            new ClrKernel.Core.Secrets.SecretStore(true));
        _host = new ScriptHost(
            _configRepository, new ScriptCompiler(new ScriptCacheDirectory(Path.Combine(_repoRoot, "cache"))));
    }

    public void Dispose()
    {
        try { Directory.Delete(_repoRoot, recursive: true); } catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void Register(string name, string kind, string entryType, string code) =>
        _configRepository.SaveScript(
            new ScriptDefinition
            {
                Manifest = new ScriptConfig { Name = name, Kind = kind, EntryType = entryType },
                Code = code,
            },
            new Core.Git.GitAuthor("t", "t@t"));

    private static readonly List<ColumnMapping> Mappings =
    [
        new() { SourceColumn = "Id", TargetColumn = "Id" },
        new() { SourceColumn = "Name", TargetColumn = "Name" },
        new() { SourceColumn = "Region", TargetColumn = "Region" },
    ];

    private static readonly ChangeSchema Schema = new(["Id", "Name", "Region"]);

    private static async IAsyncEnumerable<ChangeRow> Rows(params ChangeRow[] rows)
    {
        foreach (var row in rows)
            yield return await ValueTask.FromResult(row);
    }

    private static ChangeRow Row(ChangeOperation op, int id, string? name, string? region) =>
        new(op, Schema, [id, name, region]);

    private TableMappingConfig MappingBinding(params (string Slot, string Script)[] bindings)
    {
        var mapping = new TableMappingConfig
        {
            Name = "m", Sources = [new SourceTableSpec { Table = "T" }], Targets = [new TableSpec { Table = "T" }],
        };
        foreach (var (slot, script) in bindings)
            mapping.Scripts[slot] = new ScriptBinding { ScriptName = script };
        return mapping;
    }

    private TransformPipeline Build(TableMappingConfig mapping) =>
        TransformPipeline.Build(_host, null, null, mapping, Mappings, new FakeDialect(), _log.Add);

    private static async Task<List<ChangeRow>> CollectAsync(IAsyncEnumerable<ChangeRow> rows)
    {
        var list = new List<ChangeRow>();
        await foreach (var row in rows)
            list.Add(row);
        return list;
    }

    [Fact]
    public void NothingBound_IsAnEmptyPipeline()
    {
        Assert.True(Build(MappingBinding()).IsEmpty);
    }

    [Fact]
    public async Task AValueExpression_TransformsOnlyTheColumnsItDeclares()
    {
        Register("upper", ScriptSlots.ValueColumnExpression, "Upper", """
            using System.Collections.Generic;
            using DbDataSync.Scripting.Abstractions;

            public sealed class Upper : IValueColumnExpression
            {
                public IReadOnlyList<string> DeclareColumns(ValueColumnDeclarationContext c) => new[] { "Name" };
                public object? Evaluate(object? value, ValueColumnExpressionContext c) =>
                    value is string s ? s.ToUpperInvariant() : value;
            }
            """);

        var result = await CollectAsync(Build(MappingBinding((ScriptSlots.ValueColumnExpression, "upper")))
            .ApplyAsync(Rows(Row(ChangeOperation.Insert, 1, "widget", "eu"))));

        var row = Assert.Single(result);
        Assert.Equal("WIDGET", row["Name"]);
        // Declared "Name" and nothing else, so Region was never handed to the script.
        Assert.Equal("eu", row["Region"]);
    }

    [Fact]
    public async Task AnUndeclaredColumn_IsNeverPassedToTheScript()
    {
        // The performance property. Without DeclareColumns, a transform interested in one column of
        // forty costs a delegate call on all forty for every row — and losing it later would be silent.
        Register("spy", ScriptSlots.ValueColumnExpression, "Spy", """
            using System.Collections.Generic;
            using DbDataSync.Scripting.Abstractions;

            public sealed class Spy : IValueColumnExpression
            {
                public IReadOnlyList<string> DeclareColumns(ValueColumnDeclarationContext c) => new[] { "Name" };
                public object? Evaluate(object? value, ValueColumnExpressionContext c) =>
                    c.SourceColumn == "Name" ? value : "SHOULD NEVER HAPPEN";
            }
            """);

        var result = await CollectAsync(Build(MappingBinding((ScriptSlots.ValueColumnExpression, "spy")))
            .ApplyAsync(Rows(Row(ChangeOperation.Insert, 1, "widget", "eu"))));

        Assert.DoesNotContain(Assert.Single(result).Values, v => Equals(v, "SHOULD NEVER HAPPEN"));
    }

    [Fact]
    public async Task Deletes_SkipValueExpressions()
    {
        // A delete carries only its key; every other slot is null, and the writers key off the
        // operation rather than the values.
        Register("marker", ScriptSlots.ValueColumnExpression, "Marker", """
            using System.Collections.Generic;
            using DbDataSync.Scripting.Abstractions;

            public sealed class Marker : IValueColumnExpression
            {
                public IReadOnlyList<string> DeclareColumns(ValueColumnDeclarationContext c) => new[] { "Name" };
                public object? Evaluate(object? value, ValueColumnExpressionContext c) => "TOUCHED";
            }
            """);

        var result = await CollectAsync(Build(MappingBinding((ScriptSlots.ValueColumnExpression, "marker")))
            .ApplyAsync(Rows(Row(ChangeOperation.Delete, 1, null, null), Row(ChangeOperation.Insert, 2, "x", "eu"))));

        Assert.Null(result[0]["Name"]);
        Assert.Equal("TOUCHED", result[1]["Name"]);
    }

    [Fact]
    public async Task ARowTransform_CanDropARow()
    {
        Register("no-eu", ScriptSlots.RowTransform, "NoEu", """
            using System.Threading;
            using System.Threading.Tasks;
            using DbDataSync.Drivers.Abstractions;
            using DbDataSync.Scripting.Abstractions;

            public sealed class NoEu : IRowTransform
            {
                public ChangeSchema DeclareSchema(ChangeSchema input, RowTransformContext c) => input;

                public ValueTask<ChangeRow?> TransformAsync(ChangeRow row, RowTransformContext c, CancellationToken ct) =>
                    ValueTask.FromResult<ChangeRow?>(Equals(row["Region"], "eu") ? null : row);
            }
            """);

        long dropped = 0;
        var result = await CollectAsync(Build(MappingBinding((ScriptSlots.RowTransform, "no-eu")))
            .ApplyAsync(
                Rows(Row(ChangeOperation.Insert, 1, "a", "eu"), Row(ChangeOperation.Insert, 2, "b", "us")),
                n => dropped = n));

        Assert.Equal(2, Assert.Single(result)["Id"]);
        // Rows read and rows staged diverging is correct when something filtered, and invisible unless
        // someone says so.
        Assert.Equal(1, dropped);
    }

    [Fact]
    public async Task ARowTransform_SeesDeletes()
    {
        Register("drop-deletes", ScriptSlots.RowTransform, "DropDeletes", """
            using System.Threading;
            using System.Threading.Tasks;
            using DbDataSync.Drivers.Abstractions;
            using DbDataSync.Scripting.Abstractions;

            public sealed class DropDeletes : IRowTransform
            {
                public ChangeSchema DeclareSchema(ChangeSchema input, RowTransformContext c) => input;

                public ValueTask<ChangeRow?> TransformAsync(ChangeRow row, RowTransformContext c, CancellationToken ct) =>
                    ValueTask.FromResult<ChangeRow?>(row.Operation == ChangeOperation.Delete ? null : row);
            }
            """);

        var result = await CollectAsync(Build(MappingBinding((ScriptSlots.RowTransform, "drop-deletes")))
            .ApplyAsync(Rows(Row(ChangeOperation.Delete, 1, null, null), Row(ChangeOperation.Insert, 2, "b", "us"))));

        Assert.Equal(2, Assert.Single(result)["Id"]);
    }

    [Fact]
    public async Task ARowTransform_CanRewriteValuesAndWriteToTheRunLog()
    {
        Register("shout", ScriptSlots.RowTransform, "Shout", """
            using System.Threading;
            using System.Threading.Tasks;
            using DbDataSync.Drivers.Abstractions;
            using DbDataSync.Scripting.Abstractions;

            public sealed class Shout : IRowTransform
            {
                public ChangeSchema DeclareSchema(ChangeSchema input, RowTransformContext c)
                {
                    c.Log("declaring schema");
                    return input;
                }

                public ValueTask<ChangeRow?> TransformAsync(ChangeRow row, RowTransformContext c, CancellationToken ct)
                {
                    var values = (object?[])row.Values.Clone();
                    values[row.Schema.GetOrdinal("Name")] = "!";
                    return ValueTask.FromResult<ChangeRow?>(row with { Values = values });
                }
            }
            """);

        var result = await CollectAsync(Build(MappingBinding((ScriptSlots.RowTransform, "shout")))
            .ApplyAsync(Rows(Row(ChangeOperation.Insert, 1, "a", "eu"))));

        Assert.Equal("!", Assert.Single(result)["Name"]);
        // The script's own output lands in the run log, where whoever is debugging it is looking.
        Assert.Contains("declaring schema", _log);
    }

    [Fact]
    public async Task BothSlots_RunInOrder_ValuesThenTheRow()
    {
        Register("upper", ScriptSlots.ValueColumnExpression, "Upper", """
            using System.Collections.Generic;
            using DbDataSync.Scripting.Abstractions;

            public sealed class Upper : IValueColumnExpression
            {
                public IReadOnlyList<string> DeclareColumns(ValueColumnDeclarationContext c) => new[] { "Name" };
                public object? Evaluate(object? value, ValueColumnExpressionContext c) =>
                    value is string s ? s.ToUpperInvariant() : value;
            }
            """);
        Register("keep-upper", ScriptSlots.RowTransform, "KeepUpper", """
            using System.Threading;
            using System.Threading.Tasks;
            using DbDataSync.Drivers.Abstractions;
            using DbDataSync.Scripting.Abstractions;

            public sealed class KeepUpper : IRowTransform
            {
                public ChangeSchema DeclareSchema(ChangeSchema input, RowTransformContext c) => input;

                // Only true if the value expression already ran.
                public ValueTask<ChangeRow?> TransformAsync(ChangeRow row, RowTransformContext c, CancellationToken ct) =>
                    ValueTask.FromResult<ChangeRow?>(Equals(row["Name"], "WIDGET") ? row : null);
            }
            """);

        var result = await CollectAsync(
            Build(MappingBinding((ScriptSlots.ValueColumnExpression, "upper"), (ScriptSlots.RowTransform, "keep-upper")))
                .ApplyAsync(Rows(Row(ChangeOperation.Insert, 1, "widget", "eu"))));

        Assert.Single(result);
    }

    [Fact]
    public async Task AScriptThatThrows_FailsNamingWhatItWasDoing()
    {
        Register("boom", ScriptSlots.ValueColumnExpression, "Boom", """
            using System.Collections.Generic;
            using DbDataSync.Scripting.Abstractions;

            public sealed class Boom : IValueColumnExpression
            {
                public IReadOnlyList<string> DeclareColumns(ValueColumnDeclarationContext c) => new[] { "Name" };
                public object? Evaluate(object? value, ValueColumnExpressionContext c) =>
                    throw new System.InvalidOperationException("nope");
            }
            """);

        var ex = await Assert.ThrowsAsync<ScriptExecutionException>(async () =>
            await CollectAsync(Build(MappingBinding((ScriptSlots.ValueColumnExpression, "boom")))
                .ApplyAsync(Rows(Row(ChangeOperation.Insert, 1, "a", "eu")))));

        Assert.Contains("Name", ex.Message);
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public async Task AScriptDeclaringNothing_NeverRuns()
    {
        Register("idle", ScriptSlots.ValueColumnExpression, "Idle", """
            using System.Collections.Generic;
            using DbDataSync.Scripting.Abstractions;

            public sealed class Idle : IValueColumnExpression
            {
                public IReadOnlyList<string> DeclareColumns(ValueColumnDeclarationContext c) => new string[0];
                public object? Evaluate(object? value, ValueColumnExpressionContext c) => "NEVER";
            }
            """);

        var pipeline = Build(MappingBinding((ScriptSlots.ValueColumnExpression, "idle")));
        Assert.True(pipeline.IsEmpty);

        var result = await CollectAsync(pipeline.ApplyAsync(Rows(Row(ChangeOperation.Insert, 1, "a", "eu"))));
        Assert.Equal("a", Assert.Single(result)["Name"]);
    }

    [Fact]
    public async Task ADeclaredColumnTheReaderDidNotReturn_IsIgnoredRatherThanFatal()
    {
        // A mapping's projection may legitimately not carry it, and failing the run over a transform
        // that has nothing to transform would be worse than doing nothing.
        Register("absent", ScriptSlots.ValueColumnExpression, "Absent", """
            using System.Collections.Generic;
            using DbDataSync.Scripting.Abstractions;

            public sealed class Absent : IValueColumnExpression
            {
                public IReadOnlyList<string> DeclareColumns(ValueColumnDeclarationContext c) => new[] { "Name" };
                public object? Evaluate(object? value, ValueColumnExpressionContext c) => "X";
            }
            """);

        var narrow = new ChangeSchema(["Id"]);
        var result = await CollectAsync(Build(MappingBinding((ScriptSlots.ValueColumnExpression, "absent")))
            .ApplyAsync(Rows(new ChangeRow(ChangeOperation.Insert, narrow, [1]))));

        Assert.Equal(1, Assert.Single(result)["Id"]);
    }

    private sealed class FakeDialect : IScriptDialect
    {
        public string EngineName => "MsSql";
        public string QuoteIdentifier(string identifier) => $"[{identifier}]";
        public string ParameterReference(string name) => $"@{name}";
        public DbDataSync.Core.Sql.CanonicalType ToCanonicalType(string nativeType) => throw new NotSupportedException();
        public DbDataSync.Core.Sql.RenderedColumnType RenderColumnType(DbDataSync.Core.Sql.CanonicalType type) => throw new NotSupportedException();
    }
}
