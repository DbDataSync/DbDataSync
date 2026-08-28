using System.Net;
using System.Text.Json.Serialization;
using DataSync.Api.Configuration;
using DataSync.State;
using DataSync.State.Remote;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace DataSync.Api.State;

/// <summary>
/// A second, separate HTTP server inside this process, serving only <see cref="RunnerStateEndpoints"/>
/// to this process's own children.
/// <para>
/// **Its own server, not another endpoint on the main one.** The main API is the thing an operator
/// puts behind a reverse proxy, binds to 0.0.0.0, or exposes through a container port map; anything
/// sharing its pipeline inherits every one of those decisions. This server is constructed here, listens
/// only on loopback, and has exactly the routes listed in <see cref="RunnerStateEndpoints"/> — so
/// "what can be reached from off-box" has an answer that does not depend on the main API's
/// configuration.
/// </para>
/// <para>
/// It shares the owning process's <see cref="LocalRunnerState"/>, which is the whole point: one process
/// writes the state file, and this is how the runners it spawns reach it.
/// </para>
/// </summary>
public sealed class StateHost(
    LocalRunnerState state, RunnerToken token, ApiOptions options, ILoggerFactory loggerFactory)
    : IHostedService, IAsyncDisposable
{
    private WebApplication? _app;
    private string? _baseAddress;

    /// <summary>Where children should send state operations. Read after start, because a configured
    /// port of 0 — which is how tests avoid colliding with each other and with a dev instance — is only
    /// resolved to a real one by binding it.</summary>
    public string BaseAddress => _baseAddress
        ?? throw new InvalidOperationException("The state endpoint has not started listening yet.");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();

        // Nothing about this server should come from ambient configuration: ASPNETCORE_URLS or an
        // appsettings.json in the working directory would otherwise add bindings to a server whose
        // entire security model is which interface it is bound to.
        builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, string.Empty);
        // IPv4 loopback specifically, rather than ListenLocalhost: the latter binds both loopback
        // families and refuses a port of 0 outright, and one address is what BaseAddress has to be
        // able to report anyway.
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, options.StatePort));

        builder.Services.AddSingleton(loggerFactory);
        builder.Services.ConfigureHttpJsonOptions(json =>
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        _app = builder.Build();
        _app.UseMiddleware<RunnerStateGuard>(token);
        RunnerStateEndpoints.Map(_app, state);

        await _app.StartAsync(cancellationToken);

        _baseAddress = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();

        loggerFactory.CreateLogger<StateHost>().LogInformation(
            "Runner state endpoint listening on {Address} (loopback only).", _baseAddress);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is not null)
            await _app.StopAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
        _app = null;
    }
}
