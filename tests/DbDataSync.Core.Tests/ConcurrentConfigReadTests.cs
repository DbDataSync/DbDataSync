using ClrKernel.Core.Secrets;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;
using LibGit2Sharp;

namespace DbDataSync.Core.Tests;

/// <summary>
/// The API serves config reads straight off disk while other requests write. A truncate-then-write
/// leaves a window where a reader sees an empty or half-written document — small, real, and caught in
/// the wild as an API GET answering with a body that would not parse.
/// </summary>
public sealed class ConcurrentConfigReadTests : IDisposable
{
    private static readonly GitAuthor Author = new("Test", "test@example.com");

    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-atomic-").FullName;
    private readonly ConfigRepository _config;

    public ConcurrentConfigReadTests()
    {
        Repository.Init(_root);
        _config = new ConfigRepository(
            Path.Combine(_root, "config"), new GitCommitService(_root),
            SecretStore.ForProviders([new InMemorySecretProvider()]));
    }

    public void Dispose() => GitTempDirectory.DeleteRecursively(_root);

    private ReplicationTaskConfig Task(int frequency) => new()
    {
        Name = "sales",
        Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = frequency },
        ChangeProcessing = new ChangeProcessingConfig
        {
            Reader = new ReaderConfig { Kind = "MsSqlChangeTracking" },
            Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
            Writer = new WriterConfig { Kind = "MsSqlMerge" },
        },
    };

    /// <summary>
    /// Reads hammered against a stream of writes. Every read gets a whole document — one version or
    /// the other, never a fragment. Before the fix this fails as a deserialization error, which is
    /// what the API was turning into an unparseable response.
    /// </summary>
    [Fact]
    public async Task AReadDuringAWrite_NeverSeesAHalfWrittenFile()
    {
        _config.SaveReplicationTask(Task(30), Author);

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var writer = System.Threading.Tasks.Task.Run(() =>
        {
            var frequency = 30;
            while (!stop.IsCancellationRequested)
                _config.SaveReplicationTask(Task(frequency++ % 900 + 1), Author);
        });

        var reads = 0;
        while (!stop.IsCancellationRequested)
        {
            var task = _config.LoadReplicationTask("sales");
            Assert.Equal("sales", task.Name);
            Assert.True(task.Scheduling.FrequencySeconds > 0);
            reads++;
        }

        await writer;
        Assert.True(reads > 50, $"expected the reader to get plenty of goes; got {reads}.");
    }
}
