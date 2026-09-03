using DbDataSync.State.Remote;

namespace DbDataSync.State.Tests;

/// <summary>
/// The journal exists to survive the process being killed, so most of what matters here is what a
/// half-written file reads back as.
/// </summary>
public sealed class StateJournalTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-journal-tests-").FullName;
    private string StateDbPath => Path.Combine(_root, "state.db");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Appended_entries_read_back_in_order_with_their_payloads()
    {
        var runId = Guid.NewGuid();
        var path = StateJournal.PathFor(StateDbPath, "sales", runId);

        using (var journal = new StateJournal(path))
        {
            journal.Append(JournalOperation.MarkDone, new WorkItemRequest(7));
            journal.Append(JournalOperation.SetWatermark, new SetWatermarkRequest("sales", "dbo.Orders", "42"));
            journal.Append(JournalOperation.CompleteRun,
                new CompleteRunRequest(runId, RunStatus.Succeeded, 10, 10, null));
        }

        var entries = StateJournal.Read(path);

        Assert.Equal([1L, 2L, 3L], entries.Select(e => e.Sequence));
        Assert.Equal(
            [JournalOperation.MarkDone, JournalOperation.SetWatermark, JournalOperation.CompleteRun],
            entries.Select(e => e.Operation));
        Assert.Equal(7, StateJournal.PayloadOf<WorkItemRequest>(entries[0])!.WorkItemId);
        Assert.Equal("42", StateJournal.PayloadOf<SetWatermarkRequest>(entries[1])!.Watermark);
        Assert.Equal(RunStatus.Succeeded, StateJournal.PayloadOf<CompleteRunRequest>(entries[2])!.Status);
    }

    /// <summary>The reason the format is JSON Lines. A file cut off mid-write — a kill, a full disk —
    /// must still yield everything written before the cut.</summary>
    [Fact]
    public void A_truncated_final_line_is_skipped_and_everything_before_it_survives()
    {
        var path = StateJournal.PathFor(StateDbPath, "sales", Guid.NewGuid());
        using (var journal = new StateJournal(path))
        {
            journal.Append(JournalOperation.MarkDone, new WorkItemRequest(1));
            journal.Append(JournalOperation.MarkDone, new WorkItemRequest(2));
        }

        var text = File.ReadAllText(path);
        File.WriteAllText(path, text + """{"sequence":3,"operation":"MarkD""");

        var skipped = new List<string>();
        var entries = StateJournal.Read(path, skipped.Add);

        Assert.Equal(2, entries.Count);
        Assert.Single(skipped);
    }

    /// <summary>A run that never lost its owner should leave nothing behind for the owner to find and
    /// report as an incident.</summary>
    [Fact]
    public void A_journal_that_is_never_appended_to_creates_no_file()
    {
        var path = StateJournal.PathFor(StateDbPath, "sales", Guid.NewGuid());
        using var journal = new StateJournal(path);

        Assert.False(journal.HasEntries);
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void Journals_are_grouped_by_replication_so_recovery_can_drain_one_at_a_time()
    {
        var sales = StateJournal.PathFor(StateDbPath, "sales", Guid.NewGuid());
        var stock = StateJournal.PathFor(StateDbPath, "stock", Guid.NewGuid());

        Assert.Equal(StateJournal.DirectoryFor(StateDbPath, "sales"), Path.GetDirectoryName(sales));
        Assert.NotEqual(Path.GetDirectoryName(sales), Path.GetDirectoryName(stock));
        Assert.Equal(StateJournal.RootFor(StateDbPath), Path.GetDirectoryName(Path.GetDirectoryName(sales)));
    }
}
