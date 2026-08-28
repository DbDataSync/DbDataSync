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
    LogWriter logWriter,
    ScriptHost scriptHost)
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
                sourceDriver, sourceConnection, source, targetDriver, targetConnection, target, mapping, item.RunId, cancellationToken);

            var segments = await ResolveSegmentsAsync(reader, sourceConnection, source, item, processing.Reader.Options, cancellationToken);
            if (segments.Count > 1)
                Log(item.RunId, LogSeverity.Info, $"Processing {segments.Count} configured segment(s) in this pass.");

            long totalRead = 0;
            long totalWritten = 0;
            string? newWatermark = null;

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
                var readerOptions = WithSegment(processing.Reader.Options, segment);
                var cacheOptions = WithSegment(processing.Cache.Options, segment);
                var writerOptions = WithSegment(processing.Writer.Options, segment);
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

                var read = await reader.ReadChangesAsync(
                    sourceConnection, source, previousWatermark, columnMappings, readerOptions, cancellationToken);
                var rows = transforms.IsEmpty
                    ? read.Rows
                    : transforms.ApplyAsync(
                        read.Rows,
                        dropped => Log(item.RunId, LogSeverity.Info,
                            $"'{mapping.Name}': {dropped} row(s) dropped by a transform script."),
                        cancellationToken);

                await RunHooksAsync(
                    HookPoints.BeforeStage, hooksByPoint[HookPoints.BeforeStage], Context(null, null, null),
                    targetConnection, targetDialect, source.ConnectionName, sourceDialect, mapping.Name, item.RunId, cancellationToken);
                await RunGeneratedAsync(HookPoints.BeforeStage, null, null, null);

                var staged = await stagingProvider.StageAsync(
                    targetConnection, target, rows, mapping.ColumnMappings, cacheOptions, cancellationToken);

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

                    var written = await writer.ApplyAsync(
                        targetConnection, target, staged, mapping.ColumnMappings, writerOptions, cancellationToken);

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
        IDriver sourceDriver, DbConnection sourceConnection, SourceTableRef source,
        IDriver targetDriver, DbConnection targetConnection, TableRef target,
        TableMappingConfig mapping, Guid runId, CancellationToken cancellationToken)
    {
        if (!mapping.Provisioning.CreateTargetTableIfMissing || targetDriver is not IProvisioner provisioner)
            return;

        var sourceColumns = await sourceDriver.ListColumnsAsync(
            sourceConnection, source.Database, source.Schema, source.Table, cancellationToken);
        var (columns, identityWarnings) = ProvisioningColumnBuilder.Build(
            ResolveDialect(sourceDriver), sourceColumns, mapping.ColumnMappings);

        var request = new ProvisioningRequest(
            ProvisioningActions.CreateTargetTable, target, ReaderKind: null,
            ReaderOptions: new Dictionary<string, string>(), columns);
        var plan = await provisioner.PlanAsync(targetConnection, request, cancellationToken);

        if (plan.State == ProvisioningState.Unsupported)
        {
            foreach (var warning in plan.Warnings)
                Log(runId, LogSeverity.Warning, $"'{mapping.Name}': target table cannot be auto-created — {warning}");
            return;
        }

        if (plan.State != ProvisioningState.Missing)
            return;

        foreach (var warning in identityWarnings.Concat(plan.Warnings))
            Log(runId, LogSeverity.Warning, $"'{mapping.Name}': {warning}");

        foreach (var step in plan.Steps)
        {
            using var cmd = targetConnection.CreateCommand();
            cmd.CommandText = step.CommandText;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            Log(runId, LogSeverity.Info, $"'{mapping.Name}': target table created — {step.CommandText}");
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
            using var cmd = connection.CreateCommand();
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

