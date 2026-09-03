using ClrKernel.Core.Secrets;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;
using LibGit2Sharp;

namespace DbDataSync.Core.Tests;

/// <summary>
/// A mapping's default segmenting is a sealed hierarchy stored in YAML, which neither half of
/// YamlDotNet handles on its own — a derived record's fields would be written with nothing saying
/// which record they are, and an abstract type cannot be constructed on the way back in. These pin
/// that <see cref="BatchReloadSegmentYamlConverter"/> closes both halves, per mode, because the
/// failure only shows up on the *second* load: as config an operator can no longer open.
/// </summary>
public sealed class DefaultSegmentingYamlRoundTripTests : IDisposable
{
    private static readonly GitAuthor Author = new("Test", "test@example.com");

    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-segmenting-").FullName;
    private readonly ConfigRepository _config;

    public DefaultSegmentingYamlRoundTripTests()
    {
        Repository.Init(_root);
        _config = new ConfigRepository(
            Path.Combine(_root, "config"), new GitCommitService(_root),
            SecretStore.ForProviders([new InMemorySecretProvider()]));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private IReadOnlyList<BatchReloadSegment> RoundTrip(params BatchReloadSegment[] segments)
    {
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
        _config.SaveTableMapping("r", new TableMappingConfig
        {
            Name = "m",
            Sources = [new SourceTableSpec { ConnectionName = "s", Database = "d", Schema = "dbo", Table = "T" }],
            Targets = [new TableSpec { ConnectionName = "t", Database = "d", Schema = "dbo", Table = "T" }],
            DefaultSegmenting = [.. segments],
        }, Author);

        return _config.LoadTableMapping("r", "m").DefaultSegmenting;
    }

    [Fact]
    public void EverySegmentMode_SurvivesARoundTrip()
    {
        BatchReloadSegment[] every =
        [
            new FullSegment(),
            new ListSegment("Region", ["EU", "US", "APAC"]),
            new RangeSegment("Id", "1", "100"),
            new RangeSegment("OrderDate", "2024-03-01", "2024-04-01", "2024-03"),
            new AutoSegment("Id", 8),
            new CustomSegment("year-month"),
        ];

        Assert.Equal(every, RoundTrip(every));
    }

    [Fact]
    public void ALabellessRange_ComesBackLabelless()
    {
        // The label is optional and omitted when null, so the reader has to cope with the key being
        // absent rather than expecting an empty string — which would turn Describe()'s fallback off.
        var loaded = Assert.IsType<RangeSegment>(Assert.Single(RoundTrip(new RangeSegment("Id", "1", "100"))));

        Assert.Null(loaded.Label);
        Assert.Equal("Id [1, 100)", loaded.Describe());
    }

    [Fact]
    public void AListSegmentsValues_KeepTheirOrderAndCount()
    {
        var loaded = Assert.IsType<ListSegment>(
            Assert.Single(RoundTrip(new ListSegment("Region", ["EU", "US", "APAC"]))));

        Assert.Equal(["EU", "US", "APAC"], loaded.Values);
    }

    [Fact]
    public void AMappingThatConfiguresNothing_LoadsAnEmptyList()
    {
        // Empty means Full/no segmenting, and it has to be an empty list rather than null — every
        // consumer reads .Count on it.
        Assert.Empty(RoundTrip());
    }

    /// <summary>
    /// Several static entries side by side, which is the shape that replaced the old hand-typed
    /// `segments` reader option — and the one an operator builds in the mapping editor's add/remove
    /// rows.
    /// </summary>
    [Fact]
    public void SeveralStaticEntriesOfMixedKinds_RoundTripAsAList()
    {
        var loaded = RoundTrip(
            new ListSegment("Region", ["EU"]),
            new ListSegment("Region", ["US"]),
            new RangeSegment("Id", "1", "100"),
            new RangeSegment("Id", "100", "200"));

        Assert.Equal(4, loaded.Count);
        Assert.Equal(2, loaded.OfType<ListSegment>().Count());
        Assert.Equal(2, loaded.OfType<RangeSegment>().Count());
    }
}
