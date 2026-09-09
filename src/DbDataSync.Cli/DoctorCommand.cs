using System.Text.Json;
using ClrKernel.Core.Secrets;
using DbDataSync.Api.Auth;
using DbDataSync.Api.Configuration;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;
using DbDataSync.Core.Secrets;
using DbDataSync.Drivers.Descriptor;
using DbDataSync.Providers;
using DbDataSync.State;
using Microsoft.Extensions.Configuration;
using LibGit2Sharp;

namespace DbDataSync.Cli;

public enum CheckStatus { Ok, Warn, Fail }

/// <param name="Fix">A command or a one-line instruction that would resolve <see cref="Status"/>, or
/// null for <see cref="CheckStatus.Ok"/> (nothing to fix) or where there is no single fix to name.</param>
public sealed record CheckResult(string Name, CheckStatus Status, string Detail, string? Fix = null);

/// <summary>What every check is handed — resolved once per <c>doctor</c> run (or once per
/// <c>setup</c> review screen render) rather than each check rebuilding its own configuration chain.</summary>
public sealed class DoctorContext
{
    public required string Root { get; init; }
    public required IConfiguration Configuration { get; init; }
    public required ApiOptions ApiOptions { get; init; }
    public required AuthOptions AuthOptions { get; init; }
    public required PasskeyOptions PasskeyOptions { get; init; }
    public required CertificateOptions CertificateOptions { get; init; }

    /// <summary>Opens the configured state database, or null with the exception that stopped it — a
    /// check that needs the store (state store itself, first admin) calls this rather than each
    /// building its own connection string / secret-store plumbing.</summary>
    public (StateDatabase? Database, Exception? Error) TryOpenStateDatabase()
    {
        try
        {
            var secrets = new SecretStore("DbDataSync", true);
            var database = StateDatabase.FromOptions(
                ApiOptions.StateEngine, ApiOptions.StateDbPath, ApiOptions.StateConnectionString, secrets);
            return (database, null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException
            or System.Data.Common.DbException)
        {
            return (null, ex);
        }
    }
}

public interface IReadinessCheck
{
    Task<CheckResult> RunAsync(DoctorContext context, CancellationToken cancellationToken);
}

/// <summary>
/// The non-interactive core of <c>setup</c>'s review screen: the same checks, one report, exit 0 when
/// nothing is <see cref="CheckStatus.Fail"/>, 1 otherwise — for a CI smoke test or a service wrapper
/// that cannot answer an interactive prompt.
/// </summary>
public static class DoctorCommand
{
    /// <summary>Every check, in report order — <c>internal</c> so <c>SetupCommand</c>'s review screen
    /// runs the identical list rather than a second one that could drift from this file's own.</summary>
    internal static readonly IReadOnlyList<IReadinessCheck> Checks =
    [
        new RepoCheck(),
        new StateStoreCheck(),
        new ProvidersAndDriversCheck(),
        new AuthCheck(),
        new BindingCheck(),
        new FirstAdminCheck(),
    ];

    public static async Task<int> RunAsync(string[] args)
    {
        var asJson = CliOptions.Has(args, "--json");
        var context = BuildContext(args);
        var results = await RunChecksAsync(context);

        if (asJson)
        {
            Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            foreach (var result in results)
            foreach (var line in FormatResult(result))
                Console.WriteLine(line);
        }

        return results.Any(r => r.Status == CheckStatus.Fail) ? 1 : 0;
    }

    internal static async Task<IReadOnlyList<CheckResult>> RunChecksAsync(DoctorContext context)
    {
        var results = new List<CheckResult>();
        foreach (var check in Checks)
            results.Add(await check.RunAsync(context, CancellationToken.None));
        return results;
    }

    /// <summary>One or two lines of human-readable text for <paramref name="result"/> — a list, not a
    /// single string, so a caller writing through <see cref="IPromptIo"/> (<c>SetupCommand</c>'s review
    /// screen) emits each line the same way this command's own <see cref="Console"/> output does.</summary>
    internal static IReadOnlyList<string> FormatResult(CheckResult result)
    {
        var marker = result.Status switch
        {
            CheckStatus.Ok => "✓",
            CheckStatus.Warn => "!",
            CheckStatus.Fail => "✗",
            _ => "?",
        };

        List<string> lines = [$"{marker} {result.Name}: {result.Detail}"];
        if (result.Fix is not null)
            lines.Add($"    fix: {result.Fix}");
        return lines;
    }

