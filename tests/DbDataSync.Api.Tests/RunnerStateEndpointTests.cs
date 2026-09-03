using System.Net;
using System.Net.Http.Json;
using DbDataSync.Api.State;
using DbDataSync.State;
using DbDataSync.State.Remote;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataSync.Api.Tests;

/// <summary>
/// The state endpoint, against the real Kestrel server the API starts for it. Unlike the main app —
/// which WebApplicationFactory replaces with an in-memory server — StateHost is genuinely listening
/// here, which is the only way to test the things that are properties of a socket.
/// </summary>
public sealed class RunnerStateEndpointTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;
    private readonly StateHost _host;
    private readonly RunnerToken _token;

    public RunnerStateEndpointTests(TestApiFactory factory)
    {
        _factory = factory;
        _ = factory.CreateClient(); // forces the host — and so StateHost — to start
        _host = factory.Services.GetRequiredService<StateHost>();
        _token = factory.Services.GetRequiredService<RunnerToken>();
    }

    private HttpClient Client(string? token)
    {
        var client = new HttpClient { BaseAddress = new Uri(_host.BaseAddress) };
        if (token is not null)
            client.DefaultRequestHeaders.Add(StateProtocol.TokenHeader, token);
        return client;
    }

    [Fact]
    public void The_state_endpoint_is_bound_to_loopback_only()
    {
        // The first of the two defences, and the one that does not depend on any application code
        // being correct: the OS refuses a connection from anywhere else.
        Assert.StartsWith("http://127.0.0.1:", _host.BaseAddress);
    }

    [Fact]
    public async Task A_request_carrying_the_token_is_served()
    {
        using var client = Client(_token.Value);

        var response = await client.GetAsync(
            $"{StateProtocol.Route}/has-outstanding-work?taskName=nothing-here");

        response.EnsureSuccessStatusCode();
        Assert.False((await response.Content.ReadFromJsonAsync<BoolResponse>())!.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-the-token")]
    public async Task A_request_without_the_right_token_is_refused(string? token)
    {
        // Loopback is not a trust boundary: any process belonging to any local user can reach
        // 127.0.0.1, so the token is what distinguishes this API's own children from everything else
        // on the host.
        using var client = Client(token);

        var response = await client.GetAsync(
            $"{StateProtocol.Route}/has-outstanding-work?taskName=nothing-here");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The second defence, tested independently of the first: what survives someone widening the
    /// binding, or a refactor that merges this back onto the main server.
    /// </summary>
    [Fact]
    public async Task The_guard_refuses_a_non_loopback_client_even_if_the_binding_is_widened()
    {
        var reachedTheEndpoint = false;
        var guard = new RunnerStateGuard(_ => { reachedTheEndpoint = true; return Task.CompletedTask; }, _token);

        var context = new DefaultHttpContext();
        context.Request.Path = $"{StateProtocol.Route}/mark-done";
        context.Request.Headers[StateProtocol.TokenHeader] = _token.Value; // a valid token is not enough
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.1.2.3");

        await guard.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.False(reachedTheEndpoint);
    }

    /// <summary>
    /// The whole <see cref="IRunnerState"/> surface over a real socket, checked against what the owner
    /// actually recorded. This is the test that says a runner loses nothing by not opening the file.
    /// </summary>
    [Fact]
    public void The_remote_implementation_does_the_same_thing_as_the_local_one()
    {
        var taskName = $"remote-state-{Guid.NewGuid():N}";
        var owner = _factory.Services.GetRequiredService<LocalRunnerState>();
        var workQueue = _factory.Services.GetRequiredService<WorkQueueStore>();
        var logs = _factory.Services.GetRequiredService<LogWriter>();

        owner.UpsertTask(taskName, enabled: true);
        var runId = workQueue.Enqueue(taskName, RunKind.Primary, "Orders");

        using var remote = new RemoteRunnerState(
            Client(_token.Value), new StateJournal(Path.Combine(_factory.RepoRoot, "unused.jsonl")),
            TimeSpan.Zero, _ => { });

        Assert.True(remote.HasOutstandingWork(taskName));

        var claimed = remote.TryClaimNext(taskName, "worker-1");
        Assert.NotNull(claimed);
        Assert.Equal("Orders", claimed.MappingName);

        Assert.True(remote.TryAcquireLock(taskName, RunKind.Primary, "Orders", claimed.RunId));
        remote.BeginRun(claimed.RunId, pid: 4242);
        remote.MarkRunning(claimed.Id);
        remote.Log(claimed.RunId, LogSeverity.Info, "hello from a runner");
        remote.SetWatermark(taskName, "orders", "dbo.Orders", "1234");
        remote.MarkDone(claimed.Id);
        remote.CompleteRun(claimed.RunId, RunStatus.Succeeded, 7, 7, null);
        remote.ReleaseLock(taskName, RunKind.Primary, "Orders");
        remote.Flush();

        // Read back through the owner, which is the only thing that ever touched the file.
        Assert.Equal("1234", owner.GetWatermark(taskName, "orders", "dbo.Orders"));
        Assert.False(remote.HasOutstandingWork(taskName));

        var run = _factory.Services.GetRequiredService<TaskRunStore>().GetRun(claimed.RunId);
        Assert.Equal(RunStatus.Succeeded, run!.Status);
        Assert.Equal(7, run.RowsRead);
        Assert.Contains(logs.GetLogs(claimed.RunId), l => l.Message == "hello from a runner");

        // The lock was released, so the same mapping can be claimed again.
        Assert.True(owner.TryAcquireLock(taskName, RunKind.Primary, "Orders", Guid.NewGuid()));
        Assert.Equal(runId, claimed.RunId);
    }
}
