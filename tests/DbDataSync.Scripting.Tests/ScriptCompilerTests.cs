using DbDataSync.Core.Config;
using DbDataSync.Scripting;
using DbDataSync.Scripting.Abstractions;
using DbDataSync.Core.Sql;

namespace DbDataSync.Scripting.Tests;

/// <summary>Compiling operator-written C# — the spine of the whole scripting feature.</summary>
public sealed class ScriptCompilerTests : IDisposable
{
    private readonly string _cacheRoot = Path.Combine(Path.GetTempPath(), $"ds-script-cache-{Guid.NewGuid():N}");
    private readonly ScriptCompiler _compiler;

    public ScriptCompilerTests() => _compiler = new ScriptCompiler(new ScriptCacheDirectory(_cacheRoot));

    public void Dispose()
    {
        try { Directory.Delete(_cacheRoot, recursive: true); } catch (IOException) { }
    }

    private static ScriptDefinition Script(string code, string entryType = "Upper") => new()
    {
        Manifest = new ScriptConfig { Name = "upper", Kind = ScriptSlots.SqlColumnExpression, EntryType = entryType },
        Code = code,
    };

    private const string ValidCode = """
        using DbDataSync.Scripting.Abstractions;

        public sealed class Upper : ISqlColumnExpression
        {
            public string? RenderSql(SqlColumnExpressionContext context) =>
                $"UPPER({context.ColumnReference})";
        }
        """;

    [Fact]
    public void ValidCode_CompilesAndItsEntryTypeIsFound()
    {
        var compilation = _compiler.Compile(Script(ValidCode));

        Assert.True(compilation.Success, string.Join("; ", compilation.Diagnostics));
        Assert.Equal("Upper", compilation.EntryType!.Name);
    }

    [Fact]
    public void TheCompiledTypeCanBeConstructedAndCalled()
    {
        var expression = _compiler.Compile(Script(ValidCode)).CreateInstance<ISqlColumnExpression>("upper");

        var rendered = expression.RenderSql(new SqlColumnExpressionContext(
            "Region", "Region", null, "[Region]", new FakeDialect(), ScriptParameters.Empty));

        Assert.Equal("UPPER([Region])", rendered);
    }

    [Fact]
    public void BrokenCode_ReportsDiagnosticsWithALineNumber()
    {
        var compilation = _compiler.Compile(Script("""
            using DbDataSync.Scripting.Abstractions;

            public sealed class Upper : ISqlColumnExpression
            {
                public string? RenderSql(SqlColumnExpressionContext context) => nonsense;
            }
            """));

        Assert.False(compilation.Success);
        var diagnostic = Assert.Single(compilation.Diagnostics);
        // Line and column, because a message with neither sends the operator hunting.
        Assert.Equal(5, diagnostic.Line);
        Assert.True(diagnostic.Column > 0);
        Assert.Contains("nonsense", diagnostic.Message);
    }

    [Fact]
    public void AnAssemblyOutsideTheReferenceSet_FailsToCompile()
    {
        // The guardrail. Not a security boundary — reflection defeats it — but it turns "I will just
        // call an HTTP API from inside a transform" into a design conversation rather than a
        // production incident.
        var compilation = _compiler.Compile(Script("""
            using System.Net.Http;
            using DbDataSync.Scripting.Abstractions;

            public sealed class Upper : ISqlColumnExpression
            {
                public string? RenderSql(SqlColumnExpressionContext context)
                {
                    using var client = new HttpClient();
                    return null;
                }
            }
            """));

        Assert.False(compilation.Success);
        Assert.Contains(compilation.Diagnostics, d => d.Message.Contains("HttpClient") || d.Message.Contains("Net"));
    }

    [Fact]
    public void FileIo_IsRejectedByTheSyntaxGuardRatherThanTheReferenceSet()
    {
        // The reference set cannot do this one: System.IO.File lives in System.Private.CoreLib next to
        // string, so there is no reference set that admits one and not the other. The guard catches a
        // spelled-out qualified name, which is the shape the accident actually takes.
        var compilation = _compiler.Compile(Script("""
            using DbDataSync.Scripting.Abstractions;

            public sealed class Upper : ISqlColumnExpression
            {
                public string? RenderSql(SqlColumnExpressionContext context) =>
                    System.IO.File.ReadAllText("/etc/passwd");
            }
            """));

        Assert.False(compilation.Success);
        Assert.Contains("System.IO", Assert.Single(compilation.Diagnostics).Message);
    }

    [Fact]
    public void ABannedUsingDirective_IsRejectedWithItsLineNumber()
    {
        var compilation = _compiler.Compile(Script("""
            using System.IO;
            using DbDataSync.Scripting.Abstractions;

            public sealed class Upper : ISqlColumnExpression
            {
                public string? RenderSql(SqlColumnExpressionContext context) => null;
            }
            """));

        Assert.False(compilation.Success);
        var diagnostic = Assert.Single(compilation.Diagnostics);
        Assert.Equal(1, diagnostic.Line);
        Assert.Contains("does not read files", diagnostic.Message);
    }

