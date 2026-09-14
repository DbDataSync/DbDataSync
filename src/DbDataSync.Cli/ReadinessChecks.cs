using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ClrKernel.Core.Secrets;
using DbDataSync.Api.Auth;
using DbDataSync.Api.Configuration;
using DbDataSync.Certificates;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;
using DbDataSync.Core.Secrets;
using DbDataSync.Drivers.Descriptor;
using DbDataSync.Libraries;
using DbDataSync.State;
using Microsoft.Extensions.Configuration;
using LibGit2Sharp;

namespace DbDataSync.Cli;

internal enum CheckStatus { Ok, Warn, Fail }

/// <param name="Fix">A command or a one-line instruction that would resolve <see cref="Status"/>, or
/// null for <see cref="CheckStatus.Ok"/> (nothing to fix) or where there is no single fix to name.</param>
internal sealed record CheckResult(string Name, CheckStatus Status, string Detail, string? Fix = null);

/// <summary>What every check is handed — resolved once per <c>config check</c> run (or once per
/// <c>setup</c> review screen render) rather than each check rebuilding its own configuration chain.</summary>
internal sealed class ReadinessContext
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
            var libraryRegistry = new LibraryRegistry(Root).LoadAll();
            var database = StateDatabase.FromOptions(
                ApiOptions.StateEngine, ApiOptions.StateDbPath, ApiOptions.StateConnectionString, secrets,
                libraryRegistry);
            return (database, null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException
            or System.Data.Common.DbException)
        {
            return (null, ex);
        }
    }
}

internal interface IReadinessCheck
{
    Task<CheckResult> RunAsync(ReadinessContext context, CancellationToken cancellationToken);
}

/// <summary>
/// The shared check engine behind <c>dbdatasync config check</c> (the non-interactive CLI entry,
/// <see cref="ConfigCommand"/>) and <c>setup</c>'s review screen (rendered for a human, in-process).
/// Built as its own top-level <c>doctor</c> command in phase 110; phase 115 folded that entry point
/// under <c>config</c> and pulled the engine itself out into this neutral file so it belongs to
/// neither caller.
/// </summary>
internal static class ReadinessChecks
{
    /// <summary>Every check, in report order — shared by <see cref="ConfigCommand"/> and
    /// <c>SetupCommand</c>'s review screen so the two never drift onto separate lists.</summary>
    internal static readonly IReadOnlyList<IReadinessCheck> Checks =
    [
        new RepoCheck(),
        new StateStoreCheck(),
        new LibrariesAndDriversCheck(),
        new RuntimeDiscoverabilityCheck(),
        new AuthCheck(),
        new CertificateCheck(),
        new BindingCheck(),
        new FirstAdminCheck(),
    ];

    internal static async Task<IReadOnlyList<CheckResult>> RunChecksAsync(ReadinessContext context)
    {
        var results = new List<CheckResult>();
        foreach (var check in Checks)
            results.Add(await check.RunAsync(context, CancellationToken.None));
        return results;
    }

    /// <summary>One or two lines of human-readable text for <paramref name="result"/> — <c>config
    /// check</c>'s own plain-text report, one line at a time via <see cref="Console"/>. Phase 128's
    /// <c>dbdatasync setup</c> TUI renders <see cref="CheckResult"/> directly instead (a color-coded
    /// list, not flattened text) — this stays here for <see cref="ConfigCommand"/>'s path only.</summary>
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
    internal static ReadinessContext BuildContext(string[] args)
    {
        var root = DbDataSyncRoot.Resolve(args);

        var builder = new ConfigurationBuilder();
        if (File.Exists(DbDataSyncConfigFile.PathIn(root)))
            builder.Add(new DbDataSyncConfigFileSource { InitialData = DbDataSyncConfigFile.Read(root) });
        builder.AddEnvironmentVariables();
        builder.AddCommandLine(args.Where(a => a.StartsWith("--DbDataSync:", StringComparison.Ordinal)).ToArray());

        var configuration = builder.Build();
        return new ReadinessContext
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
internal sealed class RepoCheck : IReadinessCheck
{
    public Task<CheckResult> RunAsync(ReadinessContext context, CancellationToken cancellationToken)
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
internal sealed class StateStoreCheck : IReadinessCheck
{
    public Task<CheckResult> RunAsync(ReadinessContext context, CancellationToken cancellationToken)
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
                  "(`dbdatasync config secret set dbdatasync:config:stateConnectionString ...`)."));
    }
}

