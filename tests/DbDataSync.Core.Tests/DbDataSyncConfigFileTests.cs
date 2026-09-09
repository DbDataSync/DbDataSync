using DbDataSync.Core.Config;

namespace DbDataSync.Core.Tests;

/// <summary>
/// <c>dbdatasync.config.yaml</c>'s reader (flattens to ASP.NET Core's own <c>Section:Key</c> shape) and
/// writer (edits one key in place, keeping every other line — including comments — untouched; see
/// architecture/implementation/done/phase-079-standardized-config-file.md for why this isn't a
/// round trip through YamlDotNet's serializer).
/// </summary>
public sealed class DbDataSyncConfigFileTests : IDisposable
{
    private readonly string _repoRoot = Directory.CreateTempSubdirectory("dbdatasync-config-file-tests-").FullName;

    public void Dispose() => Directory.Delete(_repoRoot, recursive: true);

    private string Path_ => DbDataSyncConfigFile.PathIn(_repoRoot);

    [Fact]
    public void Read_NoFile_ReturnsEmpty() => Assert.Empty(DbDataSyncConfigFile.Read(_repoRoot));

    [Fact]
    public void Read_FlattensNestedSections()
    {
        File.WriteAllText(Path_,
            """
            DbDataSync:
              Url: http://localhost:5080
              StateEngine: MsSql
            """);

        var flattened = DbDataSyncConfigFile.Read(_repoRoot);

        Assert.Equal("http://localhost:5080", flattened["DbDataSync:Url"]);
        Assert.Equal("MsSql", flattened["DbDataSync:StateEngine"]);
    }

    [Fact]
    public void Read_IgnoresComments()
    {
        File.WriteAllText(Path_,
            """
            # DbDataSync:
            #   Url: http://localhost:5080
            """);

        Assert.Empty(DbDataSyncConfigFile.Read(_repoRoot));
    }

    [Fact]
    public void Read_QuotedValue_IsReadBackUnquoted()
    {
        File.WriteAllText(Path_,
            """
            DbDataSync:
              StateConnectionString: "Server=sql01;Database=DbDataSyncState;"
            """);

        Assert.Equal(
            "Server=sql01;Database=DbDataSyncState;", DbDataSyncConfigFile.Read(_repoRoot)["DbDataSync:StateConnectionString"]);
    }

    [Fact]
    public void SetValue_NoFile_CreatesItWithTheSectionAndKey()
    {
        DbDataSyncConfigFile.SetValue(_repoRoot, "DbDataSync", "Url", "http://localhost:5080");

        Assert.Equal("http://localhost:5080", DbDataSyncConfigFile.Read(_repoRoot)["DbDataSync:Url"]);
    }

    [Fact]
    public void SetValue_ExistingKey_ReplacesItsValueOnly()
    {
        DbDataSyncConfigFile.SetValue(_repoRoot, "DbDataSync", "Url", "http://localhost:5080");
        DbDataSyncConfigFile.SetValue(_repoRoot, "DbDataSync", "Url", "http://localhost:9090");

        Assert.Equal("http://localhost:9090", DbDataSyncConfigFile.Read(_repoRoot)["DbDataSync:Url"]);
    }

    [Fact]
    public void SetValue_NewKeyInExistingSection_IsAdded()
    {
        DbDataSyncConfigFile.SetValue(_repoRoot, "DbDataSync", "Url", "http://localhost:5080");
        DbDataSyncConfigFile.SetValue(_repoRoot, "DbDataSync", "StateEngine", "Postgres");

        var flattened = DbDataSyncConfigFile.Read(_repoRoot);
        Assert.Equal("http://localhost:5080", flattened["DbDataSync:Url"]);
        Assert.Equal("Postgres", flattened["DbDataSync:StateEngine"]);
    }

    [Fact]
    public void SetValue_PreservesCommentsElsewhereInTheFile()
    {
        File.WriteAllText(Path_,
            """
            # A note an operator left for themselves.
            DbDataSync:
              Url: http://localhost:5080
              # StateEngine: MsSql
            """);

        DbDataSyncConfigFile.SetValue(_repoRoot, "DbDataSync", "Url", "http://localhost:9090");

        var text = File.ReadAllText(Path_);
        Assert.Contains("# A note an operator left for themselves.", text);
        Assert.Contains("# StateEngine: MsSql", text);
        Assert.Contains("http://localhost:9090", text);
        Assert.DoesNotContain("http://localhost:5080", text);
    }

    [Fact]
    public void SetValue_StateConnectionStringWithACredential_IsRejected()
    {
        var ex = Assert.Throws<ConfigValidationException>(() =>
            DbDataSyncConfigFile.SetValue(_repoRoot, "DbDataSync", "StateConnectionString", "Server=sql01;Password=hunter2"));

        Assert.Contains("secret store", ex.Message);
        Assert.False(File.Exists(Path_)); // rejected before anything hit disk
    }

    [Fact]
    public void SetValue_StateConnectionStringWithoutACredential_Succeeds()
    {
        DbDataSyncConfigFile.SetValue(_repoRoot, "DbDataSync", "StateConnectionString", "Server=sql01;Database=DbDataSyncState;");

        Assert.Equal(
            "Server=sql01;Database=DbDataSyncState;",
            DbDataSyncConfigFile.Read(_repoRoot)["DbDataSync:StateConnectionString"]);
    }

    [Fact]
    public void WriteStarter_NoFile_WritesOneWithTheSecretRefComment()
    {
        DbDataSyncConfigFile.WriteStarter(_repoRoot);

        var text = File.ReadAllText(Path_);
        Assert.Contains("dbdatasync config secret set dbdatasync:config:stateConnectionString", text);
        // Every key is commented out — this is a reference, not a default configuration silently in effect.
        Assert.Empty(DbDataSyncConfigFile.Read(_repoRoot));
    }

    [Fact]
    public void WriteStarter_FileAlreadyExists_LeavesItUntouched()
    {
        File.WriteAllText(Path_, "DbDataSync:\n  Url: http://custom/\n");

        DbDataSyncConfigFile.WriteStarter(_repoRoot);

        Assert.Equal("http://custom/", DbDataSyncConfigFile.Read(_repoRoot)["DbDataSync:Url"]);
    }
}