    [Fact]
    public void AScriptOfTheOperatorsOwnNamedLikeABannedType_IsNotFalselyRejected()
    {
        // The guard matches namespaces, not identifiers. Someone's own `File` class is their business.
        var compilation = _compiler.Compile(Script("""
            using DbDataSync.Scripting.Abstractions;

            public sealed class File { public static string Name => "ok"; }

            public sealed class Upper : ISqlColumnExpression
            {
                public string? RenderSql(SqlColumnExpressionContext context) => File.Name;
            }
            """));

        Assert.True(compilation.Success, string.Join("; ", compilation.Diagnostics));
    }

    [Fact]
    public void AMissingEntryType_SaysWhatWasActuallyThere()
    {
        var compilation = _compiler.Compile(Script(ValidCode, entryType: "NotThisOne"));

        Assert.False(compilation.Success);
        var message = Assert.Single(compilation.Diagnostics).Message;
        Assert.Contains("NotThisOne", message);
        Assert.Contains("Upper", message);
    }

    [Fact]
    public void AWrongContract_FailsWhenTheInstanceIsAskedFor()
    {
        var compilation = _compiler.Compile(Script("public sealed class Upper { }"));

        var ex = Assert.Throws<ScriptExecutionException>(
            () => compilation.CreateInstance<ISqlColumnExpression>("upper"));
        Assert.Contains(nameof(ISqlColumnExpression), ex.Message);
    }

    [Fact]
    public void EmptyCode_IsAFailureRatherThanAnEmptyAssembly()
    {
        Assert.False(_compiler.Compile(Script("")).Success);
    }

    [Fact]
    public void CompilingTheSameSourceTwice_ComesBackFromTheDiskCache()
    {
        // The cache is what makes this usable from the TaskRunner, which is a process per run and would
        // otherwise pay Roslyn's start-up cost on every fifteen-second pass.
        Assert.True(_compiler.Compile(Script(ValidCode)).Success);

        var cached = Directory.GetFiles(_cacheRoot, "*.dll");
        Assert.Single(cached);

        var second = _compiler.Compile(Script(ValidCode));
        Assert.True(second.Success);
        Assert.Single(Directory.GetFiles(_cacheRoot, "*.dll"));
    }

    [Fact]
    public void ChangingTheEntryType_IsADifferentCacheEntryEvenWithIdenticalCode()
    {
        const string twoTypes = """
            using DbDataSync.Scripting.Abstractions;

            public sealed class Upper : ISqlColumnExpression
            {
                public string? RenderSql(SqlColumnExpressionContext c) => $"UPPER({c.ColumnReference})";
            }

            public sealed class Lower : ISqlColumnExpression
            {
                public string? RenderSql(SqlColumnExpressionContext c) => $"LOWER({c.ColumnReference})";
            }
            """;

        Assert.NotEqual(
            ScriptCompiler.HashOf(twoTypes, "Upper"),
            ScriptCompiler.HashOf(twoTypes, "Lower"));
    }

    private sealed class FakeDialect : IScriptDialect
    {
        public string EngineName => "MsSql";
        public string QuoteIdentifier(string identifier) => $"[{identifier}]";
        public string ParameterReference(string name) => $"@{name}";
        public DbDataSync.Core.Sql.CanonicalType ToCanonicalType(string nativeType) => throw new NotSupportedException();
        public DbDataSync.Core.Sql.RenderedColumnType RenderColumnType(DbDataSync.Core.Sql.CanonicalType type) => throw new NotSupportedException();
    }

    /// <summary>
    /// A script must be able to read what its contract hands it. <c>ColumnMapping</c>,
    /// <c>SourceTableRef</c> and <c>TableRef</c> live in DbDataSync.Core, and until phase 41 that
    /// assembly was not in the reference set — so a lifecycle hook could implement the interface and
    /// not touch <c>context.Target</c>, which is exactly what phase 27's motivating example does.
    /// Nothing had run one, so nothing had found out.
    /// </summary>
    [Fact]
    public void AScriptCanReadTheConfigTypesItsContractHandsIt()
    {
        var result = _compiler.Compile(new ScriptDefinition
        {
            Manifest = new ScriptConfig
            {
                Name = "reads-its-context", Kind = ScriptSlots.LifecycleHook, EntryType = "ReadsItsContext",
            },
            Code = """
                using System.Collections.Generic;
                using System.Linq;
                using DbDataSync.Drivers.Abstractions;
                using DbDataSync.Scripting.Abstractions;

                public sealed class ReadsItsContext : ILifecycleHook
                {
                    public IReadOnlyList<string> DeclarePoints(LifecycleHookContext c) => ["beforeStage"];

                    public IReadOnlyList<HookStatement> BuildStatements(string point, LifecycleHookContext c)
                    {
                        // Every config type a contract exposes: the mapping list, and both table refs.
                        var mapped = c.ColumnMappings.Select(m => m.TargetColumn).ToList();
                        var from = $"{c.Source.Schema}.{c.Source.Table}";
                        var to = $"{c.Target.Schema}.{c.Target.Table}";
                        return [new HookStatement($"-- {mapped.Count} column(s), {from} -> {to}", [])];
                    }
                }
                """,
        });

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
    }
}
