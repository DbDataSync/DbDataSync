using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace DbDataSync.Drivers.MsSql.Tests;

/// <summary>
/// CDC's capture is a SQL Server Agent job that scans the transaction log on its own timer, so
/// nothing a test writes is readable the instant it commits — and on a loaded CI runner the wait for
/// that timer to come round was the integration suite's most common flake ("The CDC capture job did
/// not advance within 60s", and cleanup rounding against a change table the job had not caught up on).
/// <para>
/// These fixtures instead drive the scan themselves with <see cref="ScanAsync"/>: it stops the Agent
/// capture job, waits for it to actually halt (<c>sp_cdc_stop_job</c> returns before the job does),
/// and runs <c>sys.sp_cdc_scan</c> — after which everything committed beforehand is captured, with no
/// polling, latency, or a background job fighting it for the log reader. <see cref="DiagnoseAsync"/>
/// stays for the messages on the deadlock/other retries that remain.
/// </para>
/// </summary>
internal static class CdcCaptureJob
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Enables CDC on the connection's current database if it is not already. Idempotent. Registers
    /// the Agent jobs but does not create the capture job — that happens on the first
    /// <c>sp_cdc_enable_table</c>, which also starts it.
    /// </summary>
    public static async Task EnableDbAsync(SqlConnection connection)
    {
        if (await MsSqlCdcCatalog.CdcIsEnabledAsync(connection, CancellationToken.None))
            return;

        await ExecuteAsync(connection, "EXEC sys.sp_cdc_enable_db;");
    }

    /// <summary>
    /// Forces a synchronous scan of the log: everything committed before this returns is captured and
    /// its LSN mapped in <c>cdc.lsn_time_mapping</c>. Stops the Agent capture job first (it is
    /// restarted by <c>sp_cdc_enable_table</c>, so this cannot be a one-time step) and waits for it to
    /// release the log reader. Returns the latest <c>tran_end_time</c> the scan produced in
    /// <c>cdc.lsn_time_mapping</c> — see <see cref="ScanUntilPastAsync"/>, which uses it to wait for a
    /// genuinely later mapping point instead of guessing a delay.
    /// </summary>
    public static async Task<DateTime> ScanAsync(SqlConnection connection)
    {
        await StopCaptureJobAsync(connection);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // @maxtrans must be > 0; these bounds are far above anything a test writes in a pass.
                await ExecuteAsync(connection,
                    "EXEC sys.sp_cdc_scan @maxtrans = 5000, @maxscans = 10, @continuous = 0;");

                // sp_cdc_scan checks the log reader (sp_replcmds) out to this session and does not
                // check it back in — so a pooled connection carries the lock back to the pool and the
                // next test's scan fails with "another connection is already running 'sp_replcmds'".
                // @reset = 1 hands it back; guarded because with nothing checked out it raises.
                await ExecuteAsync(connection, """
                    BEGIN TRY
                        EXEC sys.sp_repldone @xactid = NULL, @xact_seqno = NULL, @numtrans = 0, @time = 0, @reset = 1;
                    END TRY BEGIN CATCH END CATCH;
                    """);
                return await LatestMappedTimeAsync(connection);
            }
            catch (SqlException ex) when (attempt < 20 && IsScanBusy(ex))
            {
                // The just-stopped job, or a not-yet-released pooled connection, still has the log
                // reader for a beat.
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }
    }

    /// <summary>
    /// The latest transaction commit time <c>cdc.lsn_time_mapping</c> has recorded — the same value
    /// CDC's own mapped-time reads for the source table derive from, so comparing it before and after an
    /// operation proves whether that operation actually got a new mapping point.
    /// </summary>
    public static async Task<DateTime> LatestMappedTimeAsync(SqlConnection connection)
    {
        var result = await ScalarAsync(connection, "SELECT MAX(tran_end_time) FROM cdc.lsn_time_mapping;");
        return result is DateTime time ? time : DateTime.MinValue;
    }

    /// <summary>
    /// Re-scans until <c>cdc.lsn_time_mapping</c> records a transaction strictly after
    /// <paramref name="after"/> — proof the next tracked change will map to a genuinely later time,
    /// rather than a fixed delay long enough on one runner and not on another. Replaces an earlier fix
    /// that waited 30 ms on the theory that <c>datetime</c>'s 3.33 ms tick was the only granularity in
    /// play; CI kept producing identical mapped times anyway even with that gap in place, so whatever
    /// actually governs how often a mapping point advances is coarser or load-dependent, and worth
    /// checking for rather than out-waiting (see
    /// architecture/planning/todo/follow-up-phase-154-scd2-cdc-timestamp-mapping-race.md).
    /// </summary>
    public static async Task<DateTime> ScanUntilPastAsync(SqlConnection connection, DateTime after)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            var latest = await ScanAsync(connection);
            if (latest > after)
                return latest;

            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException(
                    $"cdc.lsn_time_mapping did not record a transaction after {after:O} within 30s "
                    + $"(latest seen: {latest:O}). {await DiagnoseAsync(connection)}");

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
    }

    /// <summary>
    /// Stops the Agent capture job and waits until it has actually halted — <c>sp_cdc_stop_job</c>
    /// only requests the stop. A stop request for an already-stopped job is the goal state, not an
    /// error. Call from <c>DisposeAsync</c> too, so the next CDC test class does not start against a
    /// job still scanning this class's (about-to-be-dropped) database.
    /// </summary>
    public static async Task StopCaptureJobAsync(SqlConnection connection)
    {
        try
        {
            await ExecuteAsync(connection, "EXEC sys.sp_cdc_stop_job @job_type = N'capture';");
        }
        catch (SqlException ex) when (ex.Message.Contains("not currently running", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < StopTimeout)
        {
            if (!await CaptureJobRunningAsync(connection))
                return;
            await Task.Delay(250);
        }
    }

    /// <summary>Whether an Agent capture-job step is live against this database right now.</summary>
    private static async Task<bool> CaptureJobRunningAsync(SqlConnection connection)
    {
        var live = await ScalarAsync(connection, """
            SELECT COUNT(*)
            FROM sys.dm_exec_sessions
            WHERE program_name LIKE N'SQLAgent - TSQL JobStep%'
              AND database_id = DB_ID();
            """);
        return live is int count && count > 0;
    }

    private static bool IsScanBusy(SqlException ex) =>
        ex.Message.Contains("currently running", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("already running 'sp_replcmds'", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("is already running", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A one-line snapshot of the capture job and CDC's scan errors, for a timeout message — so a
    /// genuinely dead Agent is no longer indistinguishable from a slow one.
    /// </summary>
    public static async Task<string> DiagnoseAsync(SqlConnection connection)
    {
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM msdb.dbo.sysjobs WHERE name LIKE N'cdc.%_capture' AND enabled = 1),
                    (SELECT COUNT(*) FROM sys.dm_cdc_errors),
                    (SELECT TOP (1) error_message FROM sys.dm_cdc_errors ORDER BY entry_time DESC);
                """;
            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return "no capture-job diagnostics";

            var enabledJobs = reader.GetInt32(0);
            var errorCount = reader.GetInt32(1);
            var lastError = reader.IsDBNull(2) ? null : reader.GetString(2);
            return $"capture jobs enabled: {enabledJobs}; CDC scan errors: {errorCount}"
                + (lastError is null ? "" : $" (last: {lastError})");
        }
        catch (Exception ex)
        {
            return $"capture-job diagnostics unavailable: {ex.Message}";
        }
    }

    private static async Task<object?> ScalarAsync(SqlConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? null : result;
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
