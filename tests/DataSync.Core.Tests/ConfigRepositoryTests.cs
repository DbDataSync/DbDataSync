using ClrKernel.Core.Secrets;
using DataSync.Core.Config;
using DataSync.Core.Git;
using LibGit2Sharp;

namespace DataSync.Core.Tests;

public sealed class ConfigRepositoryTests : IDisposable
{
    private readonly string _repoRoot;
    private readonly string _configRoot;
    private readonly ConfigRepository _repository;
    private readonly SecretStore _secrets;
    private static readonly GitAuthor Author = new("Test User", "test@example.com");

    public ConfigRepositoryTests()
    {
        _repoRoot = Directory.CreateTempSubdirectory("datasync-config-tests-").FullName;
        Repository.Init(_repoRoot);
        _configRoot = Path.Combine(_repoRoot, "config");

        // In-memory only: never touch the real OS credential store from a test/CI run.
        _secrets = SecretStore.ForProviders([new InMemorySecretProvider()]);

        _repository = new ConfigRepository(_configRoot, new GitCommitService(_repoRoot), _secrets);
    }

    public void Dispose() => Directory.Delete(_repoRoot, recursive: true);

    [Fact]
    public void SaveConnection_WritesYamlFileMatchingLayout()
    {
        _repository.SaveConnection(SqlAuthInput("orders-db"), Author);

        var expectedPath = Path.Combine(_configRoot, "connections", "orders-db.yaml");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void SaveConnection_NeverWritesPlaintextPasswordToDisk()
    {
        const string password = "sup3r-s3cr3t-P@ssw0rd";
        _repository.SaveConnection(SqlAuthInput("orders-db", password), Author);

        var yaml = File.ReadAllText(Path.Combine(_configRoot, "connections", "orders-db.yaml"));
        Assert.DoesNotContain(password, yaml);
    }

    [Fact]
    public void SaveConnection_CreatesGitCommitContainingNoPlaintextSecret()
    {
        const string password = "sup3r-s3cr3t-P@ssw0rd";
        _repository.SaveConnection(SqlAuthInput("orders-db", password), Author);

        using var repo = new Repository(_repoRoot);
        var head = repo.Head.Tip;
        Assert.NotNull(head);
        Assert.Equal(Author.Name, head.Author.Name);
        Assert.Equal(Author.Email, head.Author.Email);

        var blob = (Blob)head[$"config/connections/orders-db.yaml"].Target;
        Assert.DoesNotContain(password, blob.GetContentText());
    }

    [Fact]
    public void SaveConnection_RoundTripsCredentialThroughSecretStore()
    {
        const string password = "sup3r-s3cr3t-P@ssw0rd";
        var saved = _repository.SaveConnection(SqlAuthInput("orders-db", password), Author);

        Assert.NotNull(saved.CredentialSecretRef);
        var resolved = _secrets.Resolve(saved.CredentialSecretRef!);
        Assert.Equal(password, resolved);
    }

    [Fact]
    public void SaveConnection_IntegratedAuth_HasNoSecretRef()
    {
        var input = new ConnectionInput
        {
            Name = "warehouse-db",
            DriverType = ConnectionDriverType.MsSql,
            Host = "warehouse.internal",
            AuthMode = AuthMode.IntegratedAuth,
        };

        var saved = _repository.SaveConnection(input, Author);

        Assert.Null(saved.CredentialSecretRef);
    }

    [Fact]
    public void SaveConnection_SqlAuthWithoutPasswordOrExistingSecret_Throws()
    {
        var input = new ConnectionInput
        {
            Name = "orders-db",
            DriverType = ConnectionDriverType.MsSql,
            Host = "sql01",
            AuthMode = AuthMode.SqlAuth,
            UserId = "svc_orders",
        };

        Assert.Throws<ConfigValidationException>(() => _repository.SaveConnection(input, Author));
    }

    [Fact]
    public void LoadConnection_ReturnsWhatWasSaved()
    {
        _repository.SaveConnection(SqlAuthInput("orders-db"), Author);

        var loaded = _repository.LoadConnection("orders-db");

        Assert.Equal("orders-db", loaded.Name);
        Assert.Equal(ConnectionDriverType.MsSql, loaded.DriverType);
        Assert.Equal("sql01", loaded.Host);
    }

    [Fact]
    public void ListConnections_ReturnsAllSavedNames()
    {
        _repository.SaveConnection(SqlAuthInput("orders-db"), Author);
        _repository.SaveConnection(SqlAuthInput("crm-db"), Author);

        Assert.Equal(["crm-db", "orders-db"], _repository.ListConnections());
    }

    [Fact]
    public void DeleteConnection_RemovesFileAndSecretAndCommits()
    {
        var saved = _repository.SaveConnection(SqlAuthInput("orders-db"), Author);
        var secretRef = saved.CredentialSecretRef!;
        Assert.True(_secrets.TryResolve(secretRef, out _));

        _repository.DeleteConnection("orders-db", Author);

        Assert.False(File.Exists(Path.Combine(_configRoot, "connections", "orders-db.yaml")));
        Assert.False(_secrets.TryResolve(secretRef, out _));

        using var repo = new Repository(_repoRoot);
        Assert.Contains("Delete connection", repo.Head.Tip.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has spaces")]
    [InlineData("has/slash")]
    public void SaveConnection_RejectsInvalidName(string invalidName)
    {
        var input = SqlAuthInput(invalidName);
        Assert.Throws<ConfigValidationException>(() => _repository.SaveConnection(input, Author));
    }

    [Fact]
    public void SaveReplicationTask_WritesYamlAndCommits()
    {
        var task = new ReplicationTaskConfig
        {
            Name = "crm-sync",
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 30 },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = "MsSqlChangeTracking" },
                Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
                Writer = new WriterConfig { Kind = "MsSqlMerge" },
            },
        };

        _repository.SaveReplicationTask(task, Author);

        var path = Path.Combine(_configRoot, "replications", "crm-sync", "task.yaml");
        Assert.True(File.Exists(path));

        var loaded = _repository.LoadReplicationTask("crm-sync");
        Assert.Equal(ScheduleMode.Continuous, loaded.Scheduling.Mode);
        Assert.Equal(30, loaded.Scheduling.FrequencySeconds);
        Assert.Equal("MsSqlChangeTracking", loaded.ChangeProcessing.Reader.Kind);

        using var repo = new Repository(_repoRoot);
        Assert.Contains("crm-sync", repo.Head.Tip.Message);
    }

