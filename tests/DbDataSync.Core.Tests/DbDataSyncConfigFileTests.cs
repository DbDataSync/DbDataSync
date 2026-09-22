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

    /// <summary>
    /// The same logical key can already be in the file under a *different* split between section
    /// header and key line — <c>setup</c> and the phase 164 migration write
    /// <c>DbDataSync:Auth:Network:</c> / <c>Admin</c>, while the Admin config screen and
    /// <c>config set</c> write <c>DbDataSync:</c> / <c>Auth:Network:Admin</c>. Both flatten to the
    /// same key, so a write has to find whichever line the file actually has rather than adding a
    /// second one and leaving the reader to pick.
    /// </summary>
    [Fact]
    public void SetValue_KeyWrittenUnderAnEquivalentSectionHeader_UpdatesThatLine()
    {
        File.WriteAllText(Path_,
            """
            DbDataSync:
              App:Url: "http://localhost:5080"

            DbDataSync:Auth:Network:
              Admin: "loopback"
            """);

        DbDataSyncConfigFile.SetValue(_repoRoot, "DbDataSync", "Auth:Network:Admin", "disabled");

        Assert.Equal("disabled", DbDataSyncConfigFile.Read(_repoRoot)["DbDataSync:Auth:Network:Admin"]);
    }

    /// <summary>The same collision the other way round: <c>setup</c>'s split writing into a file the
    /// Admin config screen wrote first.</summary>
    [Fact]
    public void SetValue_KeyWrittenUnderTheShorterHeader_UpdatesThatLine()
    {
        DbDataSyncConfigFile.SetValue(_repoRoot, "DbDataSync", "Auth:Network:Admin", "disabled");
        DbDataSyncConfigFile.SetValue(_repoRoot, "DbDataSync:Auth:Network", "Admin", "loopback");

        var flattened = DbDataSyncConfigFile.Read(_repoRoot);
        Assert.Equal("loopback", flattened["DbDataSync:Auth:Network:Admin"]);
        Assert.Equal(1, File.ReadAllLines(Path_).Count(l => l.Contains("Admin", StringComparison.Ordinal)));
    }

    /// <summary>A hand-written file uses YAML's own nesting rather than either writer's split, and a
    /// write has to land on that line too rather than adding a second one beside it.</summary>
    [Fact]
    public void SetValue_KeyWrittenAsNestedMappings_UpdatesThatLine()
    {
        File.WriteAllText(Path_,
            """
            DbDataSync:
              Auth:
                Network:
                  Admin: loopback
            """);

        DbDataSyncConfigFile.SetValue(_repoRoot, "DbDataSync", "Auth:Network:Admin", "disabled");

        Assert.Equal("disabled", DbDataSyncConfigFile.Read(_repoRoot)["DbDataSync:Auth:Network:Admin"]);
    }

    /// <summary>A value with a colon in it (a URL) must not be mistaken for a key boundary.</summary>
    [Fact]
    public void SetValue_ExistingKeyWhoseValueContainsAColon_ReplacesTheValueOnly()
    {
        DbDataSyncConfigFile.SetValue(_repoRoot, "DbDataSync", "App:Url", "http://localhost:5080");
        DbDataSyncConfigFile.SetValue(_repoRoot, "DbDataSync", "App:Url", "http://localhost:9090");

        var flattened = DbDataSyncConfigFile.Read(_repoRoot);
        Assert.Equal("http://localhost:9090", flattened["DbDataSync:App:Url"]);
        Assert.Single(flattened);
    }

    /// <summary>Phase 164 renamed <c>StateConnectionString</c> to <c>State:ConnectionString</c> and
    /// moved the split, which left the credential guard matching a key name nothing passes any more.</summary>
    [Fact]
    public void SetValue_StateConnectionStringUnderItsPhase164Name_IsStillRejected()
    {
        foreach (var (section, key) in new[] { ("DbDataSync:State", "ConnectionString"), ("DbDataSync", "State:ConnectionString") })
        {
            var ex = Assert.Throws<ConfigValidationException>(() =>
                DbDataSyncConfigFile.SetValue(_repoRoot, section, key, "Server=sql01;Password=hunter2"));

            Assert.Contains("secret store", ex.Message);
        }

        Assert.False(File.Exists(Path_)); // rejected before anything hit disk
    }

    /// <summary>RemoveValue shares SetValue's lookup, so the migration's "write the new key, drop the
    /// old one" pair can't half-apply on a file written with the other split.</summary>
    [Fact]
    public void RemoveValue_KeyWrittenUnderAnEquivalentSectionHeader_RemovesThatLine()
    {
        DbDataSyncConfigFile.SetValue(_repoRoot, "DbDataSync:Auth", "Disabled", "true");

        DbDataSyncConfigFile.RemoveValue(_repoRoot, "DbDataSync", "Auth:Disabled");

        // The emptied "DbDataSync:Auth:" header is left behind, as it always was — the key it held
        // is what had to stop being read.
        Assert.DoesNotContain("DbDataSync:Auth:Disabled", DbDataSyncConfigFile.Read(_repoRoot).Keys);
    }

    [Fact]
    public void RemoveValue_NoFile_IsANoOp()
    {
        DbDataSyncConfigFile.RemoveValue(_repoRoot, "DbDataSync", "Url");

        Assert.False(File.Exists(Path_));
    }

    [Fact]
    public void RemoveValue_KeyNotPresent_IsANoOp()
    {
        DbDataSyncConfigFile.SetValue(_repoRoot, "DbDataSync", "Url", "http://localhost:5080");

        DbDataSyncConfigFile.RemoveValue(_repoRoot, "DbDataSync", "StateEngine");

        Assert.Equal("http://localhost:5080", DbDataSyncConfigFile.Read(_repoRoot)["DbDataSync:Url"]);
    }

    [Fact]
    public void RemoveValue_ExistingKey_RemovesOnlyThatLine()
    {
        DbDataSyncConfigFile.SetValue(_repoRoot, "DbDataSync", "Url", "http://localhost:5080");
        DbDataSyncConfigFile.SetValue(_repoRoot, "DbDataSync", "StateEngine", "Postgres");

        DbDataSyncConfigFile.RemoveValue(_repoRoot, "DbDataSync", "StateEngine");

        var flattened = DbDataSyncConfigFile.Read(_repoRoot);
        Assert.Equal("http://localhost:5080", flattened["DbDataSync:Url"]);
        Assert.False(flattened.ContainsKey("DbDataSync:StateEngine"));
    }

    [Fact]
    public void RemoveValue_PreservesCommentsAndOtherSections()
    {
        File.WriteAllText(Path_,
            """
            # A note an operator left for themselves.
            DbDataSync:
              Url: http://localhost:5080
              StateEngine: Postgres

            Kestrel:Certificates:Default:
              Path: /etc/certs/cert.pem
              KeyPath: /etc/certs/key.pem
            """);

        DbDataSyncConfigFile.RemoveValue(_repoRoot, "Kestrel:Certificates:Default", "KeyPath");

        var text = File.ReadAllText(Path_);
        Assert.Contains("# A note an operator left for themselves.", text);
        Assert.Contains("StateEngine: Postgres", text);
        Assert.Contains("Path: /etc/certs/cert.pem", text);
        Assert.DoesNotContain("KeyPath", text);
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
