using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDataSync.Drivers.MsSql.Tests;

/// <summary>
/// That CDC will state, itself, when a position committed — the fact the whole of phase 85's CDC lag
/// rests on, and the counterpart of what <c>MsSqlChangeTrackingVersionTimeTests</c> asserts for the
/// other mechanism.
/// <para>
/// Against a real server because <c>fn_cdc_map_lsn_to_time</c> reads <c>cdc.lsn_time_mapping</c>,
/// which the capture job populates: there is nothing to fake here that would still be testing the
/// claim.
/// </para>
/// <para>
/// **A captured table, not just a CDC-enabled database.** <c>sp_cdc_enable_db</c> registers the Agent
/// jobs, but with no capture instance for the job to scan there is nothing to write to the mapping
/// table and <c>fn_cdc_get_max_lsn()</c> stays null for ever — which reads exactly like a broken
/// Agent and cost this file a 90-second timeout before the setup below existed.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class MsSqlCdcLsnTimeTests(MsSqlTestDatabase db)
    : IClassFixture<MsSqlTestDatabase>, IAsyncLifetime
{
    private SqlConnection _connection = null!;
    private string _tableName = null!;

    public async Task InitializeAsync()
    {
        _connection = db.OpenConnection();
        _tableName = $"CdcLsnTime_{Guid.NewGuid():N}";

        await ExecuteAsync($"""
            CREATE TABLE dbo.[{_tableName}] (
                Id INT NOT NULL PRIMARY KEY,
                Name NVARCHAR(50) NOT NULL
            );
            """);

        if (!await MsSqlCdcCatalog.CdcIsEnabledAsync(_connection, CancellationToken.None))
            await ExecuteAsync("EXEC sys.sp_cdc_enable_db;");

        await EnableCaptureWithRetryAsync();
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task TheMaxLsn_MapsToATimeTheEngineStates()
    {
        var max = await WaitForMaxLsnAsync();

        var mapped = await MsSqlCdcCatalog.MapLsnToTimeAsync(_connection, max, CancellationToken.None);

        // A real time, not a null and not a guess. Loosely bounded on purpose: the value is the
        // source server's own clock, which is what makes it usable as one end of a difference against
        // another value from this same function and unusable as an absolute against ours.
        Assert.NotNull(mapped);
        Assert.InRange(
            mapped.Value.DateTime,
            DateTime.UtcNow.AddDays(-1),
            DateTime.UtcNow.AddDays(1));
    }

    [Fact]
    public async Task AnLsnOutsideTheMapping_IsNullRatherThanAnError()
    {
        await WaitForMaxLsnAsync();

        // A position from before anything this database retains. Null is the honest answer and the
        // one lag reports as "unknown"; an exception here would make a status screen fail over a
        // watermark that has simply aged out.
        var mapped = await MsSqlCdcCatalog.MapLsnToTimeAsync(
            _connection, new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 }, CancellationToken.None);

        Assert.Null(mapped);
    }

    /// <summary>
    /// Retried on 1205, for the reason <c>MsSqlCdcReaderTests</c> sets out at length: enabling
    /// capture touches msdb, so does the capture job already running for this database, and the two
    /// deadlock often enough to matter.
    /// </summary>
    private async Task EnableCaptureWithRetryAsync()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await ExecuteAsync($"""
                    EXEC sys.sp_cdc_enable_table @source_schema = N'dbo', @source_name = N'{_tableName}',
                         @role_name = NULL, @supports_net_changes = 1;
                    """);
                return;
            }
            catch (SqlException ex) when (IsDeadlock(ex) && attempt < 6)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt));

                if (await MsSqlCdcCatalog.FindCaptureInstanceAsync(
                        _connection, "dbo", _tableName, CancellationToken.None) is not null)
                    return;
            }
        }
    }

    private static bool IsDeadlock(SqlException ex) =>
        ex.Errors.Cast<SqlError>().Any(e => e.Number == 1205)
        || ex.Message.Contains("was deadlocked on lock resources", StringComparison.Ordinal);

    private async Task<byte[]> WaitForMaxLsnAsync()
    {
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(90))
        {
            if (await MsSqlCdcCatalog.GetMaxLsnAsync(_connection, CancellationToken.None) is { } max)
                return max;

            await Task.Delay(500);
        }

        throw new TimeoutException(
            "The CDC capture job never produced a maximum LSN. Is SQL Server Agent running? " +
            "(docker-compose.yml sets MSSQL_AGENT_ENABLED on mssql-source.)");
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
