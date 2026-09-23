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
    /// genuinely later mapping point instead of guessing a delay, and shares this method's own
    /// stop/scan/release machinery rather than repeating a full stop-and-release cycle per retry — see
    /// that method's own doc comment for why that repetition, not scan timing, is the likelier cause of
    /// a real recurrence.
    /// </summary>
    public static async Task<DateTime> ScanAsync(SqlConnection connection)
    {
        await StopCaptureJobAsync(connection);
        var latest = await ScanOnceAsync(connection);
        await ReleaseLogReaderAsync(connection);
        return latest;
    }

    /// <summary>
    /// The repeatable half of a scan: assumes the capture job is already stopped and the log reader
    /// already checked out to this session (both <see cref="ScanAsync"/> and
    /// <see cref="ScanUntilPastAsync"/> arrange that once, before calling this any number of times) —
    /// <c>sp_cdc_scan</c> itself, then the latest mapped time it produced.
    /// </summary>
    private static async Task<DateTime> ScanOnceAsync(SqlConnection connection)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // @maxtrans must be > 0; these bounds are far above anything a test writes in a pass.
                await ExecuteAsync(connection,
                    "EXEC sys.sp_cdc_scan @maxtrans = 5000, @maxscans = 10, @continuous = 0;");
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
    /// <c>sp_cdc_scan</c> checks the log reader (<c>sp_replcmds</c>) out to this session and does not
    /// check it back in — so a pooled connection carries the lock back to the pool and the next test's
    /// scan fails with "another connection is already running 'sp_replcmds'". <c>@reset = 1</c> hands
    /// it back; guarded because with nothing checked out it raises. Called once per logical scan
    /// operation (a single <see cref="ScanAsync"/>, or the whole retry loop in
    /// <see cref="ScanUntilPastAsync"/>) — never once per retry attempt, since there is nothing to hand
    /// back until that operation is actually done with the session.
    /// </summary>
    private static async Task ReleaseLogReaderAsync(SqlConnection connection) =>
        await ExecuteAsync(connection, """
            BEGIN TRY
                EXEC sys.sp_repldone @xactid = NULL, @xact_seqno = NULL, @numtrans = 0, @time = 0, @reset = 1;
            END TRY BEGIN CATCH END CATCH;
            """);

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

    private static readonly TimeSpan ScanUntilPastDeadline = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Re-scans until <c>cdc.lsn_time_mapping</c> records a transaction strictly after
    /// <paramref name="after"/> — proof the next tracked change will map to a genuinely later time,
    /// rather than a fixed delay long enough on one runner and not on another. Replaces an earlier fix
    /// that waited 30 ms on the theory that <c>datetime</c>'s 3.33 ms tick was the only granularity in
    /// play; CI kept producing identical mapped times anyway even with that gap in place, so whatever
    /// actually governs how often a mapping point advances is coarser or load-dependent, and worth
    /// checking for rather than out-waiting (see
    /// architecture/planning/todo/follow-up-phase-154-scd2-cdc-timestamp-mapping-race.md).
    /// <para>
    /// The deadline was originally 30s; run <c>35854242228</c> (2026-09-23) timed out at 30s with every
    /// attempt in the window reporting the identical <c>latest</c> value, so it was widened to 90s on
    /// the theory that CDC's log scan itself was occasionally lagging under I/O-contended CI. **That
    /// theory was falsified by the very next recurrence** (run <c>35901453430</c>, 2026-09-23): 777
    /// scan attempts across the full 90s, every one reporting the identical, unmoved <c>latest</c>. A
    /// genuine catch-up lag would not survive 777 real attempts without ever budging once — that many
    /// fast, cheap attempts finding nothing points at a real stall, not a slow-but-eventually one, and
    /// widening the deadline further would only spend more CI time arriving at the same failure.
    /// </para>
    /// <para>
    /// What changed instead: this loop used to call the *whole* <see cref="ScanAsync"/> — stop the
    /// capture job, scan, release the log reader — on every single 100ms retry, meaning a stuck wait
    /// re-issued <c>sp_cdc_stop_job</c> and, more pointedly, <c>sp_repldone @reset = 1</c> hundreds of
    /// times against the very capture mechanism it was waiting on. <c>sp_repldone</c> is shared
    /// infrastructure with transactional replication, whose actual job is advancing a "how far has this
    /// been consumed" marker — its exact interaction with CDC's own internal bookkeeping under repeated,
    /// rapid, out-of-band calls with nothing new consumed in between is not something this comment
    /// claims to fully understand, but "stop hammering the mechanism you are waiting on with a call
    /// whose whole job is marking things as already handled" is a strictly safer, more targeted change
    /// than guessing at a longer timeout again. The capture job is now stopped once and the log reader
    /// released once per logical wait, not once per 100ms poll — see <see cref="ScanOnceAsync"/> and
    /// <see cref="ReleaseLogReaderAsync"/>.
    /// </para>
    /// </summary>
    public static async Task<DateTime> ScanUntilPastAsync(SqlConnection connection, DateTime after)
    {
        await StopCaptureJobAsync(connection);
        try
        {
            var deadline = DateTime.UtcNow + ScanUntilPastDeadline;
            var attempts = 0;
            while (true)
            {
                attempts++;
                var latest = await ScanOnceAsync(connection);
                if (latest > after)
                    return latest;

                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException(
                        $"cdc.lsn_time_mapping did not record a transaction after {after:O} within "
                        + $"{ScanUntilPastDeadline.TotalSeconds:0}s ({attempts} scan attempts, latest seen: "
                        + $"{latest:O}). {await DiagnoseAsync(connection)}");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }
        }
        finally
        {
            await ReleaseLogReaderAsync(connection);
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
    /// <para>
    /// Tried and dropped, checked live rather than assumed (after run <c>35854242228</c>'s timeout):
    /// comparing <c>sys.fn_cdc_get_max_lsn()</c> against <c>MAX(start_lsn)</c> from
    /// <c>cdc.lsn_time_mapping</c>, on the theory that the former reads the live transaction log's own
    /// current end and would reveal whether the log had more to give than CDC had scanned. A throwaway
    /// probe against a real CDC-enabled table showed <c>fn_cdc_get_max_lsn()</c> reads the *same*
    /// value as <c>MAX(start_lsn)</c> — before a newly-inserted row's scan and after, identically — so
    /// it is sourced from <c>cdc.lsn_time_mapping</c> itself, not an independent view of the log. That
    /// comparison would always report "nothing unmapped," which is not a diagnostic, so it isn't here.
    /// </para>
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
                    (SELECT TOP (1) error_message FROM sys.dm_cdc_errors ORDER BY entry_time DESC),
                    (SELECT COUNT(*) FROM cdc.lsn_time_mapping);
                """;
            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return "no capture-job diagnostics";

            var enabledJobs = reader.GetInt32(0);
            var errorCount = reader.GetInt32(1);
            var lastError = reader.IsDBNull(2) ? null : reader.GetString(2);
            var mappingRows = reader.GetInt32(3);
            return $"capture jobs enabled: {enabledJobs}; CDC scan errors: {errorCount}"
                + (lastError is null ? "" : $" (last: {lastError})")
                + $"; lsn_time_mapping rows: {mappingRows}";
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
