namespace DataSync.State.Tests;

/// <summary>
/// The notification feed, its per-user cursors and its one producer — see phase 77.
/// <para>
/// The producer's tests live here rather than beside <see cref="TaskRunStore"/>'s other tests because
/// what they assert is the notification, not the completion: that a failure produces exactly one row,
/// that a success produces none, and that a completion arriving twice does not announce twice.
/// </para>
/// </summary>
public sealed class NotificationStoreTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("datasync-notify-tests-").FullName;
    private readonly StateDatabase _database;
    private readonly NotificationStore _store;
    private readonly TaskRunStore _runs;
    private readonly WorkQueueStore _queue;

    public NotificationStoreTests()
    {
        _database = new StateDatabase(Path.Combine(_tempDir, "state.db"));
        _store = new NotificationStore(_database);
        _runs = new TaskRunStore(_database);
        _queue = new WorkQueueStore(_database);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private Guid QueueAndBegin(string taskName, string mappingName)
    {
        var runId = _queue.Enqueue(taskName, RunKind.Primary, mappingName);
        _runs.BeginRun(runId, pid: null);
        return runId;
    }

    /// <summary>Backdated by direct UPDATE, because a notification only ever knows "now" — the same
    /// shape ChangeCheckStoreTests and RunPruningTests use, and for the same reason.</summary>
    private void Backdate(long id, TimeSpan age)
    {
        using var connection = _database.OpenConnection();
        using var cmd = _database.Command(connection,
            "UPDATE Notifications SET CreatedAtUtc = $created WHERE Id = $id;");
        cmd.Bind(_database, "created", (DateTimeOffset.UtcNow - age).ToString("O"));
        cmd.Bind(_database, "id", id);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void AFailedRun_ProducesOneNotification_NamingTheReplicationAndTheRun()
    {
        var runId = QueueAndBegin("crm-sync", "orders");

        _runs.CompleteRun(runId, RunStatus.Failed, 0, 0, "Login failed for user 'svc_datasync'.");

        var notification = Assert.Single(_store.List());
        Assert.Equal(NotificationKinds.RunFailed, notification.Kind);
        Assert.Equal("crm-sync", notification.TaskName);
        Assert.Equal("orders", notification.MappingName);
        Assert.Equal(runId, notification.RunId);
        // The message has to identify what failed on its own — it is what a bell renders, with
        // nothing else on screen to give it context.
        Assert.Contains("crm-sync", notification.Message);
        Assert.Contains("orders", notification.Message);
        Assert.Contains("Login failed", notification.Message);
    }

    [Fact]
    public void ASucceededRun_ProducesNothing()
    {
        _runs.CompleteRun(QueueAndBegin("crm-sync", "orders"), RunStatus.Succeeded, 10, 10, errorSummary: null);

        Assert.Empty(_store.List());
    }

    [Fact]
    public void ACompletionArrivingTwice_AnnouncesOnce()
    {
        var runId = QueueAndBegin("crm-sync", "orders");

        // Journal recovery replaying an entry whose original write did land, or the supervisor
        // reaping a process whose own failure report was already in flight. Both are real, and a
        // second announcement of one failure is noise a reader cannot tell from a second failure.
        _runs.CompleteRun(runId, RunStatus.Failed, 0, 0, "Connection reset.");
        _runs.CompleteRun(runId, RunStatus.Failed, 0, 0, "Connection reset.");

        Assert.Single(_store.List());
    }

    [Fact]
    public void PausingAReplication_ProducesOneNotification_NamingWhoAndWhy()
    {
        _runs.SetPaused("crm-sync", paused: true, note: "target disk full", performedBy: "alice");

        var notification = Assert.Single(_store.List());
        Assert.Equal(NotificationKinds.ReplicationPaused, notification.Kind);
        Assert.Equal("crm-sync", notification.TaskName);
        // No run and no mapping: a pause is about the replication, and inventing either would make the
        // row link somewhere it does not belong.
        Assert.Null(notification.MappingName);
        Assert.Null(notification.RunId);
        Assert.Contains("crm-sync", notification.Message);
        Assert.Contains("alice", notification.Message);
        Assert.Contains("target disk full", notification.Message);
    }

    [Fact]
    public void PausingWithNoNote_StillReadsAsASentence()
    {
        _runs.SetPaused("crm-sync", paused: true, note: null, performedBy: "alice");

        Assert.DoesNotContain("—", Assert.Single(_store.List()).Message);
    }

    [Fact]
    public void ResumingAReplication_ProducesNothing()
    {
        _runs.SetPaused("crm-sync", paused: true, note: null, performedBy: "alice");
        _runs.SetPaused("crm-sync", paused: false, note: "disk extended", performedBy: "alice");

        // Only the pause. A resume is the world going back to how it is supposed to be, which nobody
        // needs pushed at them — and the PauseEvents row still records it either way.
        var notification = Assert.Single(_store.List());
        Assert.Equal(NotificationKinds.ReplicationPaused, notification.Kind);
        Assert.Equal(2, _runs.GetPauseHistory("crm-sync").Count);
    }

    [Fact]
    public void PausingAnAlreadyPausedReplication_AnnouncesOnce()
    {
        _runs.SetPaused("crm-sync", paused: true, note: "first look", performedBy: "alice");
        _runs.SetPaused("crm-sync", paused: true, note: "still looking", performedBy: "bob");

        // The second call is a real act and keeps its own PauseEvents row — re-pausing with a new note
        // is how an operator updates the reason. It is not news twice, though: announcing it again
        // would make one stuck replication look like a spreading outage.
        Assert.Single(_store.List());
        Assert.Equal(2, _runs.GetPauseHistory("crm-sync").Count);
    }

    [Fact]
    public void AnExpiredPosition_IsItsOwnKind_AndSaysWhatExpired()
    {
        var runId = QueueAndBegin("crm-sync", "orders");

        // The wording RunExecutor passes through from PositionExpiredException, which already names
        // the mechanism, the table and both positions.
        _runs.CompleteRun(
            runId, RunStatus.Failed, 0, 0,
            "Change Tracking history for 'dbo.Orders' no longer covers position '42' "
                + "(the oldest still available is '95').",
            RunFailureKinds.PositionExpired);

        var notification = Assert.Single(_store.List());
        Assert.Equal(NotificationKinds.PositionExpired, notification.Kind);
        Assert.Equal("orders", notification.MappingName);
        Assert.Equal(runId, notification.RunId);
        Assert.Contains("dbo.Orders", notification.Message);
        Assert.Contains("42", notification.Message);
        Assert.Contains("95", notification.Message);
    }

    [Fact]
    public void AnOrdinaryFailureAndAnExpiry_AreDistinguishableWithoutReadingTheMessage()
    {
        _runs.CompleteRun(QueueAndBegin("a", "one"), RunStatus.Failed, 0, 0, "Connection reset.");
        _runs.CompleteRun(
            QueueAndBegin("b", "two"), RunStatus.Failed, 0, 0, "history no longer covers position",
            RunFailureKinds.PositionExpired);

        // The expiry is the one failure with a known one-click fix. A feed has to be able to find
        // those by kind rather than by matching on prose.
        Assert.Equal(
            [NotificationKinds.RunFailed, NotificationKinds.PositionExpired],
            _store.List().Select(n => n.Kind));
    }

    /// <summary>
    /// Phase 91's own failure, on the exact same footing as the expiry test above: this is the "no new
    /// wiring" claim made checkable — a MetadataNotCached completion rides CompleteRun's existing
    /// producer and needs nothing else touched to reach the feed.
    /// </summary>
    [Fact]
    public void AMetadataNotCachedFailure_ProducesItsOwnNotificationKind_NamingTheMappingAndColumn()
    {
        var runId = QueueAndBegin("crm-sync", "orders");

        // The wording RunExecutor passes through from MetadataNotCachedException, which already names
        // the mapping, the side and the column.
        _runs.CompleteRun(
            runId, RunStatus.Failed, 0, 0,
            "Table mapping 'orders' has no cached source column 'UpdatedAt'. The mapping's cached "
                + "source shape doesn't include it — the table may have changed since it was last "
                + "captured. Use Refresh metadata on the mapping to populate it before this run can proceed.",
            RunFailureKinds.MetadataNotCached);

        var notification = Assert.Single(_store.List());
        Assert.Equal(NotificationKinds.MetadataNotCached, notification.Kind);
        Assert.Equal("crm-sync", notification.TaskName);
        Assert.Equal("orders", notification.MappingName);
        Assert.Equal(runId, notification.RunId);
        Assert.Contains("orders", notification.Message);
        Assert.Contains("UpdatedAt", notification.Message);
        Assert.Contains("Refresh metadata", notification.Message);
    }

    [Fact]
    public void List_WithSinceId_ReturnsOnlyWhatIsNewer()
    {
        _runs.CompleteRun(QueueAndBegin("a", "one"), RunStatus.Failed, 0, 0, "first");
        _runs.CompleteRun(QueueAndBegin("b", "two"), RunStatus.Failed, 0, 0, "second");
        _runs.CompleteRun(QueueAndBegin("c", "three"), RunStatus.Failed, 0, 0, "third");

        var all = _store.List();
        Assert.Equal(3, all.Count);
        // Ascending, so a poller's "highest Id I hold" is the last element rather than a scan.
        Assert.Equal(["a", "b", "c"], all.Select(n => n.TaskName));

        var newer = _store.List(sinceId: all[0].Id);
        Assert.Equal(["b", "c"], newer.Select(n => n.TaskName));
        Assert.Empty(_store.List(sinceId: all[^1].Id));
    }

    [Fact]
    public void TwoUsersCursors_AreIndependent()
    {
        _runs.CompleteRun(QueueAndBegin("a", "one"), RunStatus.Failed, 0, 0, "first");
        _runs.CompleteRun(QueueAndBegin("b", "two"), RunStatus.Failed, 0, 0, "second");
        var latest = _store.List()[^1].Id;

        _store.MarkSeen("user-alice", latest);

        Assert.Equal(latest, _store.GetCursor("user-alice"));
        Assert.Equal(0, _store.UnreadCount(_store.GetCursor("user-alice")));

        // Bob has acknowledged nothing, so everything is still his to read. A shared cursor would
        // have silenced this the moment Alice looked.
        Assert.Null(_store.GetCursor("user-bob"));
        Assert.Equal(2, _store.UnreadCount(_store.GetCursor("user-bob")));
    }

    [Fact]
    public void MarkSeen_NeverMovesACursorBackwards()
    {
        _runs.CompleteRun(QueueAndBegin("a", "one"), RunStatus.Failed, 0, 0, "first");
        _runs.CompleteRun(QueueAndBegin("b", "two"), RunStatus.Failed, 0, 0, "second");
        var feed = _store.List();

        _store.MarkSeen("user-alice", feed[^1].Id);
        // A second tab, holding a staler view, acknowledging what it had. It must not resurrect what
        // the first one already cleared.
        _store.MarkSeen("user-alice", feed[0].Id);

        Assert.Equal(feed[^1].Id, _store.GetCursor("user-alice"));
        Assert.Equal(0, _store.UnreadCount(_store.GetCursor("user-alice")));
    }

    [Fact]
    public void PruneNotifications_DeletesOnlyRowsPastTheWindow()
    {
        _runs.CompleteRun(QueueAndBegin("old", "one"), RunStatus.Failed, 0, 0, "ancient");
        Backdate(_store.List()[0].Id, TimeSpan.FromDays(365));
        _runs.CompleteRun(QueueAndBegin("new", "two"), RunStatus.Failed, 0, 0, "recent");

        Assert.Equal(1, _store.PruneNotifications(TimeSpan.FromDays(90)));
        Assert.Equal("new", Assert.Single(_store.List()).TaskName);
    }

    [Fact]
    public void PruneNotifications_WithNoWindow_DeletesNothing()
    {
        _runs.CompleteRun(QueueAndBegin("old", "one"), RunStatus.Failed, 0, 0, "ancient");
        Backdate(_store.List()[0].Id, TimeSpan.FromDays(3650));

        // Null is "keep everything", matching how every other retention cap here reads a configured 0.
        Assert.Equal(0, _store.PruneNotifications(maxAge: null));
        Assert.Single(_store.List());
    }

    [Fact]
    public void Raise_WritesANotificationWithNoPairedEvent()
    {
        // Phase 82's certificate-expiry check is the first producer with nothing to be atomic with —
        // see Raise's own doc comment. Unlike every _runs.CompleteRun/_runs.SetPaused call above, this
        // one has no other table write alongside it.
        _store.Raise(NotificationKinds.CertificateExpiring, "The bound certificate expires in 12 days.");

        var notification = Assert.Single(_store.List());
        Assert.Equal(NotificationKinds.CertificateExpiring, notification.Kind);
        Assert.Null(notification.TaskName);
        Assert.Null(notification.MappingName);
        Assert.Null(notification.RunId);
        Assert.Contains("12 days", notification.Message);
    }

    [Fact]
    public void Raise_CertificateExpired_IsItsOwnKind_DistinctFromExpiring()
    {
        _store.Raise(NotificationKinds.CertificateExpiring, "expires soon");
        _store.Raise(NotificationKinds.CertificateExpired, "already expired");

        Assert.Equal(
            [NotificationKinds.CertificateExpiring, NotificationKinds.CertificateExpired],
            _store.List().Select(n => n.Kind));
    }

    [Fact]
    public void PruningTheFeed_LeavesCursorsAlone()
    {
        _runs.CompleteRun(QueueAndBegin("old", "one"), RunStatus.Failed, 0, 0, "ancient");
        var id = _store.List()[0].Id;
        _store.MarkSeen("user-alice", id);
        Backdate(id, TimeSpan.FromDays(365));

        _store.PruneNotifications(TimeSpan.FromDays(90));

        // The cursor now names an Id that no longer exists, which is exactly right: it means
        // "nothing unread", which is what somebody who read everything should still see.
        Assert.Equal(id, _store.GetCursor("user-alice"));
        Assert.Equal(0, _store.UnreadCount(_store.GetCursor("user-alice")));
    }
}
