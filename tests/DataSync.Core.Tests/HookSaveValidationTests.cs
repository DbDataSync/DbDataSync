using ClrKernel.Core.Secrets;
using DataSync.Core.Config;
using DataSync.Core.Git;
using LibGit2Sharp;

namespace DataSync.Core.Tests;

/// <summary>
/// "Referencing an unavailable token or parameter is a validation error at save time — naming the
/// point and what is available there — not a null at 3am." Exercised through <see cref="ConfigRepository"/>
/// rather than <see cref="HookValidation"/> directly, because a named-hook reference also has to
/// resolve the script it points at, which only the repository can do.
/// </summary>
public sealed class HookSaveValidationTests : IDisposable
{
    private readonly string _repoRoot;
    private readonly ConfigRepository _repository;
    private static readonly GitAuthor Author = new("Test User", "test@example.com");

    public HookSaveValidationTests()
    {
        _repoRoot = Directory.CreateTempSubdirectory("datasync-hook-tests-").FullName;
        Repository.Init(_repoRoot);
        var configRoot = Path.Combine(_repoRoot, "config");
        _repository = new ConfigRepository(configRoot, new GitCommitService(_repoRoot), SecretStore.ForProviders([new InMemorySecretProvider()]));
    }

    public void Dispose() => Directory.Delete(_repoRoot, recursive: true);

    private ConnectionInput Connection(Dictionary<string, List<HookConfig>?> hooks) => new()
    {
        Name = "tgt", DriverType = ConnectionDriverType.MsSql, Host = "h", AuthMode = AuthMode.IntegratedAuth, Hooks = hooks,
    };

    [Fact]
    public void AValidInlineHook_Saves()
    {
        var config = _repository.SaveConnection(Connection(new()
        {
            [HookPoints.BeforeLoad] = [new HookConfig { Name = "disable-indexes", Sql = "ALTER INDEX ALL ON {{target}} DISABLE;" }],
        }), Author);

        Assert.Single(config.Hooks[HookPoints.BeforeLoad]!);
    }

    [Fact]
    public void AnUnknownPoint_Throws()
    {
        var ex = Assert.Throws<ConfigValidationException>(() => _repository.SaveConnection(Connection(new()
        {
            ["notAPoint"] = [new HookConfig { Sql = "SELECT 1;" }],
        }), Author));

        Assert.Contains("notAPoint", ex.Message);
    }

    [Fact]
    public void AReferenceUnavailableAtThePoint_ThrowsNamingThePointAndTheReference()
    {
        var ex = Assert.Throws<ConfigValidationException>(() => _repository.SaveConnection(Connection(new()
        {
            [HookPoints.BeforeStage] = [new HookConfig { Sql = "SELECT @rowsWritten;" }],
        }), Author));

        Assert.Contains("rowsWritten", ex.Message);
        Assert.Contains(HookPoints.BeforeStage, ex.Message);
    }

    [Fact]
    public void SettingBothSqlAndHook_Throws()
    {
        var ex = Assert.Throws<ConfigValidationException>(() => _repository.SaveConnection(Connection(new()
        {
            [HookPoints.AfterLoad] = [new HookConfig { Sql = "SELECT 1;", Hook = "record-load" }],
        }), Author));

        Assert.Contains("both", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SettingNeitherSqlNorHook_Throws()
    {
        Assert.Throws<ConfigValidationException>(() => _repository.SaveConnection(Connection(new()
        {
            [HookPoints.AfterLoad] = [new HookConfig()],
        }), Author));
    }

    [Fact]
    public void ANamedHookReferencingAnUnknownScript_Throws()
    {
        var ex = Assert.Throws<ConfigValidationException>(() => _repository.SaveConnection(Connection(new()
        {
            [HookPoints.AfterLoad] = [new HookConfig { Hook = "ghost" }],
        }), Author));

        Assert.Contains("ghost", ex.Message);
    }

    [Fact]
    public void ANamedHookReferencingACSharpScript_Throws()
    {
        _repository.SaveScript(new ScriptDefinition
        {
            Manifest = new ScriptConfig { Name = "not-sql", Kind = "sqlColumnExpression", Language = ScriptLanguage.CSharp, EntryType = "X" },
            Code = "public sealed class X : DataSync.Scripting.Abstractions.ISqlColumnExpression { public string? RenderSql(DataSync.Scripting.Abstractions.SqlColumnExpressionContext c) => null; }",
        }, Author);

        var ex = Assert.Throws<ConfigValidationException>(() => _repository.SaveConnection(Connection(new()
        {
            [HookPoints.AfterLoad] = [new HookConfig { Hook = "not-sql" }],
        }), Author));

        Assert.Contains("not-sql", ex.Message);
    }

    [Fact]
    public void ANamedHookMissingARequiredParameter_Throws()
    {
        _repository.SaveScript(new ScriptDefinition
        {
            Manifest = new ScriptConfig
            {
                Name = "record-load", Kind = ScriptSlotHook, Language = ScriptLanguage.Sql,
                Parameters = [new ScriptParameterDeclaration { Name = "controlTable", Required = true }],
            },
            Code = "INSERT INTO {{controlTable}} (Mapping) VALUES (@mapping);",
        }, Author);

        var ex = Assert.Throws<ConfigValidationException>(() => _repository.SaveConnection(Connection(new()
        {
            [HookPoints.AfterLoad] = [new HookConfig { Hook = "record-load" }],
        }), Author));

        Assert.Contains("controlTable", ex.Message);
    }

    [Fact]
    public void ANamedHookSupplyingAnUndeclaredParameter_Throws()
    {
        _repository.SaveScript(new ScriptDefinition
        {
            Manifest = new ScriptConfig
            {
                Name = "record-load", Kind = ScriptSlotHook, Language = ScriptLanguage.Sql,
                Parameters = [new ScriptParameterDeclaration { Name = "controlTable", Required = true }],
            },
            Code = "INSERT INTO {{controlTable}} (Mapping) VALUES (@mapping);",
        }, Author);

        var ex = Assert.Throws<ConfigValidationException>(() => _repository.SaveConnection(Connection(new()
        {
            [HookPoints.AfterLoad] = [new HookConfig
            {
                Hook = "record-load",
                Parameters = { ["controlTable"] = "dbo.LoadControl", ["extra"] = "x" },
            }],
        }), Author));

        Assert.Contains("extra", ex.Message);
    }

    [Fact]
    public void AWellFormedNamedHookBinding_Saves()
    {
        _repository.SaveScript(new ScriptDefinition
        {
            Manifest = new ScriptConfig
            {
                Name = "record-load", Kind = ScriptSlotHook, Language = ScriptLanguage.Sql,
                Parameters = [new ScriptParameterDeclaration { Name = "controlTable", Required = true }],
            },
            Code = "INSERT INTO {{controlTable}} (Mapping, RowsWritten) VALUES (@mapping, @rowsWritten);",
        }, Author);

        var config = _repository.SaveConnection(Connection(new()
        {
            [HookPoints.AfterLoad] = [new HookConfig { Hook = "record-load", Parameters = { ["controlTable"] = "dbo.LoadControl" } }],
        }), Author);

        Assert.Single(config.Hooks[HookPoints.AfterLoad]!);
    }

    private const string ScriptSlotHook = "hook";
}
