using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;
using DataSync.Scripting;
using DataSync.Scripting.Abstractions;
using DataSync.Core.Sql;

namespace DataSync.Scripting.Tests;

/// <summary>
/// A compiled script generating source-dialect SQL, folded into the mapping's own
/// <see cref="ColumnMapping.Transform"/> — which is what makes everything downstream, including the
/// <c>{{column}}</c> substitution, phase 22 unchanged.
/// </summary>
public sealed class ScriptedColumnTransformTests : IDisposable
{
    private readonly string _cacheRoot = Path.Combine(Path.GetTempPath(), $"ds-xf-{Guid.NewGuid():N}");
    private readonly ScriptCompiler _compiler;

    public ScriptedColumnTransformTests() => _compiler = new ScriptCompiler(new ScriptCacheDirectory(_cacheRoot));

    public void Dispose()
    {
        try { Directory.Delete(_cacheRoot, recursive: true); } catch (IOException) { }
    }

    private ISqlColumnExpression Compile(string code, string entryType) =>
        _compiler.Compile(new ScriptDefinition
        {
            Manifest = new ScriptConfig { Name = "x", Kind = ScriptSlots.SqlColumnExpression, EntryType = entryType },
            Code = code,
        }).CreateInstance<ISqlColumnExpression>("x");

    private static ColumnMapping Map(string source, string? transform = null) =>
        new() { SourceColumn = source, TargetColumn = source, Transform = transform };

    private static IReadOnlyList<ColumnMapping> Apply(
        IReadOnlyList<ColumnMapping> mappings, ISqlColumnExpression expression,
        ScriptParameters? parameters = null, IReadOnlyList<ColumnMetadata>? metadata = null) =>
        ScriptedColumnTransforms.Apply(
            mappings, expression, parameters ?? ScriptParameters.Empty, new FakeDialect(), metadata);

    private const string UpperEverything = """
        using DataSync.Scripting.Abstractions;

        public sealed class Upper : ISqlColumnExpression
        {
            public string? RenderSql(SqlColumnExpressionContext c) => $"UPPER({c.ColumnReference})";
        }
        """;

    [Fact]
    public void TheGeneratedExpressionLandsInTransform_InTheFormAHumanWouldType()
    {
        // The script is handed {{column}} rather than a resolved reference, so what it emits is exactly
        // what someone would have typed into the mapping editor — and the reader substitutes its own
        // qualification, which is the only thing that makes it correct where the statement aliases the
        // source table.
        var result = Apply([Map("Region")], Compile(UpperEverything, "Upper"));

        Assert.Equal("UPPER({{column}})", Assert.Single(result).Transform);
    }

    [Fact]
    public void ALiteralTransformWins()
    {
        var result = Apply([Map("Region", "LOWER({{column}})")], Compile(UpperEverything, "Upper"));

        Assert.Equal("LOWER({{column}})", Assert.Single(result).Transform);
    }

    [Fact]
    public void AScriptReturningNull_LeavesTheColumnAlone_AndTheListIsNotEvenCopied()
    {
        var mappings = new List<ColumnMapping> { Map("Region") };
        var result = Apply(mappings, Compile("""
            using DataSync.Scripting.Abstractions;
            public sealed class None : ISqlColumnExpression
            {
                public string? RenderSql(SqlColumnExpressionContext c) => null;
            }
            """, "None"));

        Assert.Same(mappings, result);
    }

    [Fact]
    public void NoScriptBound_IsTheSameListBack()
    {
        var mappings = new List<ColumnMapping> { Map("Region") };
        Assert.Same(mappings, ScriptedColumnTransforms.Apply(mappings, null, ScriptParameters.Empty, new FakeDialect()));
    }

    [Fact]
    public void AScriptSeesParametersAndCanBranchOnTheColumn()
    {
        var result = Apply(
            [Map("Code"), Map("Region")],
            Compile("""
                using DataSync.Scripting.Abstractions;

                public sealed class Pad : ISqlColumnExpression
                {
                    public string? RenderSql(SqlColumnExpressionContext c)
                    {
                        if (c.SourceColumn != "Code") return null;
                        var width = c.Parameters.GetInt("width", 8);
                        return $"RIGHT(REPLICATE('0', {width}) + {c.ColumnReference}, {width})";
                    }
                }
                """, "Pad"),
            new ScriptParameters(new Dictionary<string, string> { ["width"] = "10" }));

        Assert.Equal("RIGHT(REPLICATE('0', 10) + {{column}}, 10)", result[0].Transform);
        Assert.Null(result[1].Transform);
    }

    [Fact]
    public void AScriptCanBranchOnTheEngine()
    {
        var result = Apply([Map("Region")], Compile("""
            using DataSync.Scripting.Abstractions;

            public sealed class PerEngine : ISqlColumnExpression
            {
                public string? RenderSql(SqlColumnExpressionContext c) =>
                    c.Dialect.EngineName == "Postgres"
                        ? $"{c.ColumnReference}::text"
                        : $"CAST({c.ColumnReference} AS nvarchar(max))";
            }
            """, "PerEngine"));

        Assert.Equal("CAST({{column}} AS nvarchar(max))", Assert.Single(result).Transform);
    }

    [Fact]
    public void AScriptSeesColumnMetadataWhenTheCallerHasIt()
    {
        var result = Apply(
            [Map("Amount")],
            Compile("""
                using DataSync.Scripting.Abstractions;

                public sealed class ByType : ISqlColumnExpression
                {
                    public string? RenderSql(SqlColumnExpressionContext c) =>
                        c.Column is null ? null : $"/* {c.Column.NativeType} */ {c.ColumnReference}";
                }
                """, "ByType"),
            metadata: [new ColumnMetadata("Amount", "decimal(18,2)", IsNullable: false, IsPrimaryKey: false, IsIdentity: false)]);

        Assert.Equal("/* decimal(18,2) */ {{column}}", Assert.Single(result).Transform);
    }

    [Fact]
    public void AScriptThatThrows_FailsNamingTheColumnItWasWorkingOn()
    {
        var ex = Assert.Throws<ScriptExecutionException>(() => Apply([Map("Region")], Compile("""
            using DataSync.Scripting.Abstractions;

            public sealed class Boom : ISqlColumnExpression
            {
                public string? RenderSql(SqlColumnExpressionContext c) => throw new System.InvalidOperationException("nope");
            }
            """, "Boom")));

        Assert.Contains("Region", ex.Message);
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public void AScriptDemandingAMissingRequiredParameter_SaysWhichOne()
    {
        var ex = Assert.Throws<ScriptExecutionException>(() => Apply([Map("Region")], Compile("""
            using DataSync.Scripting.Abstractions;

            public sealed class NeedsIt : ISqlColumnExpression
            {
                public string? RenderSql(SqlColumnExpressionContext c) => c.Parameters.Require("width");
            }
            """, "NeedsIt")));

        Assert.Contains("width", ex.Message);
    }

    private sealed class FakeDialect : IScriptDialect
    {
        public string EngineName => "MsSql";
        public string QuoteIdentifier(string identifier) => $"[{identifier}]";
        public string ParameterReference(string name) => $"@{name}";
        public DataSync.Core.Sql.CanonicalType ToCanonicalType(string nativeType) => throw new NotSupportedException();
        public DataSync.Core.Sql.RenderedColumnType RenderColumnType(DataSync.Core.Sql.CanonicalType type) => throw new NotSupportedException();
    }
}
