using System.Runtime.ExceptionServices;
using System.Collections.Immutable;
using System.Data.Common;
using System.Diagnostics;
using System.Threading.Channels;
using ClrKernel.Core.Secrets;
using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;
using DataSync.Drivers.Generic;
using DataSync.Drivers.MsSql;
using DataSync.Drivers.Postgres;
using DataSync.Scripting;
using DataSync.Scripting.Abstractions;
using DataSync.State;
using DataSync.State.Remote;
using DataSync.Verification;
using DataSync.Core.Sql;

namespace DataSync.TaskRunner;

/// <summary>
/// The read -> stage -> apply -> watermark-update pipeline from architecture/detailed-design.md §3.3,
/// driven by a durable work queue rather than a fixed, up-front list of mappings — see
/// architecture/implementation/done/phase-008-work-queue-schema.md. One process (spawned by
/// DataSync.Api.Services.ProcessSupervisor) claims and drains a replication's pending WorkQueue items
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
    /// Claims and processes this replication's pending WorkQueue items until the queue is drained
    /// (no Pending/Claimed/Running items remain for it), using <paramref name="degreeOfParallelism"/>
    /// concurrent consumers. Per-item outcomes live in TaskRuns/WorkQueue, not this method's return
    /// value — the process exit code only distinguishes "the worker ran and drained cleanly" from
    /// "the worker itself failed to start" (config errors before any item could even be claimed).
    /// </summary>
    public async Task<ExitCode> ExecuteWorkerAsync(string taskName, int degreeOfParallelism, CancellationToken cancellationToken)
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
        var channel = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(Math.Max(1, degreeOfParallelism) * 2)
        {
            SingleWriter = true,
            SingleReader = degreeOfParallelism == 1,
        });

        var producer = ProduceAsync(taskName, task.Scheduling, workerId, channel.Writer, cancellationToken);
        var consumers = Enumerable.Range(0, Math.Max(1, degreeOfParallelism))
            .Select(_ => ConsumeAsync(channel.Reader, cancellationToken))
            .ToArray();

        // The producer can fail while consumers are mid-item, and the owner going away is exactly
        // that case. Its finally completes the writer, so the consumers still drain what they already
        // hold — but they have to be *awaited*, or this process returns from Main and takes them with
        // it, along with the outcomes they were in the middle of recording. That is the whole
        // difference between spilling to a journal and losing the work.
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
        state.Flush();
        producerFailure?.Throw();

        return ExitCode.Success;
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

    /// <summary>Claims the next available item for this task in a loop, feeding it to the bounded
    /// channel (which applies backpressure once consumers fall behind), until the queue is observed
    /// empty for this task across <see cref="EmptyPollsBeforeExit"/> consecutive polls — at which
    /// point the worker has nothing left to do and exits. New work enqueued after this point is
    /// picked up by the next spawned worker (see ProcessSupervisor).</summary>
    private async Task ProduceAsync(
        string taskName, SchedulingConfig scheduling, string workerId, ChannelWriter<WorkItem> writer,
        CancellationToken cancellationToken)
    {
        var continuous = scheduling.Mode == ScheduleMode.Continuous;
        var consecutiveEmptyPolls = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var item = state.TryClaimNext(taskName, workerId);
                if (item is not null)
                {
                    consecutiveEmptyPolls = 0;
                    await writer.WriteAsync(item, cancellationToken);
                    continue;
                }

                if (state.HasOutstandingWork(taskName))
                {
                    // Consumers are still busy. Poll quickly — this is not idleness, it is waiting for
                    // a colleague.
                    consecutiveEmptyPolls = 0;
                    await Task.Delay(PollInterval, cancellationToken);
                    continue;
                }

                if (!continuous)
                {
                    if (++consecutiveEmptyPolls >= EmptyPollsBeforeExit)
                        break;
                    await Task.Delay(PollInterval, cancellationToken);
                    continue;
                }

                // Continuous: an empty queue is the normal state between passes, not a reason to go.
                // The worker leaves only once it has gone a whole idle timeout without a pass reading
                // anything — under a live load that never arrives, so the process stays up instead of
                // being respawned several times a minute.
                if (IdleFor() >= scheduling.IdleTimeout)
                    break;

                await WaitForWorkAsync(taskName, scheduling.Frequency, cancellationToken);
            }
        }
        finally
        {
            writer.Complete();
        }
    }

    /// <summary>
    /// Waits out one interval between passes, and stops early the moment there is something to claim.
    /// <para>
    /// The interval is the configured frequency because that is when the next pass is due; sleeping
    /// straight through it would be right if nothing else could enqueue work, and something can — a
    /// person pressing Run Now, or a backfill. A manual trigger no-ops
    /// <c>ProcessSupervisor.EnsureWorkerRunning</c> while this process is alive, so if this slept the
    /// full minute, so would they.
    /// </para>
    /// </summary>
    private async Task WaitForWorkAsync(string taskName, TimeSpan interval, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + interval;
        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            await Task.Delay(remaining < PollInterval ? remaining : PollInterval, cancellationToken);
            // Nothing is in flight at this point — the queue was empty when we got here — so anything
            // outstanding is something to claim.
            if (state.HasOutstandingWork(taskName))
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
                    $"{mapping.Targets.Count} target(s) — DataSync.TaskRunner only executes 1:1 " +
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
            if (rowsRead > 0)
                MarkProductive();
        }
        catch (Exception ex) when (ex is FileNotFoundException or ConfigValidationException)
        {
            Log(item.RunId, LogSeverity.Error, $"Config error: {ex.Message}");
            state.Flush();
            state.CompleteRun(item.RunId, RunStatus.Failed, 0, 0, ex.Message);
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
                item.RunId, RunStatus.Failed, 0, 0, ex.Message, RunFailureKinds.PositionExpired);
            state.MarkFailed(item.Id);
        }
        catch (ConnectivityException ex)
        {
            Log(item.RunId, LogSeverity.Error, $"Run failed: {ex.Message}");
            state.Flush();
            state.CompleteRun(item.RunId, RunStatus.Failed, 0, 0, ex.Message);
            state.MarkFailed(item.Id);
        }
        // Deliberately not caught: the owner being gone is not this item failing. Recording it as
        // Failed would be this process asserting an outcome it is in no position to observe — and it
        // is the one exception that must reach ConsumeAsync, which stops rather than starting more.
        catch (Exception ex) when (ex is not StateOwnerUnavailableException)
        {
            Log(item.RunId, LogSeverity.Error, $"Run failed: {ex.Message}");
            state.Flush();
            state.CompleteRun(item.RunId, RunStatus.Failed, 0, 0, ex.Message);
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

        DbConnection? sourceConnection = null;
        DbConnection? targetConnection = null;
        try
        {
            (sourceConnection, var sourceDriver) = await OpenConnectionAsync(source.ConnectionName, cancellationToken);
            (targetConnection, var targetDriver) = await OpenConnectionAsync(target.ConnectionName, cancellationToken);

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

            // Through the registry rather than the driver, so a host-supplied reader (phase 30's
            // ScriptedQuery) is as visible to the pipeline as it is to the capability endpoint.
            var reader = driverRegistry.FindReader(sourceDriver.DriverType, readerKind)
                ?? throw new InvalidOperationException($"Source driver does not support reader kind '{readerKind}'.");
            var stagingProvider = targetDriver.StagingProviders.FirstOrDefault(p => p.Kind == cacheKind)
                ?? throw new InvalidOperationException($"Target driver does not support staging kind '{cacheKind}'.");
            var writer = targetDriver.Writers.FirstOrDefault(w => w.Kind == writerKind)
                ?? throw new InvalidOperationException($"Target driver does not support writer kind '{writerKind}'.");

            if (item.Kinds != WorkItemKinds.FromConfig)
                Log(item.RunId, LogSeverity.Info,
                    $"Using reader '{readerKind}', cache '{cacheKind}', writer '{writerKind}' for this {item.RunKind} " +
                    $"(this mapping is configured for '{effectiveReader.Kind}'/'{effectiveCache.Kind}'/'{effectiveWriter.Kind}').");

            // Only a Primary pass ever advances the incremental watermark — a Backfill must never be
            // able to disturb the cursor a replication's ongoing incremental sync depends on,
            // regardless of which reader/writer Kind it happens to use internally.
            var watermarkKey = WatermarkKey.Build(source, ResolveDialect(sourceDriver));
            var previousWatermark = item.RunKind == RunKind.Primary
                ? state.GetWatermark(task.Name, mapping.Name, watermarkKey)
                : null;

            // Resolved once per pass, not per statement: the script generates an expression in exactly
            // the form a hand-written transform takes, and phase 22's projection does the rest —
            // including the {{column}} substitution that makes it correct in a reader whose statement
            // aliases the source table.
            var sourceScriptDialect = ScriptDialectFor(sourceDriver);
            var sourceConnectionConfig = configRepository.LoadConnection(source.ConnectionName);
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
                    $"Reading changes for '{mapping.Name}' ({scope}, watermark: {previousWatermark ?? "<none>"}).");

                // Started before the call, not after: how long the source takes to *begin* answering
                // is part of what the reader cost, and a stopwatch started once it returned would
                // silently exclude it.
                var passTiming = trace ? new ReaderTimingRecorder() : null;

                var read = await reader.ReadChangesAsync(
                    sourceConnection, source, previousWatermark, columnMappings, readerOptions, cancellationToken);
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
                    targetConnection, target, rows, mapping.ColumnMappings, cacheOptions, cancellationToken);
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
                        targetConnection, target, staged, mapping.ColumnMappings, writerOptions, cancellationToken);
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
            if (item.RunKind == RunKind.Primary && newWatermark is not null)
            {
                state.SetWatermark(task.Name, mapping.Name, watermarkKey, newWatermark, newWatermarkTime);

                // Recorded from inside the same gate that writes the current value, not beside it, so
                // the history on TaskRuns cannot claim an advance ChangeWatermarks did not take.
                watermarkChange = new WatermarkChange(previousWatermark, newWatermark);

                // After the write committed and after the watermark is durable, never before. A
                // reader that acknowledges is telling its source it may discard the history behind
                // this position — do that early and a failed run stops being retryable, which turns
                // a bad pass into permanent data loss. See IPositionAcknowledging.
                await AcknowledgeAsync(
                    reader, sourceConnection!, source, newWatermark, effectiveReader.Options,
                    item.RunId, cancellationToken);
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
    /// The one provisioning action DataSync ever runs unattended (phase 25 §5): additive-only, and only
    /// when the target table does not exist at all. Off by default (<see cref="ProvisioningConfig.CreateTargetTableIfMissing"/>);
    /// when the table already exists this is a no-op — no ALTER, no column reconciliation, and a
    /// mapped column missing from an existing table still fails with the ordinary staging error.
    /// </summary>
    private async Task EnsureTargetTableProvisionedAsync(
        ReplicationTaskConfig task,
        IDriver sourceDriver, DbConnection sourceConnection, SourceTableRef source,
        IDriver targetDriver, DbConnection targetConnection, TableRef target,
        TableMappingConfig mapping, Guid runId, CancellationToken cancellationToken)
    {
        var mayCreate = ProvisioningResolution.CreateTargetTableIfMissing(task, mapping);
        var mayAlter = ProvisioningResolution.AlterTargetTableColumns(task, mapping);
        if ((!mayCreate && !mayAlter) || targetDriver is not IProvisioner provisioner)
            return;

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
        // tables but does not want columns changed underneath it gets exactly that.
        var create = mayCreate
            ? await provisioner.PlanAsync(targetConnection, Request(ProvisioningActions.CreateTargetTable), cancellationToken)
            : null;

        var plan = create is { State: ProvisioningState.Missing or ProvisioningState.Unsupported }
            ? create
            : mayAlter
                ? await provisioner.PlanAsync(targetConnection, Request(ProvisioningActions.AlterTargetTable), cancellationToken)
                : null;

        if (plan is null)
            return;

        var what = plan.Action == ProvisioningActions.CreateTargetTable ? "created" : "altered";

        if (plan.State == ProvisioningState.Unsupported)
        {
            foreach (var warning in plan.Warnings)
                Log(runId, LogSeverity.Warning, $"'{mapping.Name}': target table cannot be auto-{what} — {warning}");
            return;
        }

        if (plan.State != ProvisioningState.Missing)
            return;

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
            throw new ConnectivityException(connectionName, ex);
        }

        return (connection, driver);
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

    /// <summary>
    /// A watermark advance this pass actually made durable, for the history on <c>TaskRuns</c>
    /// (phase 71).
    /// <para>
    /// One nullable value rather than two, so "this run moved the watermark" is a single question with
    /// a single answer. Two loose strings threaded up from the pass would have made the null cases —
    /// a Verification, a Backfill, a pass that read nothing new — four states to reason about where
    /// there is only one that matters.
    /// </para>
    /// </summary>
    private sealed record WatermarkChange(string? Previous, string New);
}

