using DbDataSync.Api.Configuration;
using DbDataSync.Api.Services;
using DbDataSync.State;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// The same host as <see cref="TestApiFactory"/> with every retention cap explicitly set to 0, which
/// <see cref="ApiOptions"/> reads as "no cap" and which makes <see cref="RunPruningService"/>'s
/// background loop log that pruning is off and return without ever sweeping.
/// <para>
/// Phase 140, found as an intermittent failure of
/// <see cref="ChangeCheckPruningTests.WithNoChangeCheckWindow_TheHistoryIsLeftAlone"/> in a full-suite
/// run that passed in isolation. <see cref="RunPruningService"/> sweeps **immediately** on startup and
/// only then settles onto its hourly timer — deliberately, so an API that was down for a week does not
/// hold expired rows for another hour. Under a full suite that startup sweep can land *after* a test
/// has written its own deliberately-ancient row, and the default seven-day change-check window deletes
/// the very row the test is asserting survives. Nothing about that is Windows-specific; the extra load
/// is simply what made the window wide enough to hit.
/// </para>
/// <para>
/// Turning the background loop off rather than making the test tolerate it: these tests call
/// <see cref="RunPruningService.PruneAsync"/> directly and are about what one sweep does with the
/// arguments they pass it. A second, concurrent sweeper using different arguments is not a condition
/// they are meant to survive — it is the one thing that can make them lie.
/// </para>
/// </summary>
public sealed class UnprunedApiFactory : TestApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DbDataSync:RunRetentionDays"] = "0",
                ["DbDataSync:RunRetentionMaxPerMapping"] = "0",
                ["DbDataSync:ChangeCheckRetentionDays"] = "0",
            });
        });
    }
}


/// <summary>
/// That the polling gate's history is swept by the service that already sweeps run history, on the
/// same tick — see phase 75. This is the claim that justifies the table having no background service
/// of its own, and it is the one that would quietly stop being true if the second delete were
/// dropped from <see cref="RunPruningService.PruneAsync"/>.
/// </summary>
public sealed class ChangeCheckPruningTests(UnprunedApiFactory factory) : IClassFixture<UnprunedApiFactory>
{
    private readonly StateDatabase _database = factory.Services.GetRequiredService<StateDatabase>();
    private readonly ChangeCheckStore _checks = factory.Services.GetRequiredService<ChangeCheckStore>();

    private RunPruningService BuildService() => new(
        factory.Services.GetRequiredService<TaskRunStore>(),
        _checks,
        factory.Services.GetRequiredService<NotificationStore>(),
        factory.Services.GetRequiredService<ApiOptions>(),
        NullLogger<RunPruningService>.Instance);

    private void Backdate(string connectionName, TimeSpan age)
    {
        using var connection = _database.OpenConnection();
        using var cmd = _database.Command(connection,
            "UPDATE ChangeCheckHistory SET CheckedAtUtc = $checked WHERE ConnectionName = $connection;");
        cmd.Bind(_database, "checked", (DateTimeOffset.UtcNow - age).ToString("O"));
        cmd.Bind(_database, "connection", connectionName);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task TheExistingSweep_AlsoAgesOutChangeChecks()
    {
        var stale = $"stale-{Guid.NewGuid():N}";
        var fresh = $"fresh-{Guid.NewGuid():N}";

        _checks.Record(stale, "App", "MsSqlChangeTracking", "1");
        Backdate(stale, TimeSpan.FromDays(30));
        _checks.Record(fresh, "App", "MsSqlChangeTracking", "2");

        await BuildService().PruneAsync(
            maxAge: TimeSpan.FromDays(90), maxPerMapping: null, checkMaxAge: TimeSpan.FromDays(7));

        var remaining = _checks.ListChecks().Select(c => c.ConnectionName).ToList();
        Assert.DoesNotContain(stale, remaining);
        Assert.Contains(fresh, remaining);
    }

    [Fact]
    public async Task WithNoChangeCheckWindow_TheHistoryIsLeftAlone()
    {
        var kept = $"kept-{Guid.NewGuid():N}";
        _checks.Record(kept, "App", "MsSqlCdc", "0000002A000000AB0003");
        Backdate(kept, TimeSpan.FromDays(3650));

        // Run retention still applies on the same call — the two windows are independent, which is
        // the point of the second knob.
        await BuildService().PruneAsync(
            maxAge: TimeSpan.FromDays(1), maxPerMapping: 1, checkMaxAge: null);

        Assert.Contains(kept, _checks.ListChecks().Select(c => c.ConnectionName));
    }
}