    [Fact]
    public void SaveTableMapping_WritesYamlUnderReplicationAndCommits()
    {
        var mapping = new TableMappingConfig
        {
            Name = "orders",
            Sources = [new SourceTableRef { ConnectionName = "orders-db", Database = "App", Table = "Orders" }],
            Targets = [new TableRef { ConnectionName = "warehouse-db", Database = "DW", Table = "Orders" }],
            ColumnMappings = [new ColumnMapping { SourceColumn = "Id", TargetColumn = "OrderId" }],
        };

        _repository.SaveTableMapping("crm-sync", mapping, Author);

        var path = Path.Combine(_configRoot, "replications", "crm-sync", "table-mappings", "orders.yaml");
        Assert.True(File.Exists(path));

        var loaded = _repository.LoadTableMapping("crm-sync", "orders");
        Assert.Single(loaded.Sources);
        Assert.Equal("Orders", loaded.Sources[0].Table);
        Assert.Equal(["orders"], _repository.ListTableMappings("crm-sync"));
    }

    [Fact]
    public void DeleteTableMapping_RemovesFileAndCommits()
    {
        _repository.SaveTableMapping("crm-sync", new TableMappingConfig
        {
            Name = "orders",
            Sources = [new SourceTableRef { ConnectionName = "orders-db", Database = "App", Table = "Orders" }],
            Targets = [new TableRef { ConnectionName = "warehouse-db", Database = "DW", Table = "Orders" }],
        }, Author);

        _repository.DeleteTableMapping("crm-sync", "orders", Author);

        Assert.Empty(_repository.ListTableMappings("crm-sync"));
        using var repo = new Repository(_repoRoot);
        Assert.Contains("Delete table mapping", repo.Head.Tip.Message);
    }

    [Fact]
    public void DeleteReplicationTask_RemovesTaskAndTableMappingsAndCommits()
    {
        var task = new ReplicationTaskConfig
        {
            Name = "crm-sync",
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 30 },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = "MsSqlChangeTracking" },
                Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
                Writer = new WriterConfig { Kind = "MsSqlMerge" },
            },
        };
        _repository.SaveReplicationTask(task, Author);
        _repository.SaveTableMapping("crm-sync", new TableMappingConfig
        {
            Name = "orders",
            Sources = [new SourceTableRef { ConnectionName = "orders-db", Database = "App", Table = "Orders" }],
            Targets = [new TableRef { ConnectionName = "warehouse-db", Database = "DW", Table = "Orders" }],
        }, Author);

        _repository.DeleteReplicationTask("crm-sync", Author);

        Assert.DoesNotContain("crm-sync", _repository.ListReplications());
        Assert.False(Directory.Exists(Path.Combine(_configRoot, "replications", "crm-sync")));
        using var repo = new Repository(_repoRoot);
        Assert.Contains("Delete replication task", repo.Head.Tip.Message);
    }

    private static ConnectionInput SqlAuthInput(string name, string? password = "P@ssw0rd1") =>
        new()
        {
            Name = name,
            DriverType = ConnectionDriverType.MsSql,
            Host = "sql01",
            Port = 1433,
            Database = "App",
            AuthMode = AuthMode.SqlAuth,
            UserId = "svc_app",
            Password = password,
        };
}
