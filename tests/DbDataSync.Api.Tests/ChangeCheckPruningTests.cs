using DbDataSync.Api.Configuration;
using DbDataSync.Api.Services;
using DbDataSync.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// That the polling gate's history is swept by the service that already sweeps run history, on the
/// same tick — see phase 75. This is the claim that justifies the table having no background service
/// of its own, and it is the one that would quietly stop being true if the second delete were
/// dropped from <see cref="RunPruningService.PruneAsync"/>.
/// </summary>
public sealed class ChangeCheckPruningTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
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
