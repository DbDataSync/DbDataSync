using System.Net;
using System.Text;
using DbDataSync.Core.Config;
using DbDataSync.State.Remote;

namespace DbDataSync.State.Tests;

/// <summary>
/// What the remote client does when the owner is not there. The split it enforces is the point of
/// phase 39: a prerequisite that cannot reach the owner fails, because proceeding would mean assuming
/// a claim nobody granted; an outcome is written to disk, because the work is already done.
/// </summary>
public sealed class RemoteRunnerStateTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-remote-state-tests-").FullName;
    private readonly List<string> _reports = [];
    private readonly Guid _runId = Guid.NewGuid();

    private string StateDbPath => Path.Combine(_root, "state.db");
    private string JournalPath => StateJournal.PathFor(StateDbPath, "sales", _runId);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private RemoteRunnerState Create(FakeOwner owner, TimeSpan? grace = null) =>
        new(new HttpClient(owner) { BaseAddress = new Uri("http://127.0.0.1:1") },
            new StateJournal(JournalPath),
            grace ?? TimeSpan.Zero,
            _reports.Add,
            retryDelay: TimeSpan.FromMilliseconds(1));

    // ---- Prerequisites ----

    [Fact]
    public void A_prerequisite_fails_once_the_grace_period_is_spent_rather_than_inventing_an_answer()
    {
        var owner = new FakeOwner { Reachable = false };
        using var state = Create(owner);

        var ex = Assert.Throws<StateOwnerUnavailableException>(() => state.TryClaimNext("sales", "worker-1", RunLane.ChangeProcessing));

        Assert.Contains("did not respond", ex.Message);
        Assert.True(state.OwnerLost);
        // Nothing spilled: there is no outcome here to preserve, and replaying an invented claim would
        // grant this runner something the owner never gave it.
        Assert.False(File.Exists(JournalPath));
    }

    [Fact]
    public void A_prerequisite_is_retried_for_the_grace_period_and_succeeds_if_the_owner_comes_back()
    {
        var owner = new FakeOwner { Reachable = false };
        owner.OnRequest = _ => { if (owner.Requests.Count >= 3) owner.Reachable = true; };
        using var state = Create(owner, grace: TimeSpan.FromSeconds(5));

        Assert.True(state.HasOutstandingWork("sales", RunLane.ChangeProcessing));

        Assert.False(state.OwnerLost);
        Assert.Single(_reports);
        Assert.Contains("not responding", _reports[0]);
    }

    // ---- Outcomes ----

    [Fact]
    public void An_outcome_that_cannot_be_delivered_is_journalled_rather_than_lost()
    {
        using (var state = Create(new FakeOwner { Reachable = false }))
        {
            state.MarkDone(11);
            state.CompleteRun(_runId, RunStatus.Succeeded, 5, 5, null);
        }

        var entries = StateJournal.Read(JournalPath);
        Assert.Equal(
            [JournalOperation.MarkDone, JournalOperation.CompleteRun],
            entries.Select(e => e.Operation));
        Assert.Equal(11, StateJournal.PayloadOf<WorkItemRequest>(entries[0])!.WorkItemId);
    }

    /// <summary>
    /// A claimed item that did not finish must come back as released, never as done — a runner that
    /// spilled MarkDone for work it abandoned would drop that work silently, which is the one failure
    /// this whole mechanism exists to avoid.
    /// </summary>
    [Fact]
    public void An_abandoned_work_item_is_journalled_as_released_not_as_done()
    {
        using (var state = Create(new FakeOwner { Reachable = false }))
            state.ReleaseClaim(11);

        var entries = StateJournal.Read(JournalPath);
        Assert.Equal(JournalOperation.ReleaseClaim, Assert.Single(entries).Operation);
        Assert.DoesNotContain(JournalOperation.MarkDone, entries.Select(e => e.Operation));
    }

    /// <summary>
    /// "The owner is gone" and "the owner said no" are different things. A 4xx is an answer — a bug to
    /// surface — and spilling on it would journal work the owner had already refused.
    /// </summary>
    [Fact]
    public void A_rejected_request_is_raised_rather_than_journalled()
    {
        var owner = new FakeOwner { Status = HttpStatusCode.Unauthorized };
        using var state = Create(owner);

        Assert.Throws<HttpRequestException>(() => state.MarkDone(11));

        Assert.False(state.OwnerLost);
        Assert.False(File.Exists(JournalPath));
    }

    [Fact]
    public void Once_the_owner_is_declared_lost_nothing_waits_on_the_network_again()
    {
        var owner = new FakeOwner { Reachable = false };
        using var state = Create(owner);

        state.MarkDone(1);
        var afterFirst = owner.Requests.Count;
        state.MarkDone(2);
        state.MarkFailed(3);

        // A clean shutdown should not re-spend the grace period per call.
        Assert.Equal(afterFirst, owner.Requests.Count);
        Assert.Equal(3, StateJournal.Read(JournalPath).Count);
    }

    // ---- Read intent and hold (phase 100) ----

    /// <summary>Nothing consumes these yet, but an owner that goes away must not silently drop them any
    /// more than it drops a watermark — the whole point of wiring the journal through now.</summary>
    [Fact]
    public void SetReadIntent_and_SetReadHold_are_journalled_like_any_other_outcome_when_the_owner_is_gone()
    {
        using (var state = Create(new FakeOwner { Reachable = false }))
        {
            state.SetReadIntent("sales", "orders", "dbo.Orders", ReadIntent.ChangesFromEarliest);
            state.SetReadHold("sales", "orders", "dbo.Orders", ReadHold.PositionExpired);
        }

        var entries = StateJournal.Read(JournalPath);
        Assert.Equal(
            [JournalOperation.SetReadIntent, JournalOperation.SetReadHold],
            entries.Select(e => e.Operation));
        Assert.Equal(
            ReadIntent.ChangesFromEarliest,
            StateJournal.PayloadOf<SetReadIntentRequest>(entries[0])!.Intent);
        Assert.Equal(
            ReadHold.PositionExpired,
            StateJournal.PayloadOf<SetReadHoldRequest>(entries[1])!.Hold);
    }

    // ---- Logs ----

    [Fact]
    public void Log_lines_are_batched_into_one_request_rather_than_one_each()
    {
        var owner = new FakeOwner();
        using var state = Create(owner);

        for (var i = 0; i < 50; i++)
            state.Log(_runId, LogSeverity.Info, $"line {i}");
        state.Flush();

        Assert.Equal($"{StateProtocol.Route}/log-batch", Assert.Single(owner.Requests));
    }

    /// <summary>Journalled per line rather than per batch, so a file truncated by a kill loses one
    /// entry rather than two hundred.</summary>
    [Fact]
    public void Buffered_logs_are_journalled_one_entry_per_line_when_the_owner_is_gone()
    {
        using (var state = Create(new FakeOwner { Reachable = false }))
        {
            state.Log(_runId, LogSeverity.Info, "first");
            state.Log(_runId, LogSeverity.Error, "second");
        }

        var entries = StateJournal.Read(JournalPath);
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal(JournalOperation.Log, e.Operation));
        Assert.Equal("first", StateJournal.PayloadOf<LogRequest>(entries[0])!.Message);
        Assert.Equal(LogSeverity.Error, StateJournal.PayloadOf<LogRequest>(entries[1])!.Level);
    }

    [Fact]
    public void The_token_is_not_sent_by_this_class__the_handler_it_is_given_carries_it()
    {
        // Guards the shape rather than the value: the token belongs to the HttpClient this is
        // constructed with, so nothing here can leak it into a URL or a body.
        var owner = new FakeOwner();
        using var state = Create(owner);
        state.HasOutstandingWork("sales", RunLane.ChangeProcessing);

        Assert.DoesNotContain(owner.Requests, r => r.Contains("token", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Stands in for the owning process: records what was asked, and can be switched between
    /// answering, refusing, and being unreachable.
    /// </summary>
    private sealed class FakeOwner : HttpMessageHandler
    {
        public bool Reachable { get; set; } = true;
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public List<string> Requests { get; } = [];
        public Action<HttpRequestMessage>? OnRequest { get; set; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsolutePath);
            OnRequest?.Invoke(request);

            if (!Reachable)
                throw new HttpRequestException("Connection refused.");

            return new HttpResponseMessage(Status)
            {
                Content = new StringContent(Body(request.RequestUri!.AbsolutePath), Encoding.UTF8, "application/json"),
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));

        private static string Body(string path) => path switch
        {
            var p when p.EndsWith("has-outstanding-work") => """{"value":true}""",
            var p when p.EndsWith("try-acquire-lock") => """{"value":true}""",
            var p when p.EndsWith("try-claim-next") => """{"item":null}""",
            var p when p.EndsWith("watermark") => """{"watermark":null}""",
            _ => "{}",
        };
    }
}
