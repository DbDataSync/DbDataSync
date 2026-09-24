using ClrKernel.Core.Secrets;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;
using LibGit2Sharp;

namespace DbDataSync.Core.Tests;

/// <summary>
/// Phase 186J's <see cref="RelationshipConfig"/>/<see cref="RelationshipJoinKey"/>,
/// <see cref="ColumnMapping.Relationship"/>, and <see cref="TableMappingConfig.RelationshipColumns"/>,
/// through the YAML they are actually stored as — the same reason
/// <see cref="CachedColumnsYamlRoundTripTests"/> exists for the cache these sit beside: a persisted
/// type that serialises out and will not parse back is a field that appears to work until the next
/// restart.
/// </summary>
public sealed class RelationshipsYamlRoundTripTests : IDisposable
{
    private static readonly GitAuthor Author = new("Test", "test@example.com");

    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-relationships-").FullName;
    private readonly ConfigRepository _config;

    public RelationshipsYamlRoundTripTests()
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

    private TableMappingConfig Save(TableMappingConfig mapping)
    {
        _config.SaveTableMapping("r", mapping, Author);
        return _config.LoadTableMapping("r", mapping.Name);
    }

    private static TableMappingConfig BaseMapping(string name = "m") => new()
    {
        Name = name,
        Sources = [new SourceTableSpec { ConnectionName = "s", Database = "d", Schema = "dbo", Table = "Orders" }],
        Targets = [new TableSpec { ConnectionName = "t", Database = "d", Schema = "dbo", Table = "Orders" }],
    };

    [Fact]
    public void ARelationshipWithACompositeKey_SurvivesARoundTrip()
    {
        var mapping = BaseMapping();
        mapping.Relationships =
        [
            new RelationshipConfig
            {
                Name = "Customer",
                Schema = "dbo",
                Table = "Customers",
                JoinKeys =
                [
                    new RelationshipJoinKey { LocalColumn = "CustomerId", ForeignColumn = "Id" },
                    new RelationshipJoinKey { LocalColumn = "CustomerRegion", ForeignColumn = "Region" },
                ],
            },
        ];
        mapping.ColumnMappings =
        [
            new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" },
            new ColumnMapping { SourceColumn = "Name", TargetColumn = "CustomerName", Relationship = "Customer" },
        ];

        var loaded = Save(mapping);

        var relationship = Assert.Single(loaded.Relationships);
        Assert.Equal("Customer", relationship.Name);
        Assert.Equal("dbo", relationship.Schema);
        Assert.Equal("Customers", relationship.Table);
        Assert.Equal(2, relationship.JoinKeys.Count);
        Assert.Equal("CustomerId", relationship.JoinKeys[0].LocalColumn);
        Assert.Equal("Id", relationship.JoinKeys[0].ForeignColumn);
        Assert.Equal("CustomerRegion", relationship.JoinKeys[1].LocalColumn);
        Assert.Equal("Region", relationship.JoinKeys[1].ForeignColumn);

        Assert.Null(loaded.ColumnMappings[0].Relationship);
        Assert.Equal("Customer", loaded.ColumnMappings[1].Relationship);
    }

    [Fact]
    public void ASelfJoinRelationship_SurvivesARoundTrip()
    {
        // 185J/186J's confirmed decision: a relationship's foreign side equalling the mapping's own
        // primary source (an employee -> manager pattern within one table) is ordinary, not special.
        var mapping = BaseMapping();
        mapping.Relationships =
        [
            new RelationshipConfig
            {
                Name = "Manager",
                Schema = "dbo",
                Table = "Orders",
                JoinKeys = [new RelationshipJoinKey { LocalColumn = "ManagerId", ForeignColumn = "Id" }],
            },
        ];

        var loaded = Save(mapping);

        var relationship = Assert.Single(loaded.Relationships);
        Assert.Equal("Orders", relationship.Table);
        Assert.Equal(mapping.Sources[0].Table, relationship.Table);
    }

    [Fact]
    public void RelationshipColumns_SurviveARoundTrip()
    {
        var mapping = BaseMapping();
        mapping.Relationships =
        [
            new RelationshipConfig
            {
                Name = "Customer",
                Table = "Customers",
                JoinKeys = [new RelationshipJoinKey { LocalColumn = "CustomerId", ForeignColumn = "Id" }],
            },
        ];
        mapping.RelationshipColumns = new Dictionary<string, List<CachedColumn>>
        {
            ["Customer"] =
            [
                new CachedColumn("Id", "int", isNullable: false, isPrimaryKey: true, isIdentity: true),
                new CachedColumn("Region", "nvarchar(50)", isNullable: true, isPrimaryKey: false, isIdentity: false),
            ],
        };

        var loaded = Save(mapping);

        var columns = Assert.Single(loaded.RelationshipColumns);
        Assert.Equal("Customer", columns.Key);
        Assert.Equal(2, columns.Value.Count);
        Assert.Equal("Id", columns.Value[0].Name);
        Assert.True(columns.Value[0].IsPrimaryKey);
        Assert.Equal("Region", columns.Value[1].Name);
        Assert.True(columns.Value[1].IsNullable);
    }

    [Fact]
    public void AMappingWithNoRelationships_LoadsWithEmptyListsAndDictionary()
    {
        var loaded = Save(BaseMapping());

        Assert.Empty(loaded.Relationships);
        Assert.Empty(loaded.RelationshipColumns);
    }

    [Fact]
    public void AMappingSavedBeforeThisFeatureExisted_StillLoads()
    {
        // Not a mapping this repo's own writer produced, but the actual YAML on disk from before phase
        // 186J: no relationships key, no relationshipColumns key, and a columnMapping with no
        // relationship field at all. An added field that made every existing file fail to parse would
        // take the whole config directory down on upgrade.
        var path = Path.Combine(_root, "config", "replications", "r", "table-mappings", "old.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            name: old
            sources:
            - connectionName: s
              database: d
              schema: dbo
              table: Orders
            targets:
            - connectionName: t
              database: d
              schema: dbo
              table: Orders
            columnMappings:
            - sourceColumn: Id
              targetColumn: Id
            """);

        var loaded = _config.LoadTableMapping("r", "old");

        Assert.Equal("old", loaded.Name);
        Assert.Single(loaded.ColumnMappings);
        Assert.Null(loaded.ColumnMappings[0].Relationship);
        Assert.Empty(loaded.Relationships);
        Assert.Empty(loaded.RelationshipColumns);
    }
}
