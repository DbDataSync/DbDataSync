using DbDataSync.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataSync.Api.Tests;

/// <summary>
/// A <see cref="TestApiFactory"/> whose <see cref="LibrarySearchService.HttpClientName"/> client is
/// wired to a <see cref="StubNuGetSearchHandler"/> instead of the real network — <see cref="Handler"/>
/// and <see cref="NuGetSearchEnabled"/> are both settable before the first request builds the host.
/// </summary>
public sealed class LibrarySearchApiFactory : TestApiFactory
{
    public StubNuGetSearchHandler Handler { get; } = new();
    public bool NuGetSearchEnabled { get; set; } = true;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DbDataSync:Nuget:Search:Mode"] = NuGetSearchEnabled ? "enabled" : "disabled",
            });
        });

        builder.ConfigureServices(services =>
        {
            services.AddSingleton(Handler);
            services.AddHttpClient(LibrarySearchService.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<StubNuGetSearchHandler>());
        });
    }
}
