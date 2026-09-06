using System.Runtime.ExceptionServices;
using System.Collections.Immutable;
using System.Data.Common;
using System.Diagnostics;
using System.Threading.Channels;
using ClrKernel.Core.Secrets;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.Drivers.MsSql;
using DbDataSync.Drivers.Postgres;
using DbDataSync.Scripting;
using DbDataSync.Scripting.Abstractions;
using DbDataSync.State;
using DbDataSync.State.Remote;
using DbDataSync.Verification;
using DbDataSync.Core.Sql;

namespace DbDataSync.TaskRunner;

/// <summary>
/// The read -> stage -> apply -> watermark-update pipeline from architecture/detailed-design.md §3.3,
/// driven by a durable work queue rather than a fixed, up-front list of mappings — see
/// architecture/implementation/done/phase-008-work-queue-schema.md. One process (spawned by
/// DbDataSync.Api.Services.ProcessSupervisor) claims and drains a replication's pending WorkQueue items
/// with bounded internal concurrency, rather than one process being spawned per triggered run: a
/// replication can have hundreds of table mappings, and backfills for many of them can be queued
/// continuously, so a fixed-collection fan-out (Parallel.ForEach/Task.WhenAll over an enumerated list)
/// doesn't fit — new work keeps arriving while the queue is being drained.
/// <para>
/// Each table mapping's own unit of work (a Primary pass, or one segment of a Backfill) is independent
/// — its own RunId, its own (TaskName, RunKind, MappingName) lock, its own TaskRuns row — not one
/// shared run/lock/row for the whole replication. This is what lets one slow or locked mapping stop
/// blocking every other mapping.
/// </para>
/// </summary>
public sealed class RunExecutor(
    ConfigRepository configRepository,
    DriverRegistry driverRegistry,
    SecretStore secretStore,
    IRunnerState state,
    /// <summary>How the one config write a run can make — its provisioning report, phase 94 — reaches
    /// the process that owns the repository. Never a <see cref="ConfigRepository"/> write of its own:
    /// this process reads config and reports to the owner, for the reason it does the same with
    /// state.</summary>
    IRunnerConfig runnerConfig,
    ScriptHost scriptHost,
    /// <summary>Not opened — this process never touches the state file (phase 39). It is here because
    /// a verification result is written beside it, which is the one directory every process in a
    /// deployment already agrees on.</summary>
    string stateDbPath)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// When this worker last did something that counts as finding changes — a pass that read at
    /// least one row. Written by consumers on any thread, read by the producer, so it is a tick count
    /// swapped atomically rather than a <see cref="DateTimeOffset"/> field.
    /// </summary>
    private long _lastProductiveTicks = DateTimeOffset.UtcNow.UtcTicks;

    private void MarkProductive() =>
        Interlocked.Exchange(ref _lastProductiveTicks, DateTimeOffset.UtcNow.UtcTicks);

    private TimeSpan IdleFor() =>
        DateTimeOffset.UtcNow - new DateTimeOffset(Interlocked.Read(ref _lastProductiveTicks), TimeSpan.Zero);

    /// <summary>
    /// Claims and processes this replication's pending WorkQueue items until the queue is drained,
    /// running <b>two independent lanes</b> — <see cref="RunLane.ChangeProcessing"/>
    /// (<c>RunKind.Primary</c>) and <see cref="RunLane.Backfill"/> (<c>RunKind.Backfill</c> +
    /// <c>RunKind.Verification</c>) — each with its own bounded channel and its own pool of consumers,
    /// sized by <paramref name="lanes"/>. A long-running reload in one lane can never occupy a slot the
    /// other lane needs. Per-item outcomes live in TaskRuns/WorkQueue, not this method's return value —
    /// the process exit code only distinguishes "the worker ran and drained cleanly" from "the worker
    /// itself failed to start".
    /// <para>
    /// The change-processing lane owns the process lifetime — it runs today's continuous idle-timeout
    /// / periodic-drain logic. The backfill lane rides the process: under a continuous replication it
    /// keeps polling for reloads rather than exiting on its own, and winds down only once the
    /// change-processing lane has (its producer's <c>finally</c> cancels <c>winddown</c>). That is
    /// what keeps a backfill enqueued mid-life from sitting unclaimed until the worker respawns.
    /// </para>
    /// </summary>
    public async Task<ExitCode> ExecuteWorkerAsync(string taskName, WorkerLanes lanes, CancellationToken cancellationToken)
    {
        ReplicationTaskConfig task;
        try
        {
            task = configRepository.LoadReplicationTask(taskName);
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine($"Config error: {ex.Message}");
            return ExitCode.ConfigError;
        }

        state.UpsertTask(task.Name, task.Enabled);

        var workerId = Guid.NewGuid().ToString("N");
        using var winddown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        async Task ChangeLane()
        {
            try
            {
                await RunLaneAsync(
                    taskName, task.Scheduling, RunLane.ChangeProcessing, lanes.ChangeProcessing,
                    workerId, winddown: cancellationToken, hardStop: cancellationToken);
            }
            finally
            {
                // The process's lifetime is the change lane's. Whether it drained cleanly or crashed,
                // the backfill lane — which otherwise rides a continuous replication's process
                // forever — is now free to do its last drain and stop.
                winddown.Cancel();
            }
        }

        var change = ChangeLane();
        var backfill = RunLaneAsync(
            taskName, task.Scheduling, RunLane.Backfill, lanes.Backfill,
            workerId, winddown: winddown.Token, hardStop: cancellationToken);

        // WhenAll waits for both even if one faults, so a lane failing still lets the other finish
        // recording what it was mid-way through — the journal-vs-lost-work line the single-lane
        // version drew for producer-vs-consumers, now drawn between the lanes too. Awaited (not
        // .Wait()ed) so cancellation surfaces as OperationCanceledException, which is exactly what
        // Program.cs and the tests expect on Ctrl+C.
        ExceptionDispatchInfo? failure = null;
        try
        {
            await Task.WhenAll(change, backfill);
        }
        catch (Exception ex)
        {
            failure = ExceptionDispatchInfo.Capture(ex);
        }

        state.Flush();
        failure?.Throw();

        return ExitCode.Success;
    }

    /// <summary>One lane: its own bounded channel, one producer claiming only that lane's
    /// <see cref="RunKind"/>s, and <paramref name="degreeOfParallelism"/> consumers. Mirrors what
    /// <see cref="ExecuteWorkerAsync"/> used to do for the whole worker — await the producer (capturing
    /// its failure), then await every consumer so nothing in flight is lost, then rethrow.</summary>
    private async Task RunLaneAsync(
        string taskName, SchedulingConfig scheduling, RunLane lane, int degreeOfParallelism,
        string workerId, CancellationToken winddown, CancellationToken hardStop)
    {
        var channel = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(Math.Max(1, degreeOfParallelism) * 2)
        {
            SingleWriter = true,
            SingleReader = degreeOfParallelism == 1,
        });

        var producer = ProduceAsync(taskName, scheduling, lane, workerId, channel.Writer, winddown, hardStop);
        var consumers = Enumerable.Range(0, Math.Max(1, degreeOfParallelism))
            .Select(_ => ConsumeAsync(channel.Reader, hardStop))
            .ToArray();

        ExceptionDispatchInfo? producerFailure = null;
        try
        {
            await producer;
        }
        catch (Exception ex)
        {
            producerFailure = ExceptionDispatchInfo.Capture(ex);
        }

        await Task.WhenAll(consumers);
        producerFailure?.Throw();
    }

    /// <summary>Empty polls (queue observed to have no outstanding work) required before a
    /// <see cref="ScheduleMode.Periodic"/> worker actually exits. Without this grace period, a worker
    /// that happens to finish draining at almost exactly the moment
    /// ProcessSupervisor.EnsureWorkerRunning is called (it no-ops if a tracked process is still alive,
    /// cheaper than always spawning fresh) can exit a moment before claiming newly-enqueued work — and
    /// nothing else re-triggers a worker for a one-off manual trigger that didn't come from
    /// SchedulerService's own due-ness check. This narrows that race to needing an enqueue to land
    /// during this specific window, rather than eliminating it structurally — a full fix (e.g. a worker
    /// heartbeat EnsureWorkerRunning can check against, not just OS process liveness) is real
    /// follow-on work, not built here. See
    /// architecture/implementation/done/phase-008-work-queue-schema.md.
    /// <para>
    /// A <see cref="ScheduleMode.Continuous"/> worker uses the idle timeout instead, which subsumes
    /// this: it is measured in tens of seconds rather than in polls.
    /// </para>
    /// </summary>
    private const int EmptyPollsBeforeExit = 5;

    /// <summary>Claims the next available item for one lane in a loop, feeding it to that lane's
    /// bounded channel (which applies backpressure once consumers fall behind), until the lane has
    /// nothing left to do.
    /// <para>
    /// The <b>change-processing lane</b> exits on today's rules: a periodic worker after
    /// <see cref="EmptyPollsBeforeExit"/> empty polls, a continuous one once it has gone a whole idle
    /// timeout without a <c>Primary</c> pass reading anything.
    /// </para>
    /// <para>
    /// The <b>backfill lane</b> follows the change lane. Under a periodic replication it drains and
    /// exits the same way. Under a continuous one it never exits on its own — an empty backfill queue
    /// is the normal state and the process is staying up for the change lane anyway — so it keeps
    /// polling until <paramref name="winddown"/> is cancelled (the change lane's producer finished),
    /// then does one last drain and stops. Without that, a backfill enqueued while the worker is alive
    /// would sit unclaimed until the worker respawned.
    /// </para></summary>
    private async Task ProduceAsync(
        string taskName, SchedulingConfig scheduling, RunLane lane, string workerId,
        ChannelWriter<WorkItem> writer, CancellationToken winddown, CancellationToken hardStop)
    {
        var runsContinuously = scheduling.Mode == ScheduleMode.Continuous;
        var ridesTheProcess = lane == RunLane.Backfill && runsContinuously;
        var consecutiveEmptyPolls = 0;
        try
        {
            while (!hardStop.IsCancellationRequested)
            {
                var item = state.TryClaimNext(taskName, workerId, lane);
                if (item is not null)
                {
                    consecutiveEmptyPolls = 0;
                    await writer.WriteAsync(item, hardStop);
                    continue;
                }

                // The backfill lane winds down with the process. Checked here — after one last claim
                // sweep, before anything that could wait — so a Running row we do not own (an orphan
                // the reconciler will release, or the other lane's item) cannot keep this lane alive
                // past the change lane's own exit.
                if (ridesTheProcess && winddown.IsCancellationRequested)
                    break;

                if (state.HasOutstandingWork(taskName, lane))
                {
                    // Consumers are still busy. Poll quickly — this is not idleness, it is waiting for
                    // a colleague.
                    consecutiveEmptyPolls = 0;
                    await Task.Delay(PollInterval, hardStop);
                    continue;
                }

                if (ridesTheProcess)
                {
                    // Nothing to reload right now, and the change lane has not wound down — stay with
                    // the process, which is up for that lane.
                    await Task.Delay(PollInterval, hardStop);
                    continue;
                }

                if (!runsContinuously)
                {
                    if (++consecutiveEmptyPolls >= EmptyPollsBeforeExit)
                        break;
                    await Task.Delay(PollInterval, hardStop);
                    continue;
                }

                // Continuous change-processing lane: an empty queue is the normal state between
                // passes, not a reason to go. It leaves only once it has gone a whole idle timeout
                // without a Primary pass reading anything — under a live load that never arrives, so
                // the process stays up instead of being respawned several times a minute.
                if (IdleFor() >= scheduling.IdleTimeout)
                    break;

                await WaitForWorkAsync(taskName, lane, scheduling.Frequency, hardStop);
            }
        }
        finally
        {
            writer.Complete();
        }
    }

    /// <summary>
    /// Waits out one interval between passes, and stops early the moment there is something to claim
    /// in this lane.
    /// <para>
    /// The interval is the configured frequency because that is when the next pass is due; sleeping
    /// straight through it would be right if nothing else could enqueue work, and something can — a
    /// person pressing Run Now. A manual trigger no-ops
    /// <c>ProcessSupervisor.EnsureWorkerRunning</c> while this process is alive, so if this slept the
    /// full minute, so would they.
    /// </para>
    /// </summary>
    private async Task WaitForWorkAsync(string taskName, RunLane lane, TimeSpan interval, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + interval;
        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            await Task.Delay(remaining < PollInterval ? remaining : PollInterval, cancellationToken);
            // Nothing is in flight at this point — the queue was empty when we got here — so anything
            // outstanding is something to claim.
            if (state.HasOutstandingWork(taskName, lane))
                return;
        }
    }

    private async Task ConsumeAsync(ChannelReader<WorkItem> reader, CancellationToken cancellationToken)
    {
        await foreach (var item in reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                await ProcessWorkItemAsync(item, cancellationToken);
            }
            catch (StateOwnerUnavailableException)
            {
                // Stop taking work rather than starting what cannot be recorded. Whatever is still
                // buffered stays claimed; the API releases it when it finds this process gone.
                return;
            }
        }
    }

    private async Task ProcessWorkItemAsync(WorkItem item, CancellationToken cancellationToken)
    {
        if (!state.TryAcquireLock(item.TaskName, item.RunKind, item.MappingName, item.RunId))
        {
            // Lost the real RunLocks race despite the queue's own NOT EXISTS pre-filter (rare) — give
            // the claim back so a later poll retries it, rather than recording a spurious failure.
            state.ReleaseClaim(item.Id);
            return;
        }

        state.MarkRunning(item.Id);
        state.BeginRun(item.RunId, Environment.ProcessId);
        var scope = item.SegmentLabel == WorkQueueStore.NoSegment ? "" : $", segment {item.SegmentLabel}";
        Log(item.RunId, LogSeverity.Info, $"Run started for mapping '{item.MappingName}' ({item.RunKind}{scope}).");

        try
        {
            var task = configRepository.LoadReplicationTask(item.TaskName);
            var mapping = configRepository.LoadTableMapping(item.TaskName, item.MappingName);
            if (mapping.Sources.Count != 1 || mapping.Targets.Count != 1)
                throw new ConfigValidationException(
                    $"Table mapping '{mapping.Name}' has {mapping.Sources.Count} source(s) and " +
                    $"{mapping.Targets.Count} target(s) — DbDataSync.TaskRunner only executes 1:1 " +
                    "mappings in v1 (the config schema allows more for future fan-in/fan-out).");

            long rowsRead;
            long rowsWritten;
            // A verification run reads both sides and writes nothing, so the reader/staging/writer
            // stages this traces do not exist for it. Null rather than zeros: it was not measured.
            RunTiming? timing = null;
            // Null for anything that did not advance a durable position — a Verification, a Backfill,
            // or a pass that found nothing new. See WatermarkChange.
            WatermarkChange? watermark = null;
            if (item.RunKind == RunKind.Verification)
                (rowsRead, rowsWritten) = await RunVerificationAsync(task, mapping, item, cancellationToken);
            else
                (rowsRead, rowsWritten, timing, watermark) =
                    await RunMappingAsync(task, mapping, item, cancellationToken);

            Log(item.RunId, LogSeverity.Info, item.RunKind == RunKind.Verification
                ? $"Verification finished: {rowsRead} group(s) compared."
                : $"Run succeeded: {rowsRead} row(s) read, {rowsWritten} row(s) written.");
            // Flush before CompleteRun, not after: one worker process now handles many runs over its
            // lifetime, so a run's TaskRuns row can go terminal (and RunMonitorService stop watching
            // it) long before the process itself exits — unlike the old one-run-per-process model,
            // where a single end-of-process Flush() was always guaranteed to happen first. Log lines
            // must be durable before the status that makes a poller stop looking for them.
            state.Flush();
            state.CompleteRun(
                item.RunId, RunStatus.Succeeded, rowsRead, rowsWritten, errorSummary: null, timing: timing,
                previousWatermark: watermark?.Previous, newWatermark: watermark?.New);
            state.MarkDone(item.Id);

            // Idle is "looked for changes and found none", not "the queue is empty" — the queue is
            // empty for a moment after every single pass, which is why the worker used to exit
            // straight through a live load. A pass that read nothing leaves the clock running.
            //
            // Only a Primary pass counts: the idle timeout is the *change-processing* lane's, and a
            // backfill or a verification reading rows says nothing about whether incremental changes
            // are still arriving. A continuous worker held up purely by backfills running is exactly
            // the coupling this phase removed.
            if (rowsRead > 0 && item.RunKind == RunKind.Primary)
                MarkProductive();
        }
        catch (Exception ex) when (ex is FileNotFoundException or ConfigValidationException)
        {
            Log(item.RunId, LogSeverity.Error, $"Config error: {ex.Message}");
            state.Flush();
            state.CompleteRun(item.RunId, RunStatus.Failed, 0, 0, ex.Message, errorDetail: ex.ToString());
            state.MarkFailed(item.Id);
        }
        catch (PositionExpiredException ex)
        {
            // A failure with a known fix, so it is recorded as one. The status stays Failed — the pass
            // did not happen, and a separate status would have dropped it out of every "how many
            // failed" count — and FailureKind is what lets the Runs tab offer the reload instead of
            // leaving an operator to work out that a reload is what this needs.
            Log(item.RunId, LogSeverity.Error, ex.Message);
            state.Flush();
            state.CompleteRun(
                item.RunId, RunStatus.Failed, 0, 0, ex.Message, RunFailureKinds.PositionExpired,
                errorDetail: ex.ToString());
            state.MarkFailed(item.Id);
        }
        catch (MetadataNotCachedException ex)
        {
            // Phase 91's own failure, on the same footing as PositionExpiredException just above: a
            // known cause with a known fix (Refresh metadata, not a reload), so it gets its own
            // FailureKind rather than falling into the generic catch below and reading like a mystery.
            Log(item.RunId, LogSeverity.Error, ex.Message);
            state.Flush();
            state.CompleteRun(
                item.RunId, RunStatus.Failed, 0, 0, ex.Message, RunFailureKinds.MetadataNotCached,
                errorDetail: ex.ToString());
            state.MarkFailed(item.Id);
        }
        catch (ConnectivityException ex)
        {
            Log(item.RunId, LogSeverity.Error, $"Run failed: {ex.Message}");
            state.Flush();
            state.CompleteRun(item.RunId, RunStatus.Failed, 0, 0, ex.Message, errorDetail: ex.ToString());
            state.MarkFailed(item.Id);
        }
        // Deliberately not caught: the owner being gone is not this item failing. Recording it as
        // Failed would be this process asserting an outcome it is in no position to observe — and it
        // is the one exception that must reach ConsumeAsync, which stops rather than starting more.
        catch (Exception ex) when (ex is not StateOwnerUnavailableException)
        {
            Log(item.RunId, LogSeverity.Error, $"Run failed: {ex.Message}");
            state.Flush();
            state.CompleteRun(item.RunId, RunStatus.Failed, 0, 0, ex.Message, errorDetail: ex.ToString());
            state.MarkFailed(item.Id);
        }
        finally
        {
            state.ReleaseLock(item.TaskName, item.RunKind, item.MappingName);
        }
    }

    /// <summary>
    /// Compares this mapping's source and target and writes each check's result to disk.
    /// <para>
    /// The parquet goes **straight to the filesystem**, not through the state channel: a result is a
    /// standalone artifact, possibly large, and phase 39's channel exists to serialise writes to
    /// shared mutable state — which this is not. What goes through it is the index record saying where
    /// the file is, reported as an outcome like any other.
    /// </para>
    /// <para>
    /// A check that fails does not stop the others. An operator running five checks wants five
    /// answers, and one unrunnable check is a fact about that check.
    /// </para>
    /// </summary>
    /// <summary>
    /// Tells a reader that cares that this position is durable, so its source can prune behind it.
    /// <para>
    /// A failure here is logged and does not fail the run. The rows are already at the target and the
    /// watermark is already stored; what is left undone is housekeeping at the source, and failing a
    /// successful pass over unpruned history would be reporting a disk-space problem as data loss.
    /// </para>
    /// </summary>
    private async Task AcknowledgeAsync(
        IChangeReader reader,
        DbConnection sourceConnection,
        SourceTableRef source,
        string watermark,
        IReadOnlyDictionary<string, string> options,
        Guid runId,
        CancellationToken cancellationToken)
    {
        if (reader is not IPositionAcknowledging acknowledging)
            return;

        try
        {
            await acknowledging.AcknowledgeAsync(sourceConnection, source, watermark, options, cancellationToken);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            Log(runId, LogSeverity.Warning,
                $"Could not tell the source that position '{watermark}' is durable: {ex.Message} " +
                "The pass itself succeeded; the source keeps history it could have discarded.");
        }
    }

    private async Task<(long RowsRead, long RowsWritten)> RunVerificationAsync(
        ReplicationTaskConfig task, TableMappingConfig mapping, WorkItem item, CancellationToken cancellationToken)
    {
        if (mapping.Verification.Count == 0)
        {
            Log(item.RunId, LogSeverity.Info, $"'{mapping.Name}' has no verification checks configured.");
            return (0, 0);
        }

        var source = EndpointResolution.ResolveSource(task, mapping.Sources[0]);
        var target = EndpointResolution.ResolveTarget(task, mapping.Targets[0]);

        DbConnection? sourceConnection = null;
        DbConnection? targetConnection = null;
        try
        {
            (sourceConnection, var sourceDriver) = await OpenConnectionAsync(source.ConnectionName, cancellationToken);
            (targetConnection, var targetDriver) = await OpenConnectionAsync(target.ConnectionName, cancellationToken);

            var sourceDialect = ResolveDialect(sourceDriver);
            var targetDialect = ResolveDialect(targetDriver);
            await sourceDialect.UseDatabaseAsync(sourceConnection, source.Database, cancellationToken);
            await targetDialect.UseDatabaseAsync(targetConnection, target.Database, cancellationToken);

            // Fetched once, and only when a script check needs it: a built-in derives everything it
            // asks from the mapping and should not pay a catalog round trip to learn what it knows.
            var needsCatalog = mapping.Verification.Any(c => c.Kind == VerificationCheckKind.Script);
            var sourceColumns = needsCatalog
                ? await sourceDriver.ListColumnsAsync(sourceConnection, source.Database, source.Schema, source.Table, cancellationToken)
                : [];
            var targetColumns = needsCatalog
                ? await targetDriver.ListColumnsAsync(targetConnection, target.Database, target.Schema, target.Table, cancellationToken)
                : [];

            var sourceSide = new VerificationEndpoint(
                sourceConnection, sourceDialect, source.Schema, source.Table,
                ScriptDialectFor(sourceDriver), sourceColumns, source);
            var targetSide = new VerificationEndpoint(
                targetConnection, targetDialect, target.Schema, target.Table,
                ScriptDialectFor(targetDriver), targetColumns, target);

            long compared = 0;
            long differing = 0;

            foreach (var check in mapping.Verification)
            {
                try
                {
                    var result = await VerificationExecutor.RunAsync(
                        check, mapping.ColumnMappings, sourceSide, targetSide, cancellationToken,
                        // Bound by name from the check, never through the hierarchy: which script
                        // answers a particular question is a property of that question.
                        check.Kind == VerificationCheckKind.Script && check.ScriptName is { } name
                            ? scriptHost.Resolve<IVerificationQueryBuilder>(name)
                            : null);

                    var path = VerificationPaths.For(stateDbPath, task.Name, item.RunId, check.Name);
                    await VerificationResultFile.WriteAsync(path, result, cancellationToken);

                    state.RecordVerificationResult(new VerificationResultRecord(
                        Id: 0, item.RunId, task.Name, mapping.Name, check.Name,
                        DateTimeOffset.UtcNow, result.SourceReadAtUtc, result.TargetReadAtUtc,
                        result.Rows.Count, result.DifferingGroups, path));

                    compared += result.Rows.Count;
                    differing += result.DifferingGroups;

                    Log(item.RunId, result.DifferingGroups == 0 ? LogSeverity.Info : LogSeverity.Warning,
                        $"'{check.Name}': {result.Rows.Count} group(s) compared, {result.DifferingGroups} differing " +
                        $"(the two sides were read {result.ReadGap.TotalSeconds:F1}s apart).");
                }
                catch (Exception ex) when (ex is DbException or ConfigValidationException)
                {
                    // Reported and moved past. Five checks should give five answers, and one that
                    // cannot run is a fact about that check rather than about the run.
                    Log(item.RunId, LogSeverity.Error, $"'{check.Name}' could not run: {ex.Message}");
                }
            }

            if (differing > 0)
                Log(item.RunId, LogSeverity.Warning, $"{differing} group(s) differ by more than their threshold.");

            return (compared, 0);
        }
        finally
        {
            sourceConnection?.Dispose();
            targetConnection?.Dispose();
        }
    }

    private async Task<(long RowsRead, long RowsWritten, RunTiming? Timing, WatermarkChange? Watermark)> RunMappingAsync(
        ReplicationTaskConfig task, TableMappingConfig mapping, WorkItem item, CancellationToken cancellationToken)
    {
        var source = EndpointResolution.ResolveSource(task, mapping.Sources[0]);
        var target = EndpointResolution.ResolveTarget(task, mapping.Targets[0]);

        // Two levels of override, resolved in order. First the mapping's own stage config, if it
        // has one — Kind *and* Options together, since reading one stage's Kind from the mapping
        // and its options from the replication is how an option set for a Kind nobody selected
        // ends up being passed to the one they did (phase 68).
        var effectiveReader = PipelineResolution.Reader(task, mapping);
        var effectiveCache = PipelineResolution.Cache(task, mapping);
        var effectiveWriter = PipelineResolution.Writer(task, mapping);

        // Then the unit of work's own, which is the most specific. It's how a Backfill of an
        // incrementally-synced replication reloads a segment at all: the configured reader reports
        // changes since a watermark, which is not what reloading a segment means.
        var readerKind = item.Kinds.ReaderKind ?? effectiveReader.Kind;
        var cacheKind = item.Kinds.CacheKind ?? effectiveCache.Kind;
        var writerKind = item.Kinds.WriterKind ?? effectiveWriter.Kind;

        // Resolved from config alone, before any connection is opened — the driver registry lookup and
        // WatermarkKey.Build need only the source's *configuration*, never a live connection. That is
        // what lets an unsupported read intent (below) fail before a network connection is even spent
        // on a pass that was never going to run.
        var sourceConnectionConfig = configRepository.LoadConnection(source.ConnectionName);
        var sourceDriverForResolution = driverRegistry.Get(sourceConnectionConfig.DriverType);

        // Through the registry rather than the driver, so a host-supplied reader (phase 30's
        // ScriptedQuery) is as visible to the pipeline as it is to the capability endpoint.
        var reader = driverRegistry.FindReader(sourceDriverForResolution.DriverType, readerKind)
            ?? throw new InvalidOperationException($"Source driver does not support reader kind '{readerKind}'.");

        // Only a Primary pass ever reads or advances the incremental cursor — a Backfill must never be
        // able to disturb the watermark or the intent a replication's ongoing incremental sync depends
        // on, regardless of which reader/writer Kind it happens to use internally. A Backfill's reader
        // is asked for an InitialLoad — a reload's own definition — since it never consults the cursor
        // either way; it is not a live ReadIntent so much as the closest description of what a reload
        // pass does.
        var watermarkKey = WatermarkKey.Build(source, ResolveDialect(sourceDriverForResolution));
        var readState = item.RunKind == RunKind.Primary
            ? state.GetReadState(task.Name, mapping.Name, watermarkKey)
            : null;
        var previousWatermark = readState?.Watermark;
        var intent = item.RunKind == RunKind.Primary
            ? readState?.Intent ?? ReadIntentResolution.Default(task, mapping)
            : ReadIntent.InitialLoad;

        // An intent the reader cannot honour is never quietly downgraded to InitialLoad. Save-time
        // validation cannot close this — the reader can change, or a replication-level default can be
        // set, after the mapping was last saved — so the run-time outcome is a loud failure here,
        // before this reader's ReadChangesAsync is ever called: nothing is read, let alone the whole
        // table. Same posture phase 91 took refusing to fall back to a live catalog query.
        //
        // InitialLoad itself is exempt, and always will be: the bulk-load retarget
        // (architecture/planning/done/bulk-load-pipeline-and-the-initial-load-rule.md) made it
        // universally available regardless of reader — no reader declares it any more (see
        // IReadIntentDeclaring), so without this exemption every mapping resolving to InitialLoad would
        // be refused here, which is exactly backwards. The rule below still applies in full to
        // Changes/ChangesFromEarliest/ChangesFromLatest, which remain genuinely per-reader.
        if (item.RunKind == RunKind.Primary
            && intent != ReadIntent.InitialLoad
            && reader is IReadIntentDeclaring declaring
            && !declaring.SupportedIntents.Contains(intent))
        {
            throw new InvalidOperationException(
                $"Table mapping '{mapping.Name}' has read intent '{intent}', but its reader " +
                $"('{reader.Kind}') does not support it. Supported: " +
                $"{string.Join(", ", declaring.SupportedIntents.OrderBy(i => i))}. Set a different read " +
                "intent on the mapping, or change its reader.");
        }

        DbConnection? sourceConnection = null;
        DbConnection? targetConnection = null;
        try
        {
            sourceConnection = await OpenAsync(sourceConnectionConfig, sourceDriverForResolution, cancellationToken);
            var sourceDriver = sourceDriverForResolution;
            (targetConnection, var targetDriver) = await OpenConnectionAsync(target.ConnectionName, cancellationToken);

            var stagingProvider = targetDriver.StagingProviders.FirstOrDefault(p => p.Kind == cacheKind)
                ?? throw new InvalidOperationException($"Target driver does not support staging kind '{cacheKind}'.");
            var writer = targetDriver.Writers.FirstOrDefault(w => w.Kind == writerKind)
                ?? throw new InvalidOperationException($"Target driver does not support writer kind '{writerKind}'.");

            if (item.Kinds != WorkItemKinds.FromConfig)
                Log(item.RunId, LogSeverity.Info,
                    $"Using reader '{readerKind}', cache '{cacheKind}', writer '{writerKind}' for this {item.RunKind} " +
                    $"(this mapping is configured for '{effectiveReader.Kind}'/'{effectiveCache.Kind}'/'{effectiveWriter.Kind}').");

            // Resolved once per pass, not per statement: the script generates an expression in exactly
            // the form a hand-written transform takes, and phase 22's projection does the rest —
            // including the {{column}} substitution that makes it correct in a reader whose statement
            // aliases the source table.
            var sourceScriptDialect = ScriptDialectFor(sourceDriver);
            var columnMappings = ApplyScriptedTransforms(
                task, mapping, sourceConnectionConfig, sourceScriptDialect, item.RunId);

            // The hierarchy's "connection" level is always the target's — see HookResolution's doc.
            var targetConnectionConfig = configRepository.LoadConnection(target.ConnectionName);
            var hooksByPoint = HookPoints.All.ToDictionary(
                point => point, point => HookResolution.Resolve(point, targetConnectionConfig, task, mapping));

            // The in-process half of the transform story. Built once per pass — each script is asked
            // what it wants before any row arrives, so the per-row path stays as narrow as it can be.
            var transforms = TransformPipeline.Build(
                scriptHost, sourceConnectionConfig, task, mapping, columnMappings, sourceScriptDialect,
                message => Log(item.RunId, LogSeverity.Info, $"[{mapping.Name}] {message}"));

            await EnsureTargetTableProvisionedAsync(
                task, sourceDriver, sourceConnection, source, targetDriver, targetConnection, target,
                mapping, item.RunId, cancellationToken);

            // Resolved once per pass, before the segment loop, for the same reason WithSegment builds a
            // copy rather than mutating: the configured options dictionary is shared config, and a run
            // must not write its own derivation into it.
            var passWriterOptions = await WithDerivedNaturalKeyAsync(
                writerKind, effectiveWriter.Options, sourceDriver, sourceConnection, source, mapping,
                item.RunId, cancellationToken);

            var segments = await ResolveSegmentsAsync(
                reader, sourceConnection, targetConnection, source, item, task, mapping, sourceScriptDialect,
                cancellationToken);
            if (segments.Count > 1)
                Log(item.RunId, LogSeverity.Info, $"Processing {segments.Count} configured segment(s) in this pass.");

            long totalRead = 0;
            long totalWritten = 0;
            string? newWatermark = null;
            DateTimeOffset? newWatermarkTime = null;
            WatermarkChange? watermarkChange = null;

            // Resolved once per pass. Off means the row stream is never wrapped and no stopwatch is
            // started — a mapping that did not ask pays nothing, which is the difference between an
            // opt-in trace and a metric that is always collected and usually discarded.
            var trace = mapping.TraceTiming;
            long? timeToFirstRowMs = null;
            long readerLifetimeMs = 0;
            long stagingMs = 0;
            long writerMs = 0;

            var targetDialect = ResolveDialect(targetDriver);
            var sourceDialect = ResolveDialect(sourceDriver);
            var targetQualified = targetDialect.QualifyTable(target.Schema, target.Table);
            var targetSchemaQuoted = targetDialect.QuoteIdentifier(target.Schema);
            var targetTableQuoted = targetDialect.QuoteIdentifier(target.Table);
            var sourceQualified = sourceDialect.QualifyTable(source.Schema, source.Table);

            // Phase 27's C#-generated hooks: one slot, bound at whatever level ScriptResolution finds
            // it (the same "connection" level as HookResolution's — the target's). Declared once per
            // pass, before the first read, so the host never even builds a LifecycleHookContext — let
            // alone calls the script — for a mapping with nothing bound.
            var targetScriptDialect = ScriptDialectFor(targetDriver);
            var lifecycleHookBinding = scriptHost.ResolveBinding<ILifecycleHook>(
                ScriptSlots.LifecycleHook, targetConnectionConfig, task, mapping);
            IReadOnlySet<string> declaredHookPoints = ImmutableHashSet<string>.Empty;
            if (lifecycleHookBinding is { } binding)
            {
                var declareFacts = new HookRunFacts(
                    item.RunId, task.Name, mapping.Name, item.RunKind.ToString(),
                    Segment: null, SegmentIndex: 0, SegmentCount: segments.Count, IsLastSegment: false,
                    RowsStaged: null, RowsWritten: null, StagingLocation: null, previousWatermark);
                var declareContext = await BuildLifecycleHookContextAsync(
                    "(declare)", sourceDriver, sourceConnection, source, targetDriver, targetConnection, target,
                    columnMappings, sourceScriptDialect, targetScriptDialect, declareFacts, binding.Parameters,
                    item.RunId, mapping.Name, cancellationToken);
                declaredHookPoints = binding.Script.DeclarePoints(declareContext).ToImmutableHashSet(StringComparer.Ordinal);
            }

            for (var segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
            {
                var segment = segments[segmentIndex];
                var readerOptions = WithSegment(effectiveReader.Options, segment);
                var cacheOptions = WithSegment(effectiveCache.Options, segment);
                var writerOptions = WithSegment(passWriterOptions, segment);
                var isLastSegment = segmentIndex == segments.Count - 1;

                HookRenderContext Context(string? stagingQualified, long? rowsStaged, long? rowsWritten) => new(
                    targetQualified, targetSchemaQuoted, targetTableQuoted, sourceQualified, stagingQualified,
                    task.Name, mapping.Name, item.RunId, item.RunKind.ToString(), segment?.Describe(),
                    segmentIndex, segments.Count, isLastSegment, rowsStaged, rowsWritten, previousWatermark);

                // The escape hatch runs last: only reached for a point the script itself declared, and
                // only after the configured list above has already run.
                async Task RunGeneratedAsync(string point, string? stagingLocation, long? rowsStaged, long? rowsWritten)
                {
                    if (lifecycleHookBinding is not { } b || !declaredHookPoints.Contains(point))
                        return;

                    var facts = new HookRunFacts(
                        item.RunId, task.Name, mapping.Name, item.RunKind.ToString(), segment?.Describe(),
                        segmentIndex, segments.Count, isLastSegment, rowsStaged, rowsWritten, stagingLocation, previousWatermark);
                    var context = await BuildLifecycleHookContextAsync(
                        point, sourceDriver, sourceConnection, source, targetDriver, targetConnection, target,
                        columnMappings, sourceScriptDialect, targetScriptDialect, facts, b.Parameters,
                        item.RunId, mapping.Name, cancellationToken);
                    await RunLifecycleHookStatementsAsync(
                        point, b.Script, context, targetConnection, targetDialect, mapping.Name, item.RunId, cancellationToken);
                }

                var scope = segment?.Describe() ?? "whole table";
                Log(item.RunId, LogSeverity.Info,
                    $"Reading changes for '{mapping.Name}' ({scope}, intent: {intent}, " +
                    $"watermark: {previousWatermark ?? "<none>"}).");

                // Started before the call, not after: how long the source takes to *begin* answering
                // is part of what the reader cost, and a stopwatch started once it returned would
                // silently exclude it.
                var passTiming = trace ? new ReaderTimingRecorder() : null;

                ReadResult read;
                try
                {
                    read = await reader.ReadChangesAsync(
                        sourceConnection, source, previousWatermark, intent, columnMappings, mapping.Name,
                        mapping.SourceColumns, readerOptions, cancellationToken);
                }
                catch (PositionExpiredException)
                {
                    // Caught and re-thrown rather than handled at ProcessWorkItemAsync's own catch site:
                    // this is where task.Name/mapping.Name/watermarkKey are in scope, and setting the
                    // hold is the one extra thing phase 101 adds to what was already a Failed run with
                    // RunFailureKinds.PositionExpired. The hold, not the run's own outcome, is what stops
                    // the next scheduling tick from dispatching this mapping again — see SchedulerService.
                    if (item.RunKind == RunKind.Primary)
                        state.SetReadHold(task.Name, mapping.Name, watermarkKey, ReadHold.PositionExpired);
                    throw;
                }
                var readRows = passTiming is null ? read.Rows : read.Rows.WithTiming(passTiming, cancellationToken);
                var rows = transforms.IsEmpty
                    ? readRows
                    : transforms.ApplyAsync(
                        readRows,
                        dropped => Log(item.RunId, LogSeverity.Info,
                            $"'{mapping.Name}': {dropped} row(s) dropped by a transform script."),
                        cancellationToken);

                await RunHooksAsync(
                    HookPoints.BeforeStage, hooksByPoint[HookPoints.BeforeStage], Context(null, null, null),
                    targetConnection, targetDialect, source.ConnectionName, sourceDialect, mapping.Name, item.RunId, cancellationToken);
                await RunGeneratedAsync(HookPoints.BeforeStage, null, null, null);

                // The whole call, not just the part that consumes rows. For today's providers those
                // are nearly the same span; for a file-based one that moves or uploads what it staged
                // after the stream is exhausted, the difference from the reader's lifetime is that
                // provider's own work — which is the number worth having.
                var stagingClock = trace ? Stopwatch.StartNew() : null;
                var staged = await stagingProvider.StageAsync(
                    targetConnection, target, rows, mapping.ColumnMappings, mapping.Name, mapping.TargetColumns,
                    cacheOptions, cancellationToken);
                if (stagingClock is not null)
                    stagingMs += stagingClock.ElapsedMilliseconds;

                await RunHooksAsync(
                    HookPoints.AfterStage, hooksByPoint[HookPoints.AfterStage], Context(staged.StagingLocation, staged.RowCount, null),
                    targetConnection, targetDialect, source.ConnectionName, sourceDialect, mapping.Name, item.RunId, cancellationToken);
                await RunGeneratedAsync(HookPoints.AfterStage, staged.StagingLocation, staged.RowCount, null);

                // Only meaningful now that staging has drained the reader's stream. A run that skipped
                // rows is a run whose source was changing under it — worth surfacing next to a mapping
                // that looks slow or keeps retrying, rather than leaving it invisible.
                if (read.Diagnostics is { RowsSkippedSourceRowGone: > 0 } diagnostics)
                    Log(item.RunId, LogSeverity.Warning,
                        $"{diagnostics.RowsSkippedSourceRowGone} row(s) skipped: the source row was deleted while " +
                        "this pass was reading it. Each one's deletion is applied on a later pass.");

                try
                {
                    await RunHooksAsync(
                        HookPoints.BeforeLoad, hooksByPoint[HookPoints.BeforeLoad], Context(staged.StagingLocation, staged.RowCount, null),
                        targetConnection, targetDialect, source.ConnectionName, sourceDialect, mapping.Name, item.RunId, cancellationToken);
                    await RunGeneratedAsync(HookPoints.BeforeLoad, staged.StagingLocation, staged.RowCount, null);

                    var writerClock = trace ? Stopwatch.StartNew() : null;
                    var written = await writer.ApplyAsync(
                        targetConnection, target, staged, mapping.ColumnMappings, mapping.Name, mapping.TargetColumns,
                        writerOptions, cancellationToken);
                    if (writerClock is not null)
                        writerMs += writerClock.ElapsedMilliseconds;

                    totalRead += staged.RowCount;
                    totalWritten += written.RowsWritten;
                    Log(item.RunId, LogSeverity.Info,
                        $"'{mapping.Name}' ({scope}): {staged.RowCount} row(s) read, {written.RowsWritten} row(s) written.");

                    // Inside the try, before CleanupAsync drops the staged set — otherwise {{staging}}
                    // refers to a table that no longer exists.
                    await RunHooksAsync(
                        HookPoints.AfterLoad, hooksByPoint[HookPoints.AfterLoad],
                        Context(staged.StagingLocation, staged.RowCount, written.RowsWritten),
                        targetConnection, targetDialect, source.ConnectionName, sourceDialect, mapping.Name, item.RunId, cancellationToken);
                    await RunGeneratedAsync(HookPoints.AfterLoad, staged.StagingLocation, staged.RowCount, written.RowsWritten);
                }
                finally
                {
                    // One pass can stage several times, so the staged set has to be discarded as it
                    // goes rather than left for connection teardown to deal with.
                    await stagingProvider.CleanupAsync(targetConnection, staged, CancellationToken.None);
                }

                if (passTiming is not null)
                {
                    // Lifetimes add up across segments; time-to-first-row does not. The first row of
                    // the pass is the one that says how long the source took to start answering, and
                    // a later segment's first row is measured from its own read, not from the pass's.
                    readerLifetimeMs += passTiming.LifetimeMs ?? 0;
                    timeToFirstRowMs ??= passTiming.TimeToFirstRowMs;
                }

                // After the stream is drained, and WatermarkAfterRead rather than NewWatermark: a
                // row-bounded read only knows where it got to once its rows have been through. See
                // ReadResult.
                newWatermark = read.WatermarkAfterRead;

                // Taken from the same ReadResult as the position above, and never from anywhere
                // else: the reader mapped this exact position to a time on the connection this pass
                // already had open, which is the whole reason a lag report can later cost the source
                // nothing. Null for every reader that has no such mapping. See phase 87.
                newWatermarkTime = read.WatermarkTimeAfterRead;
            }

            // Segmented passes only ever happen with a reload reader, which has no watermark of its
            // own and echoes back whatever it was given — so taking the last segment's value is the
            // same as taking any of them. An ordinary incremental pass has exactly one segment (none).
            if (item.RunKind == RunKind.Primary)
            {
                // Records the watermark and, in the same write, transitions the intent to Changes —
                // whatever it was before, this pass just applied its changes under it. See
                // PrimaryPassOutcome.
                watermarkChange = PrimaryPassOutcome.Apply(
                    state, task.Name, mapping.Name, watermarkKey, previousWatermark, newWatermark, newWatermarkTime);

                if (watermarkChange is not null)
                {
                    // After the write committed and after the watermark is durable, never before. A
                    // reader that acknowledges is telling its source it may discard the history behind
                    // this position — do that early and a failed run stops being retryable, which turns
                    // a bad pass into permanent data loss. See IPositionAcknowledging.
                    await AcknowledgeAsync(
                        reader, sourceConnection!, source, newWatermark!, effectiveReader.Options,
                        item.RunId, cancellationToken);
                }
            }

            // Summed across segments, because a pass over several segments is one run and one row in
            // TaskRuns — reporting only the last segment's numbers would understate every one of them.
            var timing = trace
                ? new RunTiming(
                    readerKind, timeToFirstRowMs, readerLifetimeMs, cacheKind, stagingMs, writerKind, writerMs)
                : null;

            return (totalRead, totalWritten, timing, watermarkChange);
        }
        finally
        {
            sourceConnection?.Dispose();
            targetConnection?.Dispose();
        }
    }

    /// <summary>
    /// What this unit of work should read, in order. Either exactly one segment carried on the work
    /// item itself (a Backfill — the API expanded and enqueued one item per segment), or the static
    /// segment list a standalone reload replication configures on its reader (iterated within this
    /// one pass), or a single null meaning "no segment, read the whole thing" — every ordinary
    /// incremental pass.
    /// </summary>
    /// <summary>
    /// The mapping's columns with any bound <c>sqlColumnExpression</c> script's output folded into
    /// <see cref="ColumnMapping.Transform"/>.
    /// <para>
    /// Only the reader is given these. Staging and the writer keep the *configured* mappings, because a
    /// transform changes a value on its way out of the source and has nothing to say about which target
    /// column it lands in.
    /// </para>
    /// </summary>
    private IReadOnlyList<ColumnMapping> ApplyScriptedTransforms(
        ReplicationTaskConfig task, TableMappingConfig mapping, ConnectionConfig connection,
        IScriptDialect dialect, Guid runId)
    {
        var binding = scriptHost.ResolveBinding<ISqlColumnExpression>(
            ScriptSlots.SqlColumnExpression, connection, task, mapping);

        if (binding is null)
            return mapping.ColumnMappings;

        var generated = new List<string>();
        var result = ScriptedColumnTransforms.Apply(
            mapping.ColumnMappings,
            binding.Value.Script,
            binding.Value.Parameters,
            dialect,
            columnMetadata: null,
            log: generated.Add);

        if (generated.Count > 0)
            Log(runId, LogSeverity.Info,
                $"Column-expression script generated {generated.Count} source transform(s): {string.Join("; ", generated)}");

        return result;
    }

    /// <summary>
    /// The one provisioning action DbDataSync ever runs unattended (phase 25 §5): additive-only, and only
    /// when the target table does not exist at all. Off by default (<see cref="ProvisioningConfig.CreateTargetTableIfMissing"/>);
    /// when the table already exists this is a no-op — no ALTER, no column reconciliation, and a
    /// mapped column missing from an existing table still fails with the ordinary staging error.
    /// <para>
    /// It is also the one thing in a run that writes config, through
    /// <see cref="CacheProvisionedTargetColumnsAsync"/>: a table this method creates has a shape
    /// nothing else in the system has ever seen, and phase 91's writers read that shape from the
    /// mapping's cache and nowhere else. Provisioning the table without recording what it provisioned
    /// left every such mapping failing its own first pass — see phase 94.
    /// </para>
    /// <para>
    /// Which is why an empty cache is a third reason to run this method at all, alongside the two
    /// permissions (phase 97). A mapping with both settings off and a target already in shape has a
    /// picture to take and no DDL to run, and it is the commonest such mapping there is — anything
    /// provisioned by hand. That inspection is a <c>PlanAsync</c> and a catalog read, both
    /// side-effect-free and both things the Setup card's preview already does; nothing here issues
    /// <c>CREATE</c> or <c>ALTER</c> without the setting that authorises it.
    /// </para>
    /// </summary>
    private async Task EnsureTargetTableProvisionedAsync(
        ReplicationTaskConfig task,
        IDriver sourceDriver, DbConnection sourceConnection, SourceTableRef source,
        IDriver targetDriver, DbConnection targetConnection, TableRef target,
        TableMappingConfig mapping, Guid runId, CancellationToken cancellationToken)
    {
        var mayCreate = ProvisioningResolution.CreateTargetTableIfMissing(task, mapping);
        var mayAlter = ProvisioningResolution.AlterTargetTableColumns(task, mapping);

        // The third reason to be here, and it is not a provisioning permission (phase 97). A mapping
        // with an empty cache has something to learn from the target whether or not it may change it —
        // and a mapping provisioned by hand has both settings off, which is *why* it was provisioned by
        // hand, so phase 94's recovery below could never once fire for the case that needed it most.
        // This inspects; it never provisions. See `mayRunDdl`, which is what keeps that true.
        //
        // Tested first so the steady state pays nothing: a mapping whose cache is populated returns
        // here exactly as it always did, without a plan, a catalog read or a connection round trip.
        //
        // Not for a source that names no table. Planning needs the source's catalog, and a query
        // source has none to read — the same condition MappingColumnReader states in words. Such a
        // mapping cannot run on an empty cache either way (phase 91 says so, in its own words); what
        // this avoids is answering it with a catalog error instead.
        var uncached = mapping.TargetColumns.Count == 0 && !string.IsNullOrWhiteSpace(source.Table);
        if ((!mayCreate && !mayAlter && !uncached) || targetDriver is not IProvisioner provisioner)
            return;

        // Said out loud, because it is the one case where a pass looks at catalogs nothing in its
        // configuration asked it to look at. It happens once — the next pass finds the cache filled and
        // returns at the gate above — and an operator reading "why did my provisioning-off run touch
        // the target's catalog" deserves the answer in the log rather than in this file.
        if (!mayCreate && !mayAlter)
            Log(runId, LogSeverity.Info,
                $"'{mapping.Name}': no target column metadata is cached, so this pass is inspecting the " +
                "target to recover it. Provisioning stays off — nothing will be created or altered.");

        var sourceColumns = await sourceDriver.ListColumnsAsync(
            sourceConnection, source.Database, source.Schema, source.Table, cancellationToken);
        var (columns, identityWarnings) = ProvisioningColumnBuilder.Build(
            ResolveDialect(sourceDriver), sourceColumns, mapping.ColumnMappings);

        // The same extension the Setup card applies, so what an unattended pass creates and what an
        // operator previewed are one answer rather than two. The mapping's *effective* writer: a
        // mapping that overrides its way onto Scd2 needs the version columns provisioned, and one that
        // overrides its way off them must not get them.
        var writerKind = PipelineResolution.Writer(task, mapping).Kind;
        var provisioned = HistorizedProvisioning.Extend(columns, writerKind);

        ProvisioningRequest Request(string action) => new(
            action, target, ReaderKind: null, ReaderOptions: new Dictionary<string, string>(), provisioned,
            writerKind);

        // Create if the table is missing, alter if it is there and out of shape — the two are mutually
        // exclusive, and each is gated by its own resolved setting. A replication that creates missing
        // tables but does not want columns changed underneath it gets exactly that. Planned when there
        // is DDL to consider *or* a shape to learn: a create plan's Satisfied is precisely "the table
        // is there", which is the question an empty cache needs answered.
        var create = mayCreate || uncached
            ? await provisioner.PlanAsync(targetConnection, Request(ProvisioningActions.CreateTargetTable), cancellationToken)
            : null;

        var plan = create is { State: ProvisioningState.Missing or ProvisioningState.Unsupported }
            ? create
            // A satisfied create plan and no alter permission still leaves something to say — see the
            // cache decision below — so it is the create plan that stands rather than nothing.
            : mayAlter
                ? await provisioner.PlanAsync(targetConnection, Request(ProvisioningActions.AlterTargetTable), cancellationToken)
                : create;

        if (plan is null)
            return;

        var what = plan.Action == ProvisioningActions.CreateTargetTable ? "created" : "altered";

        // Which permission this particular plan's steps would need. Since `uncached` can now bring a
        // plan back that no setting authorised, the authorisation is asked of the plan in hand rather
        // than inferred from having got this far — a mapping with both settings off reaches a Missing
        // create plan and runs none of it.
        var mayRunDdl = plan.Action == ProvisioningActions.CreateTargetTable ? mayCreate : mayAlter;

        if (plan.State == ProvisioningState.Unsupported)
        {
            // Only where DDL was actually wanted. "Cannot be auto-created" is a non-sequitur to a
            // mapping that never asked for anything to be created.
            if (!mayRunDdl)
                return;

            foreach (var warning in plan.Warnings)
                Log(runId, LogSeverity.Warning, $"'{mapping.Name}': target table cannot be auto-{what} — {warning}");
            return;
        }

        // Whether this pass has DDL to run and whether it has something to record about the target's
        // shape are two questions (phase 94). DDL having run is one reason to record; the other is a
        // target already in shape whose shape has never been cached — which is the state that made
        // this gap survive into a *second* pass, and the reason a report that fails to land costs one
        // more pass rather than an operator's manual Refresh. Nothing to record when the plan could
        // not be worked out at all: there is no table this pass can vouch for.
        var ddlRan = plan.State == ProvisioningState.Missing && mayRunDdl;
        var uncachedButInShape = plan.State == ProvisioningState.Satisfied && mapping.TargetColumns.Count == 0;
        if (!ddlRan && !uncachedButInShape)
            return;

        if (ddlRan)
        {
            foreach (var warning in identityWarnings.Concat(plan.Warnings))
                Log(runId, LogSeverity.Warning, $"'{mapping.Name}': {warning}");

            foreach (var step in plan.Steps)
            {
                using var cmd = targetConnection.CreateTimedCommand();
                cmd.CommandText = step.CommandText;
                await cmd.ExecuteNonQueryAsync(cancellationToken);
                Log(runId, LogSeverity.Info, $"'{mapping.Name}': target table {what} — {step.CommandText}");
            }
        }

        await CacheProvisionedTargetColumnsAsync(
            task, targetDriver, targetConnection, target, mapping, runId, cancellationToken);
    }

    /// <summary>
    /// Puts the target's shape into the mapping's phase-90 cache, now that provisioning can vouch for
    /// it — in memory for this pass, and reported to the config owner for every pass after it.
    /// <para>
    /// **Read back from the catalog, not taken from the plan.** The plan's <c>provisioned</c> list is
    /// what was asked for; the cache's contract (see <see cref="CachedColumn"/>) is what somebody saw
    /// when they last looked, and only the catalog can answer the three things that differ. A
    /// <see cref="ProvisioningColumn"/> carries a <see cref="CanonicalType"/> rather than the native
    /// type the target rendered it to, which is the string staging builds its own DDL from; it carries
    /// no identity flag, which is what a writer decides <c>IDENTITY_INSERT</c> on; and on the alter
    /// path it names only the mapped columns, so caching it would report a narrower table than the one
    /// that is there. Reading the catalog is also, exactly, what Refresh metadata does — so the cache
    /// has one answer in it rather than two that can disagree.
    /// </para>
    /// <para>
    /// This is a live introspection inside a run, which phase 91 otherwise has none of. It is the same
    /// exemption provisioning already takes: this method's caller reads the *source* catalog on every
    /// pass it runs, because provisioning cannot be planned without looking. The exemption is
    /// provisioning's, not the pipeline's.
    /// </para>
    /// </summary>
    private async Task CacheProvisionedTargetColumnsAsync(
        ReplicationTaskConfig task, IDriver targetDriver, DbConnection targetConnection, TableRef target,
        TableMappingConfig mapping, Guid runId, CancellationToken cancellationToken)
    {
        IReadOnlyList<ColumnMetadata> columns;
        try
        {
            columns = await targetDriver.ListColumnsAsync(
                targetConnection, target.Database, target.Schema, target.Table, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            // Warned, not thrown. Whatever this leaves the pass unable to do it will say for itself a
            // moment later, in MetadataNotCachedException's own words — which name the mapping and the
            // action to take. Failing here would replace that with this.
            Log(runId, LogSeverity.Warning,
                $"'{mapping.Name}': the target's shape could not be read back after provisioning it — " +
                $"{ex.Message}");
            return;
        }

        if (columns.Count == 0)
            return;

        var cached = columns
            .Select(c => new CachedColumn(c.Name, c.NativeType, c.IsNullable, c.IsPrimaryKey, c.IsIdentity))
            .ToList();

        // This pass first, and without waiting on anything: every consumer after this point reads the
        // object this method was handed, and a table this run created itself is not a table it should
        // need a round trip's permission to write to.
        mapping.TargetColumns = cached;

        // Then the durable half, which is what fixes every pass after this one — those load the mapping
        // fresh and would otherwise find the same empty cache. Best-effort by design: see
        // IRunnerConfig.ReportProvisionedTargetColumns.
        runnerConfig.ReportProvisionedTargetColumns(task.Name, mapping.Name, cached);

        Log(runId, LogSeverity.Info,
            $"'{mapping.Name}': cached the provisioned target's {cached.Count} column(s) — " +
            $"{string.Join(", ", cached.Select(c => c.Name))}.");
    }

    /// <summary>
    /// Runs one point's hook list, in declared order. Target hooks run on the pipeline's own
    /// <paramref name="targetConnection"/>; a source hook opens its own connection, lazily and only
    /// once even if several hooks in the list want it, and disposes it before returning — the
    /// <c>MsSqlChangeTrackingReader</c>'s doc comment is the reason (MARS does not help a second
    /// command issued on the connection a streaming reader is mid-read on).
    /// </summary>
    private async Task RunHooksAsync(
        string point,
        IReadOnlyList<HookConfig>? hooks,
        HookRenderContext context,
        DbConnection targetConnection,
        SqlDialect targetDialect,
        string sourceConnectionName,
        SqlDialect sourceDialect,
        string mappingName,
        Guid runId,
        CancellationToken cancellationToken)
    {
        if (hooks is null || hooks.Count == 0)
            return;

        DbConnection? sourceHookConnection = null;
        try
        {
            foreach (var hook in hooks)
            {
                var (sql, declaredParameterValues) = ResolveHookBody(hook);
                DbConnection connection;
                SqlDialect dialect;
                if (hook.Connection == HookConnectionSide.Source)
                {
                    sourceHookConnection ??= (await OpenConnectionAsync(sourceConnectionName, cancellationToken)).Connection;
                    connection = sourceHookConnection;
                    dialect = sourceDialect;
                }
                else
                {
                    connection = targetConnection;
                    dialect = targetDialect;
                }

                var statement = HookRenderer.Render(dialect, sql, declaredParameterValues, context);
                var label = hook.Name ?? hook.Hook ?? "inline";
                await ExecuteHookStatementAsync(
                    connection, dialect, statement, label, point, hook.Connection.ToString(), hook.OnError,
                    mappingName, runId, cancellationToken);
            }
        }
        finally
        {
            if (sourceHookConnection is not null)
                await sourceHookConnection.DisposeAsync();
        }
    }

    /// <summary>The SQL and the declared-parameter values behind one hook entry: <see cref="HookConfig.Sql"/>
    /// verbatim for an inline entry, or a named hook's own code plus the values its binding supplied.</summary>
    private (string Sql, IReadOnlyDictionary<string, string> DeclaredParameterValues) ResolveHookBody(HookConfig hook) =>
        hook.Sql is { } inline
            ? (inline, new Dictionary<string, string>())
            : (configRepository.LoadScript(hook.Hook!).Code, hook.Parameters);

    /// <summary>
    /// Runs one already-rendered statement and logs it, at Info, every time — a generated hook logs the
    /// statement text as well as the count/elapsed every config-driven hook logs, because a script
    /// emitting DDL nobody can read after the fact is precisely the failure this exists to prevent.
    /// <paramref name="onError"/> is always <see cref="HookErrorMode.Fail"/> for a phase 27 generated
    /// statement — there is no config knob for a script's own statements to opt into Warn.
    /// </summary>
    private async Task ExecuteHookStatementAsync(
        DbConnection connection, SqlDialect dialect, HookStatement statement, string label, string point,
        string side, HookErrorMode onError, string mappingName, Guid runId, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var cmd = connection.CreateTimedCommand();
            cmd.CommandText = statement.CommandText;
            foreach (var parameter in statement.Parameters)
                cmd.AddParameter(dialect.ParameterName(parameter.Name), parameter.Value);

            var rowsAffected = await cmd.ExecuteNonQueryAsync(cancellationToken);
            Log(runId, LogSeverity.Info,
                $"'{mappingName}' hook '{label}' ({point}, {side}): {rowsAffected} row(s) affected, " +
                $"{Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}ms. {statement.CommandText}");
        }
        catch (DbException ex)
        {
            var message = $"'{mappingName}' hook '{label}' ({point}) failed: {ex.Message}";
            if (onError == HookErrorMode.Warn)
                Log(runId, LogSeverity.Warning, message);
            else
                throw new InvalidOperationException(message, ex);
        }
    }

    /// <summary>
    /// Fetches both sides' columns fresh on every call, deliberately not cached across a pass: a
    /// schema-evolution hook's whole point is to react to the source having gained a column, and
    /// caching this lookup would be exactly the thing that silently breaks it later.
    /// </summary>
    private async Task<LifecycleHookContext> BuildLifecycleHookContextAsync(
        string point,
        IDriver sourceDriver, DbConnection sourceConnection, SourceTableRef source,
        IDriver targetDriver, DbConnection targetConnection, TableRef target,
        IReadOnlyList<ColumnMapping> columnMappings,
        IScriptDialect sourceScriptDialect, IScriptDialect targetScriptDialect,
        HookRunFacts facts, ScriptParameters parameters,
        Guid runId, string mappingName, CancellationToken cancellationToken)
    {
        var sourceColumns = await sourceDriver.ListColumnsAsync(
            sourceConnection, source.Database, source.Schema, source.Table, cancellationToken);
        var targetColumns = await targetDriver.ListColumnsAsync(
            targetConnection, target.Database, target.Schema, target.Table, cancellationToken);

        return new LifecycleHookContext(
            point, source, target, columnMappings, sourceColumns, targetColumns,
            sourceScriptDialect, targetScriptDialect, facts, parameters,
            message => Log(runId, LogSeverity.Info, $"[{mappingName}] {message}"));
    }

    /// <summary>
    /// The escape hatch runs last: at a point with both a configured list and a bound
    /// <see cref="ILifecycleHook"/>, the configured statements already ran (visible in the config diff
    /// and git log) before this is called. Only invoked for a point the script actually
    /// <see cref="ILifecycleHook.DeclarePoints"/>d, so a mapping with no bound script — the overwhelming
    /// majority — never builds a <see cref="LifecycleHookContext"/> at all.
    /// </summary>
    private async Task RunLifecycleHookStatementsAsync(
        string point, ILifecycleHook hook, LifecycleHookContext context,
        DbConnection targetConnection, SqlDialect targetDialect, string mappingName, Guid runId, CancellationToken cancellationToken)
    {
        IReadOnlyList<HookStatement> statements;
        try
        {
            statements = hook.BuildStatements(point, context);
        }
        catch (Exception ex) when (ex is not ScriptExecutionException)
        {
            throw new ScriptExecutionException($"The lifecycle-hook script threw building statements for '{point}': {ex.Message}", ex);
        }

        foreach (var statement in statements)
            await ExecuteHookStatementAsync(
                targetConnection, targetDialect, statement, "generated", point, "Target", HookErrorMode.Fail,
                mappingName, runId, cancellationToken);
    }

    /// <summary>
    /// The dialect a driver speaks. Was a per-engine switch here (and a second one in the script
    /// adapter below it) until phase 29 made a driver name its own through <see cref="IDialectProvider"/>
    /// — two switches in two processes that every new driver had to remember to extend.
    /// </summary>
    private static SqlDialect ResolveDialect(IDriver driver) =>
        (driver as IDialectProvider)?.Dialect
        ?? throw new InvalidOperationException(
            $"The '{driver.DriverType}' driver does not name a SQL dialect.");

    /// <summary>
    /// What this pass processes, in order: the one segment a Backfill work item carries, or the
    /// mapping's own configured default segmenting, or a single null meaning "the whole thing".
    /// <para>
    /// **Read from the mapping, not from <c>readerOptions["segments"]</c>.** That option is no longer
    /// consulted at all — segmenting is a property of the table mapping, and phase 58 moved it onto
    /// one with a real editor rather than leaving it as hand-typed JSON in a stringly-typed bag. A
    /// config still carrying the old option behaves as if it had none; this is a documented breaking
    /// change, deliberately without a migration.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<BatchReloadSegment?>> ResolveSegmentsAsync(
        IChangeReader reader,
        DbConnection sourceConnection,
        DbConnection targetConnection,
        SourceTableRef source,
        WorkItem item,
        ReplicationTaskConfig task,
        TableMappingConfig mapping,
        IScriptDialect sourceDialect,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(item.SegmentJson))
            return [SegmentSerializer.Deserialize(item.SegmentJson)];

        if (mapping.DefaultSegmenting.Count == 0)
            return [null];

        IReadOnlyList<BatchReloadSegment> segments = mapping.DefaultSegmenting;

        // Custom markers first, and every pass, because that is the whole point of one: a strategy
        // flagging "the last three months" has to be re-evaluated against today, not against the day
        // somebody configured it. Only its selected candidates run — an unattended pass takes what the
        // strategy decided, not everything it could imagine.
        segments = await CustomSegments.ExpandAsync(
            task, mapping, segments,
            new SegmentingConnections(sourceConnection, targetConnection, SourceColumnsOrNull: null, sourceDialect),
            cancellationToken);

        // Auto segments are resolved against the source's actual value range, so they're expanded
        // here rather than at config-save time — the range moves as the table does.
        if (reader is ISegmentExpandingReader expanding)
            segments = await expanding.ExpandAutoSegmentsAsync(sourceConnection, source, segments, cancellationToken);

        return [.. segments];
    }

    /// <summary>Built from the script host this executor already has, rather than injected: a worker
    /// that never meets a custom segment never runs anything through it.</summary>
    private CustomSegmentExpansion CustomSegments => field ??= new(new SegmentingStrategyRunner(scriptHost));

    /// <summary>Injects the work item's segment into a per-iteration copy of one role's options under
    /// the well-known key. All three roles get it: the reader needs it to scope what it reads, and a
    /// reconciling writer needs the same scope to know which target rows the reload is accountable
    /// for. The configured options dictionary is never mutated — it's shared across every item this
    /// worker processes.</summary>
    /// <summary>
    /// The writer's options with a <c>naturalKey</c> derived from the source's primary key, when the
    /// SCD Type 2 writer is running and nobody stated one — phase 68.
    /// <para>
    /// A per-call copy, never a write into the configured dictionary, for the reason
    /// <see cref="WithSegment"/> copies: that dictionary is loaded config shared by everything reading
    /// this mapping, and a derivation belongs to this pass.
    /// </para>
    /// <para>
    /// Deriving nothing injects nothing. <c>Scd2Writer.ApplyAsync</c> then fails on its own required-option
    /// check with a message naming what is missing, which is a better failure than one invented here —
    /// and is exactly what happened before this existed.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> WithDerivedNaturalKeyAsync(
        string writerKind, IReadOnlyDictionary<string, string> options,
        IDriver sourceDriver, DbConnection sourceConnection, SourceTableRef source,
        TableMappingConfig mapping, Guid runId, CancellationToken cancellationToken)
    {
        if (writerKind != GenericDriverKinds.Scd2)
            return options;

        // A stated key wins outright — at either level, since the mapping's override has already
        // replaced the replication's stage by the time this sees it.
        if (options.TryGetValue(Scd2Writer.NaturalKeyOption, out var stated) && !string.IsNullOrWhiteSpace(stated))
            return options;

        var sourceColumns = await sourceDriver.ListColumnsAsync(
            sourceConnection, source.Database, source.Schema, source.Table, cancellationToken);
        var derived = NaturalKeyDerivation.Derive(sourceColumns, mapping.ColumnMappings);
        if (derived.Count == 0)
            return options;

        Log(runId, LogSeverity.Info,
            $"'{mapping.Name}': natural key derived from the source's primary key — {NaturalKeyDerivation.Format(derived)}.");

        return new Dictionary<string, string>(options, StringComparer.Ordinal)
        {
            [Scd2Writer.NaturalKeyOption] = NaturalKeyDerivation.Format(derived),
        };
    }

    private static IReadOnlyDictionary<string, string> WithSegment(
        IReadOnlyDictionary<string, string> options, BatchReloadSegment? segment) =>
        segment is null
            ? options
            : new Dictionary<string, string>(options, StringComparer.Ordinal)
            {
                [SegmentSerializer.SegmentOptionKey] = SegmentSerializer.Serialize(segment),
            };

    private async Task<(DbConnection Connection, IDriver Driver)> OpenConnectionAsync(
        string connectionName, CancellationToken cancellationToken)
    {
        var config = configRepository.LoadConnection(connectionName);
        var driver = driverRegistry.Get(config.DriverType);
        var connection = await OpenAsync(config, driver, cancellationToken);
        return (connection, driver);
    }

    /// <summary>
    /// The second half of <see cref="OpenConnectionAsync"/>, split out for the one caller that has
    /// already had to resolve <paramref name="driver"/> from <paramref name="config"/> itself — to
    /// decide a reader and a read intent before spending a connection on a pass that turns out not to
    /// be allowed to run (phase 101).
    /// </summary>
    private async Task<DbConnection> OpenAsync(
        ConnectionConfig config, IDriver driver, CancellationToken cancellationToken)
    {
        var credential = config.AuthMode == AuthMode.SqlAuth
            ? secretStore.Resolve(config.CredentialSecretRef!)
            : null;

        var connection = driver.CreateConnection(config, credential);
        try
        {
            await connection.OpenAsync(cancellationToken);
        }
        catch (DbException ex)
        {
            connection.Dispose();
            throw new ConnectivityException(config.Name, ex);
        }

        return connection;
    }

    private void Log(Guid runId, LogSeverity level, string message) => state.Log(runId, level, message);

    /// <summary>
    /// What a script generating SQL for this engine is told about it. A driver that names no dialect —
    /// an ODBC or JDBC driver reaching an arbitrary engine — cannot support the slots that generate
    /// SQL, and says so here rather than at the point a script tries to quote something.
    /// </summary>
    private static IScriptDialect ScriptDialectFor(IDriver driver) =>
        ScriptDialectAdapter.For(driver)
        ?? throw new InvalidOperationException(
            $"The '{driver.DriverType}' driver does not name a SQL dialect, so scripts cannot generate SQL for it.");

    private sealed class ConnectivityException(string connectionName, Exception inner)
        : Exception($"Failed to open connection '{connectionName}': {inner.Message}", inner);
}

