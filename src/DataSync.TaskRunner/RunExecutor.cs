using System.Data.Common;
using ClrKernel.Core.Secrets;
using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;
using DataSync.State;

namespace DataSync.TaskRunner;

/// <summary>
/// The read -> stage -> apply -> watermark-update pipeline from architecture/detailed-design.md §3.3,
/// as a testable class independent of CLI parsing / process exit codes (see Program.cs for that).
/// </summary>
public sealed class RunExecutor(
    ConfigRepository configRepository,
    DriverRegistry driverRegistry,
    SecretStore secretStore,
    TaskRunStore taskRunStore,
    ChangeWatermarkStore watermarkStore,
    RunLockStore runLockStore,
    LogWriter logWriter)
{
    public async Task<ExitCode> ExecuteAsync(string replicationName, Guid runId, CancellationToken cancellationToken)
    {
        ReplicationTaskConfig task;
        List<TableMappingConfig> mappings;
        try
        {
            task = configRepository.LoadReplicationTask(replicationName);
            mappings = configRepository.ListTableMappings(replicationName)
                .Select(name => configRepository.LoadTableMapping(replicationName, name))
                .ToList();

            foreach (var mapping in mappings)
            {
                if (mapping.Sources.Count != 1 || mapping.Targets.Count != 1)
                    throw new ConfigValidationException(
                        $"Table mapping '{mapping.Name}' has {mapping.Sources.Count} source(s) and " +
                        $"{mapping.Targets.Count} target(s) — DataSync.TaskRunner only executes 1:1 " +
                        "mappings in v1 (the config schema allows more for future fan-in/fan-out).");
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or ConfigValidationException)
        {
            Log(runId, LogSeverity.Error, $"Config error: {ex.Message}");
            logWriter.Flush();
            return ExitCode.ConfigError;
        }

        taskRunStore.UpsertTask(task.Name, task.Enabled);

        if (!runLockStore.TryAcquire(task.Name, runId))
        {
            Log(runId, LogSeverity.Error, $"Task '{task.Name}' already has a run in progress.");
            logWriter.Flush();
            return ExitCode.AlreadyRunning;
        }

        taskRunStore.StartRun(runId, task.Name, Environment.ProcessId);
        Log(runId, LogSeverity.Info, $"Run started for task '{task.Name}' ({mappings.Count} table mapping(s)).");

        try
        {
            var (rowsRead, rowsWritten) = await RunMappingsAsync(task, mappings, runId, cancellationToken);

            taskRunStore.CompleteRun(runId, RunStatus.Succeeded, rowsRead, rowsWritten, errorSummary: null);
            Log(runId, LogSeverity.Info, $"Run succeeded: {rowsRead} row(s) read, {rowsWritten} row(s) written.");
            return ExitCode.Success;
        }
        catch (ConnectivityException ex)
        {
            taskRunStore.CompleteRun(runId, RunStatus.Failed, 0, 0, ex.Message);
            Log(runId, LogSeverity.Error, $"Run failed: {ex.Message}");
            return ExitCode.ConnectivityError;
        }
        catch (Exception ex)
        {
            taskRunStore.CompleteRun(runId, RunStatus.Failed, 0, 0, ex.Message);
            Log(runId, LogSeverity.Error, $"Run failed: {ex.Message}");
            return ExitCode.DataError;
        }
        finally
        {
            runLockStore.Release(task.Name);
            logWriter.Flush();
        }
    }

    private async Task<(long RowsRead, long RowsWritten)> RunMappingsAsync(
        ReplicationTaskConfig task, List<TableMappingConfig> mappings, Guid runId, CancellationToken cancellationToken)
    {
        long totalRowsRead = 0;
        long totalRowsWritten = 0;

        // One connection per distinct (role, ConnectionName), reused across mappings, but source and
        // target connections are always distinct instances even when they name the same connection —
        // see IChangeReader's XML doc on why sharing one connection across a read and a bulk-copy
        // write deadlocks (found during Phase 3).
        var sourceConnections = new Dictionary<string, (DbConnection Connection, IDriver Driver)>();
        var targetConnections = new Dictionary<string, (DbConnection Connection, IDriver Driver)>();

        try
        {
            foreach (var mapping in mappings)
            {
                var source = mapping.Sources[0];
                var target = mapping.Targets[0];

                var (sourceConnection, sourceDriver) = await GetOrOpenAsync(sourceConnections, source.ConnectionName, cancellationToken);
                var (targetConnection, targetDriver) = await GetOrOpenAsync(targetConnections, target.ConnectionName, cancellationToken);

                var reader = sourceDriver.Readers.FirstOrDefault(r => r.Kind == task.ChangeProcessing.Reader.Kind)
                    ?? throw new InvalidOperationException(
                        $"Source driver does not support reader kind '{task.ChangeProcessing.Reader.Kind}'.");
                var stagingProvider = targetDriver.StagingProviders.FirstOrDefault(p => p.Kind == task.ChangeProcessing.Cache.Kind)
                    ?? throw new InvalidOperationException(
                        $"Target driver does not support staging kind '{task.ChangeProcessing.Cache.Kind}'.");
                var writer = targetDriver.Writers.FirstOrDefault(w => w.Kind == task.ChangeProcessing.Writer.Kind)
                    ?? throw new InvalidOperationException(
                        $"Target driver does not support writer kind '{task.ChangeProcessing.Writer.Kind}'.");

                var watermarkKey = WatermarkKey.Build(source);
                var previousCursor = watermarkStore.GetWatermark(task.Name, watermarkKey);

                Log(runId, LogSeverity.Info, $"Reading changes for '{mapping.Name}' (cursor: {previousCursor ?? "<none>"}).");
                var read = await reader.ReadChangesAsync(
                    sourceConnection, source, previousCursor, task.ChangeProcessing.Reader.Options, cancellationToken);

                var staged = await stagingProvider.StageAsync(
                    targetConnection, target, read.Rows, mapping.ColumnMappings, task.ChangeProcessing.Cache.Options, cancellationToken);

                var written = await writer.ApplyAsync(
                    targetConnection, target, staged, mapping.ColumnMappings, task.ChangeProcessing.Writer.Options, cancellationToken);

                watermarkStore.SetWatermark(task.Name, watermarkKey, read.NewCursor);

                totalRowsRead += staged.RowCount;
                totalRowsWritten += written.RowsWritten;
                Log(runId, LogSeverity.Info,
                    $"'{mapping.Name}': {staged.RowCount} row(s) read, {written.RowsWritten} row(s) written.");
            }
        }
        finally
        {
            foreach (var (connection, _) in sourceConnections.Values.Concat(targetConnections.Values))
                connection.Dispose();
        }

        return (totalRowsRead, totalRowsWritten);
    }

    private async Task<(DbConnection Connection, IDriver Driver)> GetOrOpenAsync(
        Dictionary<string, (DbConnection Connection, IDriver Driver)> pool,
        string connectionName,
        CancellationToken cancellationToken)
    {
        if (pool.TryGetValue(connectionName, out var existing))
            return existing;

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

        var entry = (connection, driver);
        pool[connectionName] = entry;
        return entry;
    }

    private void Log(Guid runId, LogSeverity level, string message) => logWriter.Log(runId, level, message);

    private sealed class ConnectivityException(string connectionName, Exception inner)
        : Exception($"Failed to open connection '{connectionName}': {inner.Message}", inner);
}
