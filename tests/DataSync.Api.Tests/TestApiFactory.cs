using ClrKernel.Core.Secrets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DataSync.Api.Tests;

/// <summary>
/// Points the API at a temp git repo / temp SQLite state file per test run, and swaps the real
/// SecretStore for an in-memory one — no test here should touch a real OS keychain. TaskRunnerDllPath
/// is left at its computed default, which correctly resolves to the real built
/// DataSync.TaskRunner.dll in this repo's dev layout — Integration-tagged tests that actually trigger
/// a run rely on that.
/// </summary>
public sealed class TestApiFactory : WebApplicationFactory<Program>
{
    public string RepoRoot { get; } = Directory.CreateTempSubdirectory("datasync-api-tests-").FullName;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataSync:RepoRoot"] = RepoRoot,
                ["DataSync:StateDbPath"] = Path.Combine(RepoRoot, "state.db"),
                ["DataSync:TaskRunnerDllPath"] = ResolveTaskRunnerDllPathForTests(),
                // An ephemeral port, so concurrent test classes and a dev instance on the default
                // port do not collide. Unlike the main app — which WebApplicationFactory replaces
                // with an in-memory server — StateHost is a real Kestrel server here, which is what
                // lets the real child processes these tests spawn actually reach it.
                ["DataSync:StatePort"] = "0",
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<SecretStore>();
            services.AddSingleton(SecretStore.ForProviders([new InMemorySecretProvider()]));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(RepoRoot))
            Directory.Delete(RepoRoot, recursive: true);
    }

    /// <summary>
    /// ApiOptions' own default resolution assumes DataSync.Api is the entry assembly (true when
    /// running it directly, not when it's loaded inside this test host — AppContext.BaseDirectory
    /// here is tests/DataSync.Api.Tests/bin/..., which never contains "DataSync.Api/bin"). Walks up
    /// from this test assembly's own output directory to the repo root instead, then reconstructs
    /// TaskRunner's build output path using this assembly's own Configuration/TFM segments (both
    /// projects are built together, so they match).
    /// </summary>
    private static string ResolveTaskRunnerDllPathForTests()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var tfm = Path.GetFileName(baseDir);
        var configuration = Path.GetFileName(Path.GetDirectoryName(baseDir))!;

        var repoRoot = new DirectoryInfo(baseDir);
        while (repoRoot is not null && !File.Exists(Path.Combine(repoRoot.FullName, "DataSync.slnx")))
            repoRoot = repoRoot.Parent;

        if (repoRoot is null)
            throw new InvalidOperationException($"Could not locate the repo root (DataSync.slnx) from '{baseDir}'.");

        return Path.Combine(repoRoot.FullName, "src", "DataSync.TaskRunner", "bin", configuration, tfm, "DataSync.TaskRunner.dll");
    }
}
