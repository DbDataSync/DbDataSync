using ClrKernel.Core.Secrets;
using DataSync.Core.Config;
using DataSync.Core.Git;
using LibGit2Sharp;

namespace DataSync.Core.Tests;

/// <summary>
/// Notes are Markdown, which means multiple lines, leading hashes and colons — all of which YAML has
/// opinions about. A field that silently loses its blank lines, or fails to parse back at all because
/// something started with a "#", is worse than not having one. Phase 64.
/// </summary>
public sealed class NotesYamlRoundTripTests : IDisposable
{
    private static readonly GitAuthor Author = new("Test", "test@example.com");

    private const string Markdown = """
        # Owner: the warehouse team

        - Do **not** reload during month-end close.
        - `OrderDate` is local time, not UTC: see [ticket 4471](https://example.invalid/4471).

        > Migrated from the old ETL in March; the "Region" column is a code, not a name.
        """;

    private readonly string _root = Directory.CreateTempSubdirectory("datasync-notes-").FullName;
    private readonly ConfigRepository _config;

    public NotesYamlRoundTripTests()
    {
        Repository.Init(_root);
        _config = new ConfigRepository(
            Path.Combine(_root, "config"), new GitCommitService(_root),
            SecretStore.ForProviders([new InMemorySecretProvider()]));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void SaveReplication(string? notes) => _config.SaveReplicationTask(new ReplicationTaskConfig
    {
        Name = "r",
        Notes = notes,
        Scheduling = new SchedulingConfig { Mode = ScheduleMode.Periodic },
        ChangeProcessing = new ChangeProcessingConfig
        {
            Reader = new ReaderConfig { Kind = "BatchReload" },
            Cache = new CacheConfig { Kind = "StagingTable" },
            Writer = new WriterConfig { Kind = "DeleteInsert" },
        },
    }, Author);

    private void SaveMapping(string? notes) => _config.SaveTableMapping("r", new TableMappingConfig
    {
        Name = "m",
        Notes = notes,
        Sources = [new SourceTableSpec { ConnectionName = "s", Database = "d", Schema = "dbo", Table = "T" }],
        Targets = [new TableSpec { ConnectionName = "t", Database = "d", Schema = "dbo", Table = "T" }],
    }, Author);

    [Fact]
    public void AReplicationsMarkdownNotes_SurviveARoundTrip()
    {
        SaveReplication(Markdown);

        Assert.Equal(Markdown, _config.LoadReplicationTask("r").Notes);
    }

    [Fact]
    public void AMappingsMarkdownNotes_SurviveARoundTrip()
    {
        SaveReplication(null);
        SaveMapping(Markdown);

        Assert.Equal(Markdown, _config.LoadTableMapping("r", "m").Notes);
    }

    [Fact]
    public void NoNotes_StaysNullRatherThanBecomingAnEmptyString()
    {
        // The Notes tab distinguishes "nothing written yet" (offering to start) from an empty
        // document, and an empty string arriving back as one would make that distinction wrong.
        SaveReplication(null);
        SaveMapping(null);

        Assert.Null(_config.LoadReplicationTask("r").Notes);
        Assert.Null(_config.LoadTableMapping("r", "m").Notes);
    }

    [Fact]
    public void NotesCanBeEditedAndCleared()
    {
        SaveReplication(Markdown);
        SaveReplication("something shorter");
        Assert.Equal("something shorter", _config.LoadReplicationTask("r").Notes);

        SaveReplication(null);
        Assert.Null(_config.LoadReplicationTask("r").Notes);
    }
}
