using ClrKernel.Core.Secrets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DbDataSync.Api.Tests;

/// <summary>
/// Points the API at a temp git repo / temp SQLite state file per test run, and swaps the real
/// SecretStore for an in-memory one — no test here should touch a real OS keychain. TaskRunnerDllPath
/// is left at its computed default, which correctly resolves to the real built
/// DbDataSync.TaskRunner.dll in this repo's dev layout — Integration-tagged tests that actually trigger
/// a run rely on that.
/// </summary>
public sealed class TestApiFactory : WebApplicationFactory<Program>
{
    public string RepoRoot { get; } = Directory.CreateTempSubdirectory("dbdatasync-api-tests-").FullName;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DbDataSync:RepoRoot"] = RepoRoot,
                ["DbDataSync:StateDbPath"] = Path.Combine(RepoRoot, "state.db"),
                ["DbDataSync:TaskRunnerDllPath"] = ResolveTaskRunnerDllPathForTests(),
                // Authentication off, deliberately. Every test using this factory is about what an
                // endpoint *does*; making all of them sign in first would obscure that and test the
                // same session plumbing a hundred times. Who may call what is
                // AuthenticatedApiFactory's subject, and it turns authentication on.
                ["DbDataSync:Auth:Disabled"] = "true",
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<SecretStore>();
            // Prefixed "DbDataSync" to match the real composition root (DbDataSyncHost.cs) — tests that
            // set an env var for a connection's credential (SecretStore.EnvName) need this store to name
            // things exactly as production does, not under SecretPrefix.Default's "ClrKernel".
            services.AddSingleton(SecretStore.ForProviders("DbDataSync", [new InMemorySecretProvider()]));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(RepoRoot))
            Directory.Delete(RepoRoot, recursive: true);
    }

    /// <summary>
    /// ApiOptions' own default resolution assumes DbDataSync.Api is the entry assembly (true when
    /// running it directly, not when it's loaded inside this test host — AppContext.BaseDirectory
    /// here is tests/DbDataSync.Api.Tests/bin/..., which never contains "DbDataSync.Api/bin"). Walks up
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
        while (repoRoot is not null && !File.Exists(Path.Combine(repoRoot.FullName, "DbDataSync.slnx")))
            repoRoot = repoRoot.Parent;

        if (repoRoot is null)
            throw new InvalidOperationException($"Could not locate the repo root (DbDataSync.slnx) from '{baseDir}'.");

        return Path.Combine(repoRoot.FullName, "src", "DbDataSync.TaskRunner", "bin", configuration, tfm, "DbDataSync.TaskRunner.dll");
    }
}
