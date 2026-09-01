using DataSync.Core.Config;

namespace DataSync.Core.Tests;

/// <summary>
/// <c>datasync.config.yaml</c>'s reader (flattens to ASP.NET Core's own <c>Section:Key</c> shape) and
/// writer (edits one key in place, keeping every other line — including comments — untouched; see
/// architecture/implementation/done/phase-079-standardized-config-file.md for why this isn't a
/// round trip through YamlDotNet's serializer).
/// </summary>
public sealed class DataSyncConfigFileTests : IDisposable
{
    private readonly string _repoRoot = Directory.CreateTempSubdirectory("datasync-config-file-tests-").FullName;

    public void Dispose() => Directory.Delete(_repoRoot, recursive: true);

    private string Path_ => DataSyncConfigFile.PathIn(_repoRoot);

    [Fact]
    public void Read_NoFile_ReturnsEmpty() => Assert.Empty(DataSyncConfigFile.Read(_repoRoot));

    [Fact]
    public void Read_FlattensNestedSections()
    {
        File.WriteAllText(Path_,
            """
            DataSync:
              Url: http://localhost:5080
              StateEngine: MsSql
            """);

        var flattened = DataSyncConfigFile.Read(_repoRoot);

        Assert.Equal("http://localhost:5080", flattened["DataSync:Url"]);
        Assert.Equal("MsSql", flattened["DataSync:StateEngine"]);
    }

    [Fact]
    public void Read_IgnoresComments()
    {
        File.WriteAllText(Path_,
            """
            # DataSync:
            #   Url: http://localhost:5080
            """);

        Assert.Empty(DataSyncConfigFile.Read(_repoRoot));
    }

    [Fact]
    public void Read_QuotedValue_IsReadBackUnquoted()
    {
        File.WriteAllText(Path_,
            """
            DataSync:
              StateConnectionString: "Server=sql01;Database=DataSyncState;"
            """);

        Assert.Equal(
            "Server=sql01;Database=DataSyncState;", DataSyncConfigFile.Read(_repoRoot)["DataSync:StateConnectionString"]);
    }

    [Fact]
    public void SetValue_NoFile_CreatesItWithTheSectionAndKey()
    {
        DataSyncConfigFile.SetValue(_repoRoot, "DataSync", "Url", "http://localhost:5080");

        Assert.Equal("http://localhost:5080", DataSyncConfigFile.Read(_repoRoot)["DataSync:Url"]);
    }

    [Fact]
    public void SetValue_ExistingKey_ReplacesItsValueOnly()
    {
        DataSyncConfigFile.SetValue(_repoRoot, "DataSync", "Url", "http://localhost:5080");
        DataSyncConfigFile.SetValue(_repoRoot, "DataSync", "Url", "http://localhost:9090");

        Assert.Equal("http://localhost:9090", DataSyncConfigFile.Read(_repoRoot)["DataSync:Url"]);
    }

    [Fact]
    public void SetValue_NewKeyInExistingSection_IsAdded()
    {
        DataSyncConfigFile.SetValue(_repoRoot, "DataSync", "Url", "http://localhost:5080");
        DataSyncConfigFile.SetValue(_repoRoot, "DataSync", "StateEngine", "Postgres");

        var flattened = DataSyncConfigFile.Read(_repoRoot);
        Assert.Equal("http://localhost:5080", flattened["DataSync:Url"]);
        Assert.Equal("Postgres", flattened["DataSync:StateEngine"]);
    }

    [Fact]
    public void SetValue_PreservesCommentsElsewhereInTheFile()
    {
        File.WriteAllText(Path_,
            """
            # A note an operator left for themselves.
            DataSync:
              Url: http://localhost:5080
              # StateEngine: MsSql
            """);

        DataSyncConfigFile.SetValue(_repoRoot, "DataSync", "Url", "http://localhost:9090");

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
            DataSyncConfigFile.SetValue(_repoRoot, "DataSync", "StateConnectionString", "Server=sql01;Password=hunter2"));

        Assert.Contains("secret store", ex.Message);
        Assert.False(File.Exists(Path_)); // rejected before anything hit disk
    }

    [Fact]
    public void SetValue_StateConnectionStringWithoutACredential_Succeeds()
    {
        DataSyncConfigFile.SetValue(_repoRoot, "DataSync", "StateConnectionString", "Server=sql01;Database=DataSyncState;");

        Assert.Equal(
            "Server=sql01;Database=DataSyncState;",
            DataSyncConfigFile.Read(_repoRoot)["DataSync:StateConnectionString"]);
    }

    [Fact]
    public void WriteStarter_NoFile_WritesOneWithTheSecretRefComment()
    {
        DataSyncConfigFile.WriteStarter(_repoRoot);

        var text = File.ReadAllText(Path_);
        Assert.Contains("datasync secret set datasync:config:stateConnectionString", text);
        // Every key is commented out — this is a reference, not a default configuration silently in effect.
        Assert.Empty(DataSyncConfigFile.Read(_repoRoot));
    }

    [Fact]
    public void WriteStarter_FileAlreadyExists_LeavesItUntouched()
    {
        File.WriteAllText(Path_, "DataSync:\n  Url: http://custom/\n");

        DataSyncConfigFile.WriteStarter(_repoRoot);

        Assert.Equal("http://custom/", DataSyncConfigFile.Read(_repoRoot)["DataSync:Url"]);
    }
}
