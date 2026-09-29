using DbDataSync.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DbDataSync.Api.Tests;

/// <summary>
/// The authenticated API (<see cref="AuthenticatedApiFactory"/>) with the Updates screen's release lookup switched
/// on and everything that touches the outside world replaced: what this process is and how it was started, and the
/// network. Nothing touches nuget.org or GitHub.
/// </summary>
public sealed class UpdateApiFactory : AuthenticatedApiFactory
{
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
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<UpdateHostFacts>();
            services.AddSingleton(HostFacts);
            services.ConfigureHttpClientDefaults(client => client.ConfigurePrimaryHttpMessageHandler(() => Network));
        });
    }
}
