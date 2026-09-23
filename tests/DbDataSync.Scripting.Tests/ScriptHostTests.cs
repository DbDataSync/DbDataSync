using DbDataSync.Core.Config;
using DbDataSync.Scripting.Abstractions;

namespace DbDataSync.Scripting.Tests;

/// <summary>
/// <see cref="ScriptHost"/>'s own in-memory compile cache — separate from <see cref="ScriptCompilerTests"/>,
/// which drives <see cref="ScriptCompiler"/> directly and never sees this layer at all.
/// <para>
/// The bug this locks in: a name-keyed cache returned the same compiled script forever once resolved
/// once, so an operator who edited and re-saved a bound script kept seeing the old behavior — in both
/// Preview and a real run, since both resolve through this same class — until the whole process was
/// restarted. Content-hash keying (<see cref="ScriptConfig.ContentHash"/>, computed once by
/// <c>ConfigRepository.SaveScript</c>) is what these tests hold in place.
/// </para>
/// </summary>
public sealed class ScriptHostTests : IDisposable
{
    private readonly string _repoRoot = Path.Combine(Path.GetTempPath(), $"ds-sh-{Guid.NewGuid():N}");
    private readonly ConfigRepository _configRepository;
    private readonly ScriptHost _host;

    public ScriptHostTests()
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

    public void Dispose() => GitTempDirectory.DeleteRecursively(_repoRoot);

    private static string Code(string upperOrLower) => $$"""
        using DbDataSync.Scripting.Abstractions;

        public sealed class Expr : ISqlColumnExpression
        {
            public string? RenderSql(SqlColumnExpressionContext c) => $"{{upperOrLower}}({c.ColumnReference})";
        }
        """;

    private void Save(string name, string code, bool enabled = true) => _configRepository.SaveScript(
        new ScriptDefinition
        {
            Manifest = new ScriptConfig { Name = name, Kind = ScriptSlots.SqlColumnExpression, EntryType = "Expr", Enabled = enabled },
            Code = code,
        },
        new Core.Git.GitAuthor("t", "t@t"));

    private string Render(string name) => _host.Resolve<ISqlColumnExpression>(name).RenderSql(
        new SqlColumnExpressionContext("Region", "Region", null, "[Region]", new FakeDialect(), ScriptParameters.Empty))!;

    [Fact]
    public void EditingAndResavingABoundScript_ChangesWhatTheNextResolveReturns()
    {
        Save("expr", Code("UPPER"));
        Assert.Equal("UPPER([Region])", Render("expr"));

        Save("expr", Code("LOWER"));

        Assert.Equal(
            "LOWER([Region])",
            Render("expr"));
    }

    [Fact]
    public void TwoScriptsWithDifferentContent_AreCachedSeparately_EvenAfterOneIsEdited()
    {
        Save("a", Code("UPPER"));
        Save("b", Code("LOWER"));
        Assert.Equal("UPPER([Region])", Render("a"));
        Assert.Equal("LOWER([Region])", Render("b"));

        Save("a", Code("LOWER"));

        Assert.Equal("LOWER([Region])", Render("a"));
        Assert.Equal("LOWER([Region])", Render("b"));
    }

    [Fact]
    public void DisablingABoundScript_IsSeenOnTheVeryNextResolve()
    {
        Save("expr", Code("UPPER"));
        Assert.Equal("UPPER([Region])", Render("expr"));

        Save("expr", Code("UPPER"), enabled: false);

        var ex = Assert.Throws<ScriptExecutionException>(() => Render("expr"));
        Assert.Contains("disabled", ex.Message);
    }

    [Fact]
    public void ReenablingABoundScript_IsSeenOnTheVeryNextResolve()
    {
        Save("expr", Code("UPPER"), enabled: false);
        Assert.Throws<ScriptExecutionException>(() => Render("expr"));

        Save("expr", Code("UPPER"), enabled: true);

        Assert.Equal("UPPER([Region])", Render("expr"));
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
