using DbDataSync.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DbDataSync.Api.Tests;

/// <summary>
/// The authenticated API (<see cref="AuthenticatedApiFactory"/>) with phase 159's update feature switched on
/// and everything that touches the outside world replaced: what this process is and how it was started, the
/// worker probe, the restart, and the network. Asking it to update never restarts the test host and never
/// touches nuget.org or GitHub.
/// </summary>
public sealed class UpdateApiFactory : AuthenticatedApiFactory
{
    internal UpdateServiceTests.FakeProbe Probe { get; } = new();
    internal UpdateServiceTests.FakeRestart Restart { get; } = new();

    public bool Enabled { get; set; } = true;
    public string Channels { get; set; } = "stable,beta,snapshot";
    public UpdateHostFacts HostFacts { get; set; } = UpdateServiceTests.Facts();
    internal UpdateServiceTests.FakeNetwork Network { get; set; } = UpdateServiceTests.Network();

    public UpdateService Updates => Services.GetRequiredService<UpdateService>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DbDataSync:Updates:Mode"] = Enabled ? "manual" : "disabled",
                ["DbDataSync:Updates:Channels"] = Channels,
                ["DbDataSync:Updates:DrainTimeoutSeconds"] = "5",
                // Long enough that the confirmation service never fires inside a test.
                ["DbDataSync:Updates:ConfirmAfterSeconds"] = "3600",
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<UpdateHostFacts>();
            services.AddSingleton(HostFacts);
            services.RemoveAll<IUpdateWorkProbe>();
            services.AddSingleton<IUpdateWorkProbe>(Probe);
            services.RemoveAll<IUpdateRestart>();
            services.AddSingleton<IUpdateRestart>(Restart);
            services.ConfigureHttpClientDefaults(client => client.ConfigurePrimaryHttpMessageHandler(() => Network));
        });
    }
}
