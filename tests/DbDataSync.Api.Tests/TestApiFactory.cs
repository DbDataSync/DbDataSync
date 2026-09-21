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
public class TestApiFactory : WebApplicationFactory<Program>
{
    private readonly bool _ownsRepoRoot;

    public string RepoRoot { get; }

    public TestApiFactory() : this(null) { }

    /// <summary>
    /// <paramref name="existingRepoRoot"/> is for a test standing up a *second* host over a repo root
    /// another factory already created and still owns — simulating a restart's "read whatever is on
    /// disk right now" without this instance deleting a directory it doesn't own when it disposes.
    /// </summary>
    protected TestApiFactory(string? existingRepoRoot)
    {
        _ownsRepoRoot = existingRepoRoot is null;
        RepoRoot = existingRepoRoot ?? Directory.CreateTempSubdirectory("dbdatasync-api-tests-").FullName;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DbDataSync:App:RepoRoot"] = RepoRoot,
                ["DbDataSync:State:DbPath"] = Path.Combine(RepoRoot, "state.db"),
                ["DbDataSync:App:TaskRunnerDllPath"] = ResolveTaskRunnerDllPathForTests(),
                ["DbDataSync:App:CliDllPath"] = ResolveSiblingDllPathForTests("DbDataSync.Cli"),
                // Authentication off, deliberately. Every test using this factory is about what an
                // endpoint *does*; making all of them sign in first would obscure that and test the
                // same session plumbing a hundred times. Who may call what is
                // AuthenticatedApiFactory's subject, and it turns authentication on. TestServer's
                // requests report loopback (or no) remote address, so Auth:Network:Admin: loopback is
                // the phase 164 equivalent of the old blanket Auth:Disabled for this factory's purpose.
                ["DbDataSync:Auth:Network:Admin"] = "loopback",
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<SecretStore>();
            // Prefixed "DbDataSync" to match the real composition root (DbDataSyncHost.cs) — tests that
            // set an env var for a connection's credential (SecretStore.EnvName) need this store to name
            // things exactly as production does, not under SecretPrefix.Default's "ClrKernel".
            services.AddSingleton(SecretStore.ForProviders("DbDataSync", [new InMemorySecretProvider()]));
            // Without this, every request through this host answers 500 on Windows — see
            // TestServerConnectionItemsFilter for why that is nothing to do with auth being disabled here.
            services.AddTestServerConnectionItems();
            ConfigureTestServices(services);
        });
    }

    /// <summary>
    /// For a subclass that needs one more service swapped — a fake catalog in place of the one that
    /// opens real connections, say. Everything above stays shared rather than being copied per
    /// factory, since a second copy of the repo-root/state-db/runner-path wiring is a second thing to
    /// keep in step with the composition root.
    /// </summary>
    protected virtual void ConfigureTestServices(IServiceCollection services) { }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && _ownsRepoRoot)
            GitTempDirectory.DeleteRecursively(RepoRoot);
    }

    /// <summary>
    /// ApiOptions' own default resolution assumes DbDataSync.Api is the entry assembly (true when
    /// running it directly, not when it's loaded inside this test host — AppContext.BaseDirectory
    /// here is tests/DbDataSync.Api.Tests/bin/..., which never contains "DbDataSync.Api/bin"). Walks up
    /// from this test assembly's own output directory to the repo root instead, then reconstructs
    /// TaskRunner's build output path using this assembly's own Configuration/TFM segments (both
    /// projects are built together, so they match).
    /// </summary>
    private static string ResolveTaskRunnerDllPathForTests() => ResolveSiblingDllPathForTests("DbDataSync.TaskRunner");

    /// <summary>Same trick as <see cref="ResolveTaskRunnerDllPathForTests"/>, generalized: phase 109j's
    /// <c>LibraryValidationLauncher</c> needs to find <c>DbDataSync.Cli.dll</c>'s own build output the
    /// same way, and both are "some other project's own separately-built output, found by walking up to
    /// the repo root and back down" — the same shape, different project name.</summary>
    private static string ResolveSiblingDllPathForTests(string projectName)
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var tfm = Path.GetFileName(baseDir);
        var configuration = Path.GetFileName(Path.GetDirectoryName(baseDir))!;

        var repoRoot = new DirectoryInfo(baseDir);
        while (repoRoot is not null && !File.Exists(Path.Combine(repoRoot.FullName, "DbDataSync.slnx")))
            repoRoot = repoRoot.Parent;

        if (repoRoot is null)
            throw new InvalidOperationException($"Could not locate the repo root (DbDataSync.slnx) from '{baseDir}'.");

        return Path.Combine(repoRoot.FullName, "src", projectName, "bin", configuration, tfm, $"{projectName}.dll");
    }
}

/// <summary>A second host over a repo root an existing factory (of any kind — this doesn't care which)
/// already owns — for a test simulating "restart the API and see what it reads off disk now" without
/// standing up a real process restart.</summary>
public sealed class TestApiFactoryOnRepo(string existingRepoRoot) : TestApiFactory(existingRepoRoot);
