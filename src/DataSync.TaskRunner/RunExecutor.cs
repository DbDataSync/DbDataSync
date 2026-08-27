using System.Data.Common;
using System.Threading.Channels;
using ClrKernel.Core.Secrets;
using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;
using DataSync.State;

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
    TaskRunStore taskRunStore,
    ChangeWatermarkStore watermarkStore,
    RunLockStore runLockStore,
    WorkQueueStore workQueueStore,
    LogWriter logWriter)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

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

        taskRunStore.UpsertTask(task.Name, task.Enabled);

        var workerId = Guid.NewGuid().ToString("N");
        var channel = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(Math.Max(1, degreeOfParallelism) * 2)
        {
            SingleWriter = true,
            SingleReader = degreeOfParallelism == 1,
        });

        var producer = ProduceAsync(taskName, workerId, channel.Writer, cancellationToken);
        var consumers = Enumerable.Range(0, Math.Max(1, degreeOfParallelism))
            .Select(_ => ConsumeAsync(channel.Reader, cancellationToken))
            .ToArray();

        await producer;
        await Task.WhenAll(consumers);
        logWriter.Flush();

        return ExitCode.Success;
    }

    /// <summary>Empty polls (queue observed to have no outstanding work) required before the worker
    /// actually exits. Without this grace period, a worker that happens to finish draining at almost
    /// exactly the moment ProcessSupervisor.EnsureWorkerRunning is called (it no-ops if a tracked
    /// process is still alive, cheaper than always spawning fresh) can exit a moment before claiming
    /// newly-enqueued work — and nothing else re-triggers a worker for a one-off manual trigger that
    /// didn't come from SchedulerService's own due-ness check. This narrows that race to needing an
    /// enqueue to land during this specific window, rather than eliminating it structurally — a full
    /// fix (e.g. a worker heartbeat EnsureWorkerRunning can check against, not just OS process
    /// liveness) is real follow-on work, not built here. See
    /// architecture/implementation/done/phase-008-work-queue-schema.md.</summary>
    private const int EmptyPollsBeforeExit = 5;

    /// <summary>Claims the next available item for this task in a loop, feeding it to the bounded
    /// channel (which applies backpressure once consumers fall behind), until the queue is observed
    /// empty for this task across <see cref="EmptyPollsBeforeExit"/> consecutive polls — at which
    /// point the worker has nothing left to do and exits. New work enqueued after this point is
    /// picked up by the next spawned worker (see ProcessSupervisor).</summary>
    private async Task ProduceAsync(string taskName, string workerId, ChannelWriter<WorkItem> writer, CancellationToken cancellationToken)
    {
        var consecutiveEmptyPolls = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var item = workQueueStore.TryClaimNext(taskName, workerId);
                if (item is not null)
                {
                    consecutiveEmptyPolls = 0;
                    await writer.WriteAsync(item, cancellationToken);
                }
                else if (!workQueueStore.HasOutstandingWork(taskName))
                {
                    if (++consecutiveEmptyPolls >= EmptyPollsBeforeExit)
                        break;
                    await Task.Delay(PollInterval, cancellationToken);
                }
                else
                {
                    consecutiveEmptyPolls = 0;
                    await Task.Delay(PollInterval, cancellationToken);
                }
            }
        }
        finally
        {
            writer.Complete();
        }
    }

    private async Task ConsumeAsync(ChannelReader<WorkItem> reader, CancellationToken cancellationToken)
    {
        await foreach (var item in reader.ReadAllAsync(cancellationToken))
            await ProcessWorkItemAsync(item, cancellationToken);
    }

    private async Task ProcessWorkItemAsync(WorkItem item, CancellationToken cancellationToken)
    {
        if (!runLockStore.TryAcquire(item.TaskName, item.RunKind, item.MappingName, item.RunId))
        {
            // Lost the real RunLocks race despite the queue's own NOT EXISTS pre-filter (rare) — give
            // the claim back so a later poll retries it, rather than recording a spurious failure.
            workQueueStore.ReleaseClaim(item.Id);
            return;
        }

        workQueueStore.MarkRunning(item.Id);
        taskRunStore.BeginRun(item.RunId, Environment.ProcessId);
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

            var (rowsRead, rowsWritten) = await RunMappingAsync(task, mapping, item, cancellationToken);

            Log(item.RunId, LogSeverity.Info, $"Run succeeded: {rowsRead} row(s) read, {rowsWritten} row(s) written.");
            // Flush before CompleteRun, not after: one worker process now handles many runs over its
            // lifetime, so a run's TaskRuns row can go terminal (and RunMonitorService stop watching
            // it) long before the process itself exits — unlike the old one-run-per-process model,
            // where a single end-of-process Flush() was always guaranteed to happen first. Log lines
            // must be durable before the status that makes a poller stop looking for them.
            logWriter.Flush();
            taskRunStore.CompleteRun(item.RunId, RunStatus.Succeeded, rowsRead, rowsWritten, errorSummary: null);
            workQueueStore.MarkDone(item.Id);
        }
        catch (Exception ex) when (ex is FileNotFoundException or ConfigValidationException)
        {
            Log(item.RunId, LogSeverity.Error, $"Config error: {ex.Message}");
            logWriter.Flush();
            taskRunStore.CompleteRun(item.RunId, RunStatus.Failed, 0, 0, ex.Message);
            workQueueStore.MarkFailed(item.Id);
        }
        catch (ConnectivityException ex)
        {
            Log(item.RunId, LogSeverity.Error, $"Run failed: {ex.Message}");
            logWriter.Flush();
            taskRunStore.CompleteRun(item.RunId, RunStatus.Failed, 0, 0, ex.Message);
            workQueueStore.MarkFailed(item.Id);
        }
        catch (Exception ex)
        {
            Log(item.RunId, LogSeverity.Error, $"Run failed: {ex.Message}");
            logWriter.Flush();
            taskRunStore.CompleteRun(item.RunId, RunStatus.Failed, 0, 0, ex.Message);
            workQueueStore.MarkFailed(item.Id);
        }
        finally
        {
            runLockStore.Release(item.TaskName, item.RunKind, item.MappingName);
        }
    }

    private async Task<(long RowsRead, long RowsWritten)> RunMappingAsync(
        ReplicationTaskConfig task, TableMappingConfig mapping, WorkItem item, CancellationToken cancellationToken)
    {
        var source = EndpointResolution.ResolveSource(task, mapping.Sources[0]);
        var target = EndpointResolution.ResolveTarget(task, mapping.Targets[0]);
        var processing = task.ChangeProcessing;

        DbConnection? sourceConnection = null;
        DbConnection? targetConnection = null;
        try
        {
            (sourceConnection, var sourceDriver) = await OpenConnectionAsync(source.ConnectionName, cancellationToken);
            (targetConnection, var targetDriver) = await OpenConnectionAsync(target.ConnectionName, cancellationToken);

            // A unit of work may override the replication's configured pipeline. It's how a Backfill
            // of an incrementally-synced replication reloads a segment at all: the replication's own
            // reader reports changes since a watermark, which is not what reloading a segment means.
            var readerKind = item.Kinds.ReaderKind ?? processing.Reader.Kind;
            var cacheKind = item.Kinds.CacheKind ?? processing.Cache.Kind;
            var writerKind = item.Kinds.WriterKind ?? processing.Writer.Kind;

            var reader = sourceDriver.Readers.FirstOrDefault(r => r.Kind == readerKind)
                ?? throw new InvalidOperationException($"Source driver does not support reader kind '{readerKind}'.");
            var stagingProvider = targetDriver.StagingProviders.FirstOrDefault(p => p.Kind == cacheKind)
                ?? throw new InvalidOperationException($"Target driver does not support staging kind '{cacheKind}'.");
            var writer = targetDriver.Writers.FirstOrDefault(w => w.Kind == writerKind)
                ?? throw new InvalidOperationException($"Target driver does not support writer kind '{writerKind}'.");

            if (item.Kinds != WorkItemKinds.FromConfig)
                Log(item.RunId, LogSeverity.Info,
                    $"Using reader '{readerKind}', cache '{cacheKind}', writer '{writerKind}' for this {item.RunKind} " +
                    $"(the replication itself is configured for '{processing.Reader.Kind}'/'{processing.Cache.Kind}'/'{processing.Writer.Kind}').");

            // Only a Primary pass ever advances the incremental watermark — a Backfill must never be
            // able to disturb the cursor a replication's ongoing incremental sync depends on,
            // regardless of which reader/writer Kind it happens to use internally.
            var watermarkKey = WatermarkKey.Build(source);
            var previousWatermark = item.RunKind == RunKind.Primary ? watermarkStore.GetWatermark(task.Name, watermarkKey) : null;

            var segments = await ResolveSegmentsAsync(reader, sourceConnection, source, item, processing.Reader.Options, cancellationToken);
            if (segments.Count > 1)
                Log(item.RunId, LogSeverity.Info, $"Processing {segments.Count} configured segment(s) in this pass.");

            long totalRead = 0;
            long totalWritten = 0;
            string? newWatermark = null;

            foreach (var segment in segments)
            {
                var readerOptions = WithSegment(processing.Reader.Options, segment);
                var cacheOptions = WithSegment(processing.Cache.Options, segment);
                var writerOptions = WithSegment(processing.Writer.Options, segment);

                var scope = segment?.Describe() ?? "whole table";
                Log(item.RunId, LogSeverity.Info,
                    $"Reading changes for '{mapping.Name}' ({scope}, watermark: {previousWatermark ?? "<none>"}).");

                var read = await reader.ReadChangesAsync(
                    sourceConnection, source, previousWatermark, mapping.ColumnMappings, readerOptions, cancellationToken);
                var staged = await stagingProvider.StageAsync(
                    targetConnection, target, read.Rows, mapping.ColumnMappings, cacheOptions, cancellationToken);

                // Only meaningful now that staging has drained the reader's stream. A run that skipped
                // rows is a run whose source was changing under it — worth surfacing next to a mapping
                // that looks slow or keeps retrying, rather than leaving it invisible.
                if (read.Diagnostics is { RowsSkippedSourceRowGone: > 0 } diagnostics)
                    Log(item.RunId, LogSeverity.Warning,
                        $"{diagnostics.RowsSkippedSourceRowGone} row(s) skipped: the source row was deleted while " +
                        "this pass was reading it. Each one's deletion is applied on a later pass.");

                try
                {
                    var written = await writer.ApplyAsync(
                        targetConnection, target, staged, mapping.ColumnMappings, writerOptions, cancellationToken);

                    totalRead += staged.RowCount;
                    totalWritten += written.RowsWritten;
                    Log(item.RunId, LogSeverity.Info,
                        $"'{mapping.Name}' ({scope}): {staged.RowCount} row(s) read, {written.RowsWritten} row(s) written.");
                }
                finally
                {
                    // One pass can stage several times, so the staged set has to be discarded as it
                    // goes rather than left for connection teardown to deal with.
                    await stagingProvider.CleanupAsync(targetConnection, staged, CancellationToken.None);
                }

                newWatermark = read.NewWatermark;
            }

            // Segmented passes only ever happen with a reload reader, which has no watermark of its
            // own and echoes back whatever it was given — so taking the last segment's value is the
            // same as taking any of them. An ordinary incremental pass has exactly one segment (none).
            if (item.RunKind == RunKind.Primary && newWatermark is not null)
                watermarkStore.SetWatermark(task.Name, watermarkKey, newWatermark);

            return (totalRead, totalWritten);
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
    private static async Task<IReadOnlyList<BatchReloadSegment?>> ResolveSegmentsAsync(
        IChangeReader reader,
        DbConnection sourceConnection,
        SourceTableRef source,
        WorkItem item,
        IReadOnlyDictionary<string, string> readerOptions,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(item.SegmentJson))
            return [SegmentSerializer.Deserialize(item.SegmentJson)];

        if (!readerOptions.TryGetValue(SegmentSerializer.SegmentsOptionKey, out var configured) || string.IsNullOrWhiteSpace(configured))
            return [null];

        var segments = SegmentSerializer.DeserializeMany(configured);

        // Auto segments are resolved against the source's actual value range, so they're expanded
        // here rather than at config-save time — the range moves as the table does.
        if (reader is ISegmentExpandingReader expanding)
            segments = await expanding.ExpandAutoSegmentsAsync(sourceConnection, source, segments, cancellationToken);

        return [.. segments];
    }

    /// <summary>Injects the work item's segment into a per-iteration copy of one role's options under
    /// the well-known key. All three roles get it: the reader needs it to scope what it reads, and a
    /// reconciling writer needs the same scope to know which target rows the reload is accountable
    /// for. The configured options dictionary is never mutated — it's shared across every item this
    /// worker processes.</summary>
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

    private void Log(Guid runId, LogSeverity level, string message) => logWriter.Log(runId, level, message);

    private sealed class ConnectivityException(string connectionName, Exception inner)
        : Exception($"Failed to open connection '{connectionName}': {inner.Message}", inner);
}