    /// <summary>
    /// Builds the same <see cref="IConfiguration"/> chain <c>DbDataSyncHost.InsertConfigFile</c>
    /// builds — config file, then environment variables, then the command line — but as a plain
    /// <see cref="ConfigurationBuilder"/> rather than through <c>DbDataSyncHost.Build</c>/Kestrel. A
    /// bare builder adds sources in the order given and later ones win, so this needs none of that
    /// method's "find the environment source and insert before it" trick — that trick exists only
    /// because <c>WebApplicationBuilder.CreateBuilder</c> pre-populates sources in a fixed order before
    /// customisation is possible.
    /// </summary>
    internal static DoctorContext BuildContext(string[] args)
    {
        var root = DbDataSyncRoot.Resolve(args);

        var builder = new ConfigurationBuilder();
        if (File.Exists(DbDataSyncConfigFile.PathIn(root)))
            builder.Add(new DbDataSyncConfigFileSource { InitialData = DbDataSyncConfigFile.Read(root) });
        builder.AddEnvironmentVariables();
        builder.AddCommandLine(args.Where(a => a.StartsWith("--DbDataSync:", StringComparison.Ordinal)).ToArray());

        var configuration = builder.Build();
        return new DoctorContext
        {
            Root = root,
            Configuration = configuration,
            ApiOptions = ApiOptions.FromConfiguration(configuration),
            AuthOptions = AuthOptions.FromConfiguration(configuration),
            PasskeyOptions = PasskeyOptions.FromConfiguration(configuration),
            CertificateOptions = CertificateOptions.FromConfiguration(configuration),
        };
    }
}

/// <summary>Resolvable, a valid git repository, and <c>dbdatasync.config.yaml</c> parses — the three
/// things every other check assumes already hold.</summary>
public sealed class RepoCheck : IReadinessCheck
{
    public Task<CheckResult> RunAsync(DoctorContext context, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(context.Root))
        {
            return Task.FromResult(new CheckResult(
                "Repo", CheckStatus.Fail, $"'{context.Root}' does not exist.",
                "Run `dbdatasync serve` once, or `dbdatasync setup`, to create it."));
        }

        if (!Repository.IsValid(context.Root))
        {
            return Task.FromResult(new CheckResult(
                "Repo", CheckStatus.Fail, $"'{context.Root}' is not a git repository.",
                "Run `dbdatasync serve` once to initialise it."));
        }

        try
        {
            DbDataSyncConfigFile.Read(context.Root);
        }
        catch (Exception ex) when (ex is IOException or YamlDotNet.Core.YamlException)
        {
            return Task.FromResult(new CheckResult(
                "Repo", CheckStatus.Fail, $"'{DbDataSyncConfigFile.FileName}' does not parse: {ex.Message}", null));
        }

        return Task.FromResult(new CheckResult("Repo", CheckStatus.Ok, context.Root));
    }
}

/// <summary>SQLite's directory is writable, or a server engine's connection actually opens and its
/// schema is current — <see cref="StateDatabase"/>'s own constructor runs the full migration set, so
/// successfully opening one already proves "current or migrated".</summary>
public sealed class StateStoreCheck : IReadinessCheck
{
    public Task<CheckResult> RunAsync(DoctorContext context, CancellationToken cancellationToken)
    {
        var (database, error) = context.TryOpenStateDatabase();
        if (database is not null)
        {
            return Task.FromResult(new CheckResult(
                "State store", CheckStatus.Ok, $"{context.ApiOptions.StateEngine} — schema current."));
        }

        return Task.FromResult(new CheckResult(
            "State store", CheckStatus.Fail, error?.Message ?? "Could not open the state database.",
            context.ApiOptions.StateEngine == StateEngineIds.Sqlite
                ? $"Check that '{Path.GetDirectoryName(context.ApiOptions.StateDbPath)}' is writable."
                : "Check DbDataSync:StateConnectionString and the stored password " +
                  "(`dbdatasync secret set dbdatasync:config:stateConnectionString ...`)."));
    }
}

/// <summary>Every restored provider's <c>lib/</c> closure is present, every driver manifest on disk
/// parses, and every connection's driver id resolves to a built-in or a driver found on disk.</summary>
public sealed class ProvidersAndDriversCheck : IReadinessCheck
{
    private static readonly HashSet<string> BuiltInDriverIds = [DriverIds.MsSql, DriverIds.Postgres, DriverIds.DuckDb];

