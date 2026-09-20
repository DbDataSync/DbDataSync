using DbDataSync.Api.Configuration;
using DbDataSync.Api.Services;
using DbDataSync.Updates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// Phase 159's confirmation: an updated version proves itself by having served for a while, not by merely
/// starting. Until it does, the record of the update stays "on trial" — which is exactly what makes the next
/// start's apply step roll it back if this version falls over first.
/// </summary>
public sealed class UpdateConfirmationServiceTests : IDisposable
{
    private const string Previous = "2026.9.16.1005";
    private const string Updated = "2026.9.18.1918";

    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-confirm-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class TestLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void NotifyStarted() => _started.Cancel();

        public void StopApplication() => _stopping.Cancel();
    }

    private sealed class NoWork : IUpdateWorkProbe
    {
        public int RunningWorkCount() => 0;
    }

    private sealed class NoRestart : IUpdateRestart
    {
        public void StopForUpdate()
        {
        }
    }

    private UpdateStateStore Store() => new(new UpdateWorkspace(_root));

    /// <summary>What the privileged step leaves for the version it just installed: a phase of "restarting" for it.
    /// (Its own record of the update is in a directory the service cannot read.)</summary>
    private void OnTrial(string target = Updated) => Store().Record(UpdatePhase.Restarting, "Waiting.", Previous, target, "dan");

    private (UpdateConfirmationService Confirmation, TestLifetime Lifetime) Build(string? running = Updated, int confirmAfterMilliseconds = 30)
    {
        var options = ApiOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DbDataSync:RepoRoot"] = _root,
        }).Build());
        // The grace period, in milliseconds, so a test does not wait a minute.
        options = new ApiOptions
        {
            RepoRoot = options.RepoRoot,
            StateDbPath = options.StateDbPath,
            TaskRunnerDllPath = options.TaskRunnerDllPath,
            CliDllPath = options.CliDllPath,
            SelfUpdateConfirmAfter = TimeSpan.FromMilliseconds(confirmAfterMilliseconds),
        };

        var facts = UpdateServiceTests.Facts(version: running);
        var lifetime = new TestLifetime();
        var updates = new UpdateService(
            options, new NoHttp(), facts, new UpdateDrainState(), new NoWork(), new NoRestart(), lifetime, NullLogger<UpdateService>.Instance);
        return (new UpdateConfirmationService(updates, options, facts, lifetime, NullLogger<UpdateConfirmationService>.Instance), lifetime);
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("nothing here should reach the network");
    }

    private static async Task<bool> UntilAsync(Func<bool> condition, int milliseconds = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(10);
        }

        return condition();
    }

    [Fact]
    public async Task TheUpdatedVersion_ConfirmsItself_OnceItHasBeenServingForTheGracePeriod()
    {
        OnTrial();
        var (confirmation, lifetime) = Build();

        await confirmation.StartAsync(CancellationToken.None);
        lifetime.NotifyStarted();

        Assert.True(await UntilAsync(() => Store().ReadConfirmed() is not null), "the update was never confirmed");
        Assert.Equal(Updated, Store().ReadConfirmed()!.TargetVersion);
        Assert.Equal(UpdatePhase.Succeeded, Store().ReadState().Current!.Phase);
        await confirmation.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task NothingIsConfirmed_BeforeTheHostHasStarted()
    {
        OnTrial();
        var (confirmation, lifetime) = Build(confirmAfterMilliseconds: 10);

        await confirmation.StartAsync(CancellationToken.None);
        await Task.Delay(150);

        Assert.Null(Store().ReadConfirmed());

        lifetime.NotifyStarted();
        Assert.True(await UntilAsync(() => Store().ReadConfirmed() is not null));
        await confirmation.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AVersionThatStopsBeforeTheGracePeriodEnds_LeavesTheUpdateOnTrial()
    {
        OnTrial();
        var (confirmation, lifetime) = Build(confirmAfterMilliseconds: 60_000);

        await confirmation.StartAsync(CancellationToken.None);
        lifetime.NotifyStarted();
        await Task.Delay(50);
        await confirmation.StopAsync(CancellationToken.None);

        Assert.Null(Store().ReadConfirmed());
        Assert.Equal(UpdatePhase.Restarting, Store().ReadState().Current!.Phase);
    }

    [Fact]
    public async Task AVersionThatIsNotTheOneUpdatedTo_ConfirmsNothing()
    {
        // The old version came back up (for instance after a rollback that has not yet cleared the record).
        OnTrial();
        var (confirmation, lifetime) = Build(running: Previous, confirmAfterMilliseconds: 10);

        await confirmation.StartAsync(CancellationToken.None);
        lifetime.NotifyStarted();
        await Task.Delay(150);
        await confirmation.StopAsync(CancellationToken.None);

        Assert.Null(Store().ReadConfirmed());
        Assert.Equal(UpdatePhase.Restarting, Store().ReadState().Current!.Phase);
    }

    [Fact]
    public async Task AVersionThatDoesNotKnowItsOwnVersion_ConfirmsNothing()
    {
        OnTrial();
        var (confirmation, lifetime) = Build(running: null, confirmAfterMilliseconds: 10);

        await confirmation.StartAsync(CancellationToken.None);
        lifetime.NotifyStarted();
        await Task.Delay(150);
        await confirmation.StopAsync(CancellationToken.None);

        Assert.Null(Store().ReadConfirmed());
        Assert.Equal(UpdatePhase.Restarting, Store().ReadState().Current!.Phase);
    }

    [Fact]
    public async Task WithNothingApplied_ItStillClearsWhatEarlierStartsLeftBehind()
    {
        Store().WritePending(new PendingUpdate(Updated, DateTimeOffset.UtcNow, "dan"));
        var (confirmation, _) = Build();

        await confirmation.StartAsync(CancellationToken.None);

        Assert.True(await UntilAsync(() => Store().ReadPending() is null));
        Assert.Equal(UpdatePhase.Failed, Store().ReadState().Current!.Phase);
        Assert.Contains("not applied", Store().ReadState().Current!.Message);
        await confirmation.StopAsync(CancellationToken.None);
    }
}
