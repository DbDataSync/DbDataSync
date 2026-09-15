using ClrKernel.Core.Secrets;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;
using LibGit2Sharp;

namespace DbDataSync.Core.Tests;

/// <summary>
/// The cached column metadata phase 90 added, through the YAML it is actually stored as.
/// <para>
/// Worth its own file for the reason <c>NotesYamlRoundTripTests</c> is: a persisted type that
/// serialises out and will not parse back is a field that appears to work until the next restart.
/// <see cref="CachedColumn"/> is a class rather than a positional record precisely because of that,
/// and the assertion below is what says so.
/// </para>
/// </summary>
public sealed class CachedColumnsYamlRoundTripTests : IDisposable
{
    private static readonly GitAuthor Author = new("Test", "test@example.com");

    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-cached-columns-").FullName;
    private readonly ConfigRepository _config;

    public CachedColumnsYamlRoundTripTests()
    {
        Repository.Init(_root);
        _config = new ConfigRepository(
            Path.Combine(_root, "config"), new GitCommitService(_root),
            SecretStore.ForProviders([new InMemorySecretProvider()]));

        _config.SaveReplicationTask(new ReplicationTaskConfig
        {
            Name = "r",
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Periodic },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = "BatchReload" },
                Cache = new CacheConfig { Kind = "StagingTable" },
                Writer = new WriterConfig { Kind = "DeleteInsert" },
            },
        }, Author);
    }

    public void Dispose() => GitTempDirectory.DeleteRecursively(_root);

    private TableMappingConfig Save(
        List<CachedColumn> source, List<CachedColumn> target, DateTime? capturedUtc)
    {
        _config.SaveTableMapping("r", new TableMappingConfig
        {
            Name = "m",
            Sources = [new SourceTableSpec { ConnectionName = "s", Database = "d", Schema = "dbo", Table = "T" }],
            Targets = [new TableSpec { ConnectionName = "t", Database = "d", Schema = "dbo", Table = "T" }],
            SourceColumns = source,
            TargetColumns = target,
            ColumnsCapturedUtc = capturedUtc,
        }, Author);
        return _config.LoadTableMapping("r", "m");
    }

    [Fact]
    public void EveryFactOfACapturedColumn_SurvivesARoundTrip()
    {
        var captured = new DateTime(2026, 9, 1, 14, 30, 0, DateTimeKind.Utc);
        var loaded = Save(
            [
                new CachedColumn("Id", "int", isNullable: false, isPrimaryKey: true, isIdentity: true),
                new CachedColumn("Region", "nvarchar(50)", isNullable: true, isPrimaryKey: false, isIdentity: false),
            ],
            [new CachedColumn("Id", "bigint", isNullable: false, isPrimaryKey: true, isIdentity: false)],
            captured);

        // Each of the five facts asserted separately: four of them are booleans, and a test that only
        // counted the columns would pass on a serializer that dropped every flag.
        Assert.Equal(2, loaded.SourceColumns.Count);
        Assert.Equal("Id", loaded.SourceColumns[0].Name);
        Assert.Equal("int", loaded.SourceColumns[0].NativeType);
        Assert.False(loaded.SourceColumns[0].IsNullable);
        Assert.True(loaded.SourceColumns[0].IsPrimaryKey);
        Assert.True(loaded.SourceColumns[0].IsIdentity);

        Assert.Equal("Region", loaded.SourceColumns[1].Name);
        Assert.Equal("nvarchar(50)", loaded.SourceColumns[1].NativeType);
        Assert.True(loaded.SourceColumns[1].IsNullable);
        Assert.False(loaded.SourceColumns[1].IsPrimaryKey);
        Assert.False(loaded.SourceColumns[1].IsIdentity);

        // The two sides are stored separately and really do differ — the target's Id is a bigint that
        // is not an identity, which is exactly the divergence a cache exists to record.
        Assert.Equal("bigint", Assert.Single(loaded.TargetColumns).NativeType);
        Assert.False(loaded.TargetColumns[0].IsIdentity);

        Assert.Equal(captured, loaded.ColumnsCapturedUtc);
    }

    [Fact]
    public void AMappingThatHasNeverBeenCaptured_LoadsWithEmptyListsAndNoTimestamp()
    {
        // The pre-phase-90 row, in the only form this repo can produce one: a mapping saved without
        // the fields. Nothing yet reads them, so what matters is that this loads at all.
        var loaded = Save([], [], null);

        Assert.Empty(loaded.SourceColumns);
        Assert.Empty(loaded.TargetColumns);
        Assert.Null(loaded.ColumnsCapturedUtc);
    }

    [Fact]
    public void AMappingSavedBeforeTheseFieldsExisted_StillLoads()
    {
        // Not a mapping this repo's own writer produced, but the actual YAML on disk in an
        // installation upgraded into this phase: no sourceColumns key, no targetColumns key, no
        // columnsCapturedUtc key at all. An added field that made every existing file fail to parse
        // would take the whole config directory down on upgrade.
        var path = Path.Combine(_root, "config", "replications", "r", "table-mappings", "old.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            name: old
            sources:
            - connectionName: s
              database: d
              schema: dbo
              table: T
            targets:
            - connectionName: t
              database: d
              schema: dbo
              table: T
            columnMappings:
            - sourceColumn: Id
              targetColumn: Id
            """);

        var loaded = _config.LoadTableMapping("r", "old");

        Assert.Equal("old", loaded.Name);
        Assert.Single(loaded.ColumnMappings);
        Assert.Empty(loaded.SourceColumns);
        Assert.Empty(loaded.TargetColumns);
        Assert.Null(loaded.ColumnsCapturedUtc);
    }
}