/// <summary>Every restored library's <c>lib/</c> closure is present, every driver manifest on disk
/// parses, and every connection's driver id resolves to a built-in or a driver found on disk.</summary>
internal sealed class LibrariesAndDriversCheck : IReadinessCheck
{
    private static readonly HashSet<string> BuiltInDriverIds = [DriverIds.MsSql, DriverIds.Postgres, DriverIds.DuckDb];

    public Task<CheckResult> RunAsync(ReadinessContext context, CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        var driverIds = new HashSet<string>(BuiltInDriverIds, StringComparer.Ordinal);

        var librariesRoot = LibraryPaths.LibrariesDir(context.Root);
        if (Directory.Exists(librariesRoot))
        {
            foreach (var dir in Directory.EnumerateDirectories(librariesRoot))
            {
                var manifestPath = LibraryPaths.ManifestPath(dir);
                if (!File.Exists(manifestPath))
                    continue;

                var libDir = LibraryPaths.LibDir(dir);
                if (!Directory.Exists(libDir) || !Directory.EnumerateFileSystemEntries(libDir).Any())
                    problems.Add($"library '{Path.GetFileName(dir)}' has no restored lib/ — run `dbdatasync config library sync`.");
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
                        $"run `dbdatasync config driver install {connection.DriverType} ...`.");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"could not read connections: {ex.Message}");
        }

        return Task.FromResult(problems.Count == 0
            ? new CheckResult("Libraries / drivers", CheckStatus.Ok, "Every connection's driver resolves.")
            : new CheckResult("Libraries / drivers", CheckStatus.Fail, string.Join(" ", problems)));
    }
}

/// <summary>
/// Whether the .NET runtime is discoverable *machine-wide*, not just by this process — which already
/// found it, so checking that would prove nothing. What breaks silently otherwise (phase 123): a
/// systemd unit's <c>nologin</c> service account has no login shell to source <c>PATH</c> from, so a
/// <c>dotnet</c> installed by <c>dotnet-install.sh</c> into an interactive user's own <c>~/.dotnet</c>
/// is invisible to it — the service fails to start with "You must install the .NET runtime", a message
/// that names nothing about why. A warning, not a failure — every check here is a heuristic, and a
/// genuinely unusual layout this doesn't recognize is not proof the service would actually fail.
/// </summary>
internal sealed class RuntimeDiscoverabilityCheck : IReadinessCheck
{
    // Covers an apt/dnf/package-manager install (Debian/Ubuntu's dotnet-host puts it here; RHEL/Fedora
    // under lib64), a tarball extracted to the conventional location, and Snap's own confined path.
    private static readonly string[] WellKnownDotnetPaths =
    [
        "/usr/lib/dotnet/dotnet",
        "/usr/lib64/dotnet/dotnet",
        "/usr/share/dotnet/dotnet",
        "/usr/local/share/dotnet/dotnet",
        "/snap/bin/dotnet",
    ];

    public Task<CheckResult> RunAsync(ReadinessContext context, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
            return Task.FromResult(new CheckResult("Runtime", CheckStatus.Ok, "The .NET runtime installs machine-wide on Windows."));

        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(dotnetRoot))
            return Task.FromResult(new CheckResult("Runtime", CheckStatus.Ok, $"DOTNET_ROOT={dotnetRoot}"));

        // Written by the official install script and every major package manager's dotnet-host
        // package — the one signal that doesn't depend on guessing a specific install path right.
        const string installLocationFile = "/etc/dotnet/install_location";
        if (File.Exists(installLocationFile))
        {
            return Task.FromResult(new CheckResult(
                "Runtime", CheckStatus.Ok, $"Found '{installLocationFile}' ({File.ReadAllText(installLocationFile).Trim()})."));
        }

        var found = WellKnownDotnetPaths.FirstOrDefault(File.Exists);
        if (found is not null)
            return Task.FromResult(new CheckResult("Runtime", CheckStatus.Ok, $"Found '{found}'."));

        return Task.FromResult(new CheckResult(
            "Runtime", CheckStatus.Warn,
            "Could not confirm the .NET runtime is discoverable machine-wide — a nologin service " +
            "account's own environment may not see it.",
            "Install .NET via your package manager, or set DOTNET_ROOT system-wide."));
    }
}