    public Task<CheckResult> RunAsync(DoctorContext context, CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        var driverIds = new HashSet<string>(BuiltInDriverIds, StringComparer.Ordinal);

        var providersRoot = ProviderPaths.ProvidersDir(context.Root);
        if (Directory.Exists(providersRoot))
        {
            foreach (var dir in Directory.EnumerateDirectories(providersRoot))
            {
                var manifestPath = ProviderPaths.ManifestPath(dir);
                if (!File.Exists(manifestPath))
                    continue;

                var libDir = ProviderPaths.LibDir(dir);
                if (!Directory.Exists(libDir) || !Directory.EnumerateFileSystemEntries(libDir).Any())
                    problems.Add($"provider '{Path.GetFileName(dir)}' has no restored lib/ — run `dbdatasync provider sync`.");
            }
        }

        var driversRoot = Path.Combine(context.Root, "drivers");
        if (Directory.Exists(driversRoot))
        {
            foreach (var dir in Directory.EnumerateDirectories(driversRoot))
            {
                var yamlPath = Path.Combine(dir, DriverLoader.DescriptorFileName);
                var jsonPath = Path.Combine(dir, CompiledDriverManifest.FileName);
                try
                {
                    if (File.Exists(yamlPath))
                        driverIds.Add(DriverDescriptorReader.Read(yamlPath).Id);
                    else if (File.Exists(jsonPath))
                        driverIds.Add(CompiledDriverManifest.Read(jsonPath).Id);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or YamlDotNet.Core.YamlException
                    or JsonException)
                {
                    problems.Add($"driver '{Path.GetFileName(dir)}' does not parse: {ex.Message}");
                }
            }
        }

        try
        {
            var configRoot = Path.Combine(context.Root, "config");
            var repository = new ConfigRepository(
                configRoot, new GitCommitService(context.Root), new SecretStore("DbDataSync", true));
            foreach (var name in repository.ListConnections())
            {
                var connection = repository.LoadConnection(name);
                if (!driverIds.Contains(connection.DriverType))
                {
                    problems.Add(
                        $"connection '{name}' names driver '{connection.DriverType}', which is not installed — " +
                        $"run `dbdatasync driver install {connection.DriverType} ...`.");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"could not read connections: {ex.Message}");
        }

        return Task.FromResult(problems.Count == 0
            ? new CheckResult("Providers / drivers", CheckStatus.Ok, "Every connection's driver resolves.")
            : new CheckResult("Providers / drivers", CheckStatus.Fail, string.Join(" ", problems)));
    }
}

/// <summary>A method is configured or authentication is explicitly disabled; the passkey
/// configuration (if any) is internally consistent.</summary>
public sealed class AuthCheck : IReadinessCheck
{
    public Task<CheckResult> RunAsync(DoctorContext context, CancellationToken cancellationToken)
    {
        if (context.AuthOptions.Disabled)
        {
            return Task.FromResult(new CheckResult(
                "Auth", CheckStatus.Warn, "DbDataSync:Auth:Disabled is true — every request is accepted."));
        }

        var problem = context.PasskeyOptions.Problem();
        if (problem is not null)
            return Task.FromResult(new CheckResult("Auth", CheckStatus.Fail, problem));

        return Task.FromResult(new CheckResult(
            "Auth", CheckStatus.Ok,
            context.AuthOptions.WindowsEnabled
                ? $"Windows groups configured (admin: {context.AuthOptions.AdminGroup ?? "none"})."
                : $"Passkeys — relying party '{context.PasskeyOptions.RelyingPartyId}'."));
    }
}

/// <summary>The console URL actually answers. Warns rather than fails on plain HTTP off loopback —
/// passkeys need HTTPS there, but the console can still be reachable.</summary>
public sealed class BindingCheck : IReadinessCheck
{
    public async Task<CheckResult> RunAsync(DoctorContext context, CancellationToken cancellationToken)
    {
        var url = context.Configuration["DbDataSync:Url"] ?? "http://localhost:5080";
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            var response = await client.GetAsync($"{url.TrimEnd('/')}/api/health", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new CheckResult(
                    "Binding", CheckStatus.Fail, $"{url} answered {(int)response.StatusCode}.",
                    "Start DbDataSync (`dbdatasync serve`) and check DbDataSync:Url.");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new CheckResult(
                "Binding", CheckStatus.Fail, $"{url} did not answer: {ex.Message}",
                "Start DbDataSync (`dbdatasync serve`) and check DbDataSync:Url.");
        }

        var isLoopback = Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.IsLoopback || uri.Scheme == "https");
        return new CheckResult(
            "Binding", isLoopback ? CheckStatus.Ok : CheckStatus.Warn, url,
            isLoopback ? null : "Plain HTTP off loopback — passkeys need HTTPS here; terminate TLS in front of this.");
    }
}

/// <summary>A user exists, or the bootstrap invite is still outstanding — a fresh install with
/// neither is one nobody can sign into.</summary>
public sealed class FirstAdminCheck : IReadinessCheck
{
    public Task<CheckResult> RunAsync(DoctorContext context, CancellationToken cancellationToken)
    {
        if (context.AuthOptions.Disabled)
            return Task.FromResult(new CheckResult("First admin", CheckStatus.Ok, "Authentication is disabled."));

        var (database, error) = context.TryOpenStateDatabase();
        if (database is null)
        {
            return Task.FromResult(new CheckResult(
                "First admin", CheckStatus.Warn, $"Could not check — the state store did not open: {error?.Message}"));
        }

        if (new UserStore(database).Any())
            return Task.FromResult(new CheckResult("First admin", CheckStatus.Ok, "At least one user exists."));

        var outstanding = new InviteStore(database).ListOutstanding();
        return Task.FromResult(outstanding.Count > 0
            ? new CheckResult(
                "First admin", CheckStatus.Warn, "No users yet — a bootstrap invite is outstanding.",
                "Start DbDataSync and open the invite URL it printed, or run `dbdatasync invite`.")
            : new CheckResult(
                "First admin", CheckStatus.Fail, "No users and no outstanding invite.",
                "Run `dbdatasync invite`."));
    }
}