/// <summary>A method is configured or authentication is explicitly disabled; the passkey
/// configuration (if any) is internally consistent.</summary>
internal sealed class AuthCheck : IReadinessCheck
{
    public Task<CheckResult> RunAsync(ReadinessContext context, CancellationToken cancellationToken)
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

/// <summary>
/// A file-based certificate (phase 113), if one is configured: the file loads, its SANs cover the
/// console URL's host, and it isn't near expiry. A Windows store-based binding
/// (<c>Kestrel:Certificates:Default:Subject</c>) isn't covered here — <c>CertificateExpiryService</c>
/// already watches that path independently, and duplicating it would mean two different answers for
/// the same certificate if they ever disagreed.
/// <para>
/// Phase 130: a certificate at <see cref="ManagedSelfSignedCertificate.PfxPath"/> is reported as
/// "self-signed (managed)" rather than a plain date, since <c>SelfSignedCertificateService</c> already
/// owns keeping it current.
/// </para>
/// </summary>
internal sealed class CertificateCheck : IReadinessCheck
{
    public Task<CheckResult> RunAsync(ReadinessContext context, CancellationToken cancellationToken)
    {
        var path = context.Configuration["Kestrel:Certificates:Default:Path"];
        if (string.IsNullOrEmpty(path))
            return Task.FromResult(new CheckResult("Certificate", CheckStatus.Ok, "No file-based certificate configured."));

        if (!File.Exists(path))
        {
            return Task.FromResult(new CheckResult(
                "Certificate", CheckStatus.Fail, $"'{path}' does not exist.",
                "Run `dbdatasync config cert use-pem`/`use-pfx` again with a valid path."));
        }

        X509Certificate2 certificate;
        try
        {
            var keyPath = context.Configuration["Kestrel:Certificates:Default:KeyPath"];
            certificate = CertCommand.LoadFileCertificate(path, keyPath);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException)
        {
            return Task.FromResult(new CheckResult("Certificate", CheckStatus.Fail, $"'{path}' does not load: {ex.Message}"));
        }

        var url = context.Configuration["DbDataSync:Url"] ?? "http://localhost:5080";
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;
        var dnsNames = CertificateSanReader.GetDnsNames(certificate);
        var sanOk = host is null || dnsNames.Any(name =>
            string.Equals(name, host, StringComparison.OrdinalIgnoreCase)
            || (name.StartsWith("*.", StringComparison.Ordinal)
                && host.EndsWith(name[1..], StringComparison.OrdinalIgnoreCase)));

        if (!sanOk)
        {
            return Task.FromResult(new CheckResult(
                "Certificate", CheckStatus.Fail,
                $"'{path}' does not cover '{host}' — SANs: {string.Join(", ", dnsNames)}."));
        }

        // Phase 130: a certificate at this well-known path is generated and kept renewed by this
        // codebase's own SelfSignedCertificateService — a marker so an operator sees "self-signed
        // (managed)" rather than a report indistinguishable from a certificate bound by accident. Any
        // other path — including one that happens to be self-signed — keeps exactly the plain,
        // date-based report below; checked directly, no such distinction existed here before this
        // phase, so there is no pre-existing generic self-signed caution to preserve.
        var isManaged = string.Equals(
            Path.GetFullPath(path), ManagedSelfSignedCertificate.PfxPath(context.Root), StringComparison.OrdinalIgnoreCase);

        var daysRemaining = (int)Math.Floor((certificate.NotAfter - DateTimeOffset.UtcNow).TotalDays);
        if (daysRemaining <= 0)
        {
            return Task.FromResult(new CheckResult(
                "Certificate", CheckStatus.Fail,
                isManaged ? $"self-signed (managed) — expired {-daysRemaining} day(s) ago." : $"Expired {-daysRemaining} day(s) ago."));
        }

        if (daysRemaining <= context.CertificateOptions.ExpiryWarningDays)
        {
            return Task.FromResult(new CheckResult(
                "Certificate", CheckStatus.Warn,
                isManaged
                    ? $"self-signed (managed) — expires in {daysRemaining} day(s); the managed renewal " +
                      "service will regenerate it automatically (a restart is still needed to pick it up)."
                    : $"Expires in {daysRemaining} day(s) — renew soon.",
                isManaged ? null : "Run `dbdatasync config cert use-pem`/`use-pfx` again with the renewed file."));
        }

        return Task.FromResult(new CheckResult(
            "Certificate", CheckStatus.Ok,
            isManaged ? $"self-signed (managed) — valid for {daysRemaining} more day(s)." : $"Valid for {daysRemaining} more day(s)."));
    }
}

/// <summary>The console URL actually answers. Warns rather than fails on plain HTTP off loopback —
/// passkeys need HTTPS there, but the console can still be reachable.</summary>
internal sealed class BindingCheck : IReadinessCheck
{
    public async Task<CheckResult> RunAsync(ReadinessContext context, CancellationToken cancellationToken)
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
internal sealed class FirstAdminCheck : IReadinessCheck
{
    public Task<CheckResult> RunAsync(ReadinessContext context, CancellationToken cancellationToken)
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
