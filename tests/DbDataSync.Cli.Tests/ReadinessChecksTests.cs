using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ClrKernel.Core.Secrets;
using DbDataSync.Certificates;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;
using DbDataSync.Core.Secrets;
using DbDataSync.Libraries;

namespace DbDataSync.Cli.Tests;

/// <summary>
/// <c>dbdatasync config check</c>'s check list, run against real temp repos rather than mocked
/// contexts — the same reasoning <c>ServeCommandPrepareTests</c> uses. <see cref="BindingCheck"/> is
/// left out of every assertion here: none of these tests run a real DbDataSync host, so it always
/// reports "did not answer" regardless of the rest of the configuration, and asserting around it
/// would only be asserting that nothing is listening on port 5080.
/// </summary>
public sealed class ReadinessChecksTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-readiness-tests-").FullName;

    public void Dispose() => GitTempDirectory.DeleteRecursively(_root);

    [Fact]
    public async Task FreshSqliteRepo_RepoAndStateStoreAndAuthChecksPass()
    {
        ServeCommand.Prepare(_root);

        var context = ReadinessChecks.BuildContext(["--repo", _root]);
        var results = await ReadinessChecks.RunChecksAsync(context);

        Assert.Equal(CheckStatus.Ok, Find(results, "Repo").Status);
        Assert.Equal(CheckStatus.Ok, Find(results, "State store").Status);
        Assert.Equal(CheckStatus.Ok, Find(results, "Libraries / drivers").Status);
        Assert.Equal(CheckStatus.Ok, Find(results, "Auth").Status);
    }

    /// <summary>Phase 135's own marker — informational only, so it's always Ok, but the detail text
    /// names the recorded account/platform when one was written.</summary>
    [Fact]
    public async Task ServiceRegistrationCheck_NoMarker_ReportsNotRegistered()
    {
        ServeCommand.Prepare(_root);

        var context = ReadinessChecks.BuildContext(["--repo", _root]);
        var results = await ReadinessChecks.RunChecksAsync(context);

        var check = Find(results, "Service registration");
        Assert.Equal(CheckStatus.Ok, check.Status);
        Assert.Contains("Not registered", check.Detail);
    }

    [Fact]
    public async Task ServiceRegistrationCheck_MarkerPresent_ReportsTheAccountAndPlatform()
    {
        ServeCommand.Prepare(_root);
        ServiceRegistration.Write(_root, "LocalSystem", "windows");

        var context = ReadinessChecks.BuildContext(["--repo", _root]);
        var results = await ReadinessChecks.RunChecksAsync(context);

        var check = Find(results, "Service registration");
        Assert.Equal(CheckStatus.Ok, check.Status);
        Assert.Contains("LocalSystem", check.Detail);
        Assert.Contains("windows", check.Detail);
    }

    private ReadinessContext SelfUpdateContext(bool enabled) =>
        ReadinessChecks.BuildContext(["--repo", _root, $"--DbDataSync:Updates:Mode={(enabled ? "manual" : "disabled")}"]);

    /// <summary>Phase 159: with self-update enabled, an installed unit without the marker is one the console's
    /// update button cannot work with.</summary>
    [Fact]
    public async Task ServiceRegistrationCheck_SelfUpdateOn_AnOldSystemdUnit_Warns()
    {
        ServeCommand.Prepare(_root);
        ServiceRegistration.Write(_root, "dbdatasync", "linux");
        var unit = Path.Combine(_root, "old.service");
        File.WriteAllText(unit, "[Service]\nExecStart=/usr/bin/dbdatasync serve\nRestart=on-failure\n");

        var result = await new ServiceRegistrationCheck(unit).RunAsync(SelfUpdateContext(enabled: true), CancellationToken.None);

        Assert.Equal(CheckStatus.Warn, result.Status);
        Assert.Contains("does not apply updates", result.Detail);
        Assert.Contains("service install --self-update", result.Fix);
    }

    /// <summary>A unit without the step is the default, and correct when self-update is off.</summary>
    [Fact]
    public async Task ServiceRegistrationCheck_SelfUpdateOff_AnOrdinaryUnit_IsOk()
    {
        ServeCommand.Prepare(_root);
        ServiceRegistration.Write(_root, "dbdatasync", "linux");
        var unit = Path.Combine(_root, "ordinary.service");
        File.WriteAllText(unit, SystemdService.RenderUnit("/usr/bin/dbdatasync", "/var/lib/dbdatasync", "http://localhost:5080", "dbdatasync"));

        var result = await new ServiceRegistrationCheck(unit).RunAsync(SelfUpdateContext(enabled: false), CancellationToken.None);

        Assert.Equal(CheckStatus.Ok, result.Status);
        Assert.DoesNotContain("does not apply", result.Detail);
    }

    [Fact]
    public async Task ServiceRegistrationCheck_SelfUpdateOn_ACurrentUnit_IsOk()
    {
        ServeCommand.Prepare(_root);
        ServiceRegistration.Write(_root, "dbdatasync", "linux");
        var unit = Path.Combine(_root, "current.service");
        File.WriteAllText(unit, SystemdService.RenderUnit("/usr/bin/dbdatasync", "/var/lib/dbdatasync", "http://localhost:5080", "dbdatasync", selfUpdate: true));

        var result = await new ServiceRegistrationCheck(unit).RunAsync(SelfUpdateContext(enabled: true), CancellationToken.None);

        Assert.Equal(CheckStatus.Ok, result.Status);
    }

    [Fact]
    public async Task ServiceRegistrationCheck_AMissingUnitFile_IsNotJudged()
    {
        ServeCommand.Prepare(_root);
        ServiceRegistration.Write(_root, "dbdatasync", "linux");

        var result = await new ServiceRegistrationCheck(Path.Combine(_root, "not-there.service"))
            .RunAsync(SelfUpdateContext(enabled: true), CancellationToken.None);

        Assert.Equal(CheckStatus.Ok, result.Status);
    }

    [Fact]
    public async Task ServiceRegistrationCheck_AWindowsServiceIsNeverJudgedByAUnitFile()
    {
        ServeCommand.Prepare(_root);
        ServiceRegistration.Write(_root, "LocalSystem", "windows");
        var unit = Path.Combine(_root, "old.service");
        File.WriteAllText(unit, "[Service]\n");

        var result = await new ServiceRegistrationCheck(unit).RunAsync(SelfUpdateContext(enabled: true), CancellationToken.None);

        Assert.Equal(CheckStatus.Ok, result.Status);
    }

    /// <summary>Phase 123's own "passes on the dev/CI box" requirement, against the real environment
    /// (no faking) — this Linux sandbox has a real <c>/etc/dotnet/install_location</c>, which is
    /// exactly the common case this check exists to recognize.</summary>
    [Fact]
    public async Task RuntimeDiscoverabilityCheck_PassesOnThisBox()
    {
        ServeCommand.Prepare(_root);

        var context = ReadinessChecks.BuildContext(["--repo", _root]);
        var results = await ReadinessChecks.RunChecksAsync(context);

        Assert.Equal(CheckStatus.Ok, Find(results, "Runtime").Status);
    }

    [Fact]
    public async Task ConnectionNamingAnUninstalledDriver_LibrariesAndDriversCheckFails()
    {
        ServeCommand.Prepare(_root);
        var configRoot = Path.Combine(_root, "config");
        var repository = new ConfigRepository(
            configRoot, new GitCommitService(_root), new SecretStore("DbDataSync", true));
        repository.SaveConnection(
            new ConnectionInput
            {
                Name = "unreachable",
                DriverType = "NoSuchDriver",
                Host = "db.example",
                AuthMode = AuthMode.None,
            },
            CurrentUserForTests.Author);

        var context = ReadinessChecks.BuildContext(["--repo", _root]);
        var results = await ReadinessChecks.RunChecksAsync(context);

        var check = Find(results, "Libraries / drivers");
        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Contains("dbdatasync config driver install", check.Detail);
    }

    [Fact]
    public async Task PasskeyRelyingPartyIdThatIsAUrl_AuthCheckFailsWithTheProblemText()
    {
        ServeCommand.Prepare(_root);
        DbDataSyncConfigFile.SetValue(_root, "DbDataSync:Auth:Passkeys", "RelyingPartyId", "https://example.com");

        var context = ReadinessChecks.BuildContext(["--repo", _root]);
        var results = await ReadinessChecks.RunChecksAsync(context);

        var check = Find(results, "Auth");
        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Contains("looks like a URL", check.Detail);
    }

    [Fact]
    public async Task NoFileBasedCertificateConfigured_CertificateCheckPasses()
    {
        ServeCommand.Prepare(_root);

        var context = ReadinessChecks.BuildContext(["--repo", _root]);
        var results = await ReadinessChecks.RunChecksAsync(context);

        Assert.Equal(CheckStatus.Ok, Find(results, "Certificate").Status);
    }

    [Fact]
    public async Task ConfiguredCertificateFileMissing_CertificateCheckFails()
    {
        ServeCommand.Prepare(_root);
        DbDataSyncConfigFile.SetValue(_root, CertificateBinding.Section, "Path", Path.Combine(_root, "missing.pem"));
        DbDataSyncConfigFile.SetValue(_root, CertificateBinding.Section, "KeyPath", Path.Combine(_root, "missing-key.pem"));

        var context = ReadinessChecks.BuildContext(["--repo", _root]);
        var results = await ReadinessChecks.RunChecksAsync(context);

        var check = Find(results, "Certificate");
        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Contains("does not exist", check.Detail);
    }

    [Fact]
    public async Task ConfiguredCertificate_SanCoversTheConsoleUrlHost_CertificateCheckPasses()
    {
        ServeCommand.Prepare(_root);
        var (certPath, keyPath) = WriteSelfSignedPem(_root, "console.local");
        DbDataSyncConfigFile.SetValue(_root, "DbDataSync:App", "Url", "https://console.local:5080");
        DbDataSyncConfigFile.SetValue(_root, CertificateBinding.Section, "Path", certPath);
        DbDataSyncConfigFile.SetValue(_root, CertificateBinding.Section, "KeyPath", keyPath);

        var context = ReadinessChecks.BuildContext(["--repo", _root]);
        var results = await ReadinessChecks.RunChecksAsync(context);

        var check = Find(results, "Certificate");
        Assert.Equal(CheckStatus.Ok, check.Status);
    }

    [Fact]
    public async Task ConfiguredCertificate_SanDoesNotCoverTheConsoleUrlHost_CertificateCheckFails()
    {
        ServeCommand.Prepare(_root);
        var (certPath, keyPath) = WriteSelfSignedPem(_root, "console.local");
        DbDataSyncConfigFile.SetValue(_root, "DbDataSync:App", "Url", "https://a-different-host.example:5080");
        DbDataSyncConfigFile.SetValue(_root, CertificateBinding.Section, "Path", certPath);
        DbDataSyncConfigFile.SetValue(_root, CertificateBinding.Section, "KeyPath", keyPath);

        var context = ReadinessChecks.BuildContext(["--repo", _root]);
        var results = await ReadinessChecks.RunChecksAsync(context);

        var check = Find(results, "Certificate");
        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Contains("does not cover", check.Detail);
    }

    /// <summary>Phase 130 — a certificate at the well-known managed path, current, reports the
    /// distinguishing "self-signed (managed)" marker rather than a plain date.</summary>
    [Fact]
    public async Task ManagedSelfSignedCertificate_Current_CertificateCheckReportsTheManagedMarker()
    {
        ServeCommand.Prepare(_root);
        DbDataSyncConfigFile.SetValue(_root, "DbDataSync:App", "Url", "https://console.local:5080");
        BindAManagedSelfSignedCertificate("console.local", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(90));

        var context = ReadinessChecks.BuildContext(["--repo", _root]);
        var results = await ReadinessChecks.RunChecksAsync(context);

        var check = Find(results, "Certificate");
        Assert.Equal(CheckStatus.Ok, check.Status);
        Assert.Contains("self-signed (managed)", check.Detail);
    }

    /// <summary>Phase 130 — a managed certificate past its own <c>NotAfter</c> still fails, with the
    /// marker carried through so the message names what actually needs attention.</summary>
    [Fact]
    public async Task ManagedSelfSignedCertificate_Expired_CertificateCheckFailsWithTheManagedMarker()
    {
        ServeCommand.Prepare(_root);
        DbDataSyncConfigFile.SetValue(_root, "DbDataSync:App", "Url", "https://console.local:5080");
        BindAManagedSelfSignedCertificate("console.local", DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(-1));

        var context = ReadinessChecks.BuildContext(["--repo", _root]);
        var results = await ReadinessChecks.RunChecksAsync(context);

        var check = Find(results, "Certificate");
        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Contains("self-signed (managed)", check.Detail);
        Assert.Contains("expired", check.Detail);
    }

    /// <summary>
    /// Puts a managed self-signed certificate at the well-known path and points Kestrel at it — what
    /// <c>dbdatasync config cert new-self-signed</c> does off Windows.
    /// <para>
    /// Written directly rather than by invoking that command, since phase 140: on Windows
    /// <c>CertCommand</c> dispatches <c>new-self-signed</c> to the certificate-*store* implementation
    /// instead, so the setup silently produced no managed PFX at all and these two tests failed on the
    /// first real <c>windows-latest</c> run. The subject here is <see cref="ReadinessChecks"/>' reading
    /// of a managed certificate, which is cross-platform (<c>DbDataSyncHost</c> runs its renewal service
    /// on Windows too) — so the setup is made platform-independent and the coverage kept, rather than
    /// the tests being skipped on the one platform whose CLI cannot currently produce this state.
    /// </para>
    /// </summary>
    private void BindAManagedSelfSignedCertificate(string host, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using (var certificate = WriteAgedSelfSigned(host, notBefore, notAfter))
            ManagedSelfSignedCertificate.Write(_root, certificate);

        DbDataSyncConfigFile.SetValue(_root, CertificateBinding.Section, "Path", ManagedSelfSignedCertificate.PfxPath(_root));
        DbDataSyncConfigFile.SetValue(_root, CertificateBinding.Section, "AllowInvalid", "true");
    }

    /// <summary>Phase 130 — a self-signed certificate at any path *other* than the managed one keeps
    /// today's plain, date-based report: no generic self-signed caution existed here before this phase
    /// (checked directly), so there is none to add for the unmanaged case.</summary>
    [Fact]
    public async Task SelfSignedCertificate_AtAnUnmanagedPath_CertificateCheckHasNoManagedMarker()
    {
        ServeCommand.Prepare(_root);
        var (certPath, keyPath) = WriteSelfSignedPem(_root, "console.local");
        DbDataSyncConfigFile.SetValue(_root, "DbDataSync:App", "Url", "https://console.local:5080");
        DbDataSyncConfigFile.SetValue(_root, CertificateBinding.Section, "Path", certPath);
        DbDataSyncConfigFile.SetValue(_root, CertificateBinding.Section, "KeyPath", keyPath);

        var context = ReadinessChecks.BuildContext(["--repo", _root]);
        var results = await ReadinessChecks.RunChecksAsync(context);

        var check = Find(results, "Certificate");
        Assert.Equal(CheckStatus.Ok, check.Status);
        Assert.DoesNotContain("managed", check.Detail);
    }

    /// <summary>
    /// Builds a certificate with an explicit, arbitrary <c>NotBefore</c>/<c>NotAfter</c> window —
    /// <see cref="ManagedSelfSignedCertificate.Generate"/> always anchors <c>NotBefore</c> a few minutes
    /// before "now" (absorbing clock skew), so it cannot itself produce an already-expired certificate.
    /// </summary>
    private static X509Certificate2 WriteAgedSelfSigned(string host, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={host}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName(host);
        request.CertificateExtensions.Add(sanBuilder.Build());
        return request.CreateSelfSigned(notBefore, notAfter);
    }

    /// <summary>Writes a fresh self-signed PEM cert+key pair for <paramref name="dnsName"/>, valid 90
    /// days — comfortably outside the default 30-day expiry-warning window, so this is unambiguously
    /// an <see cref="CheckStatus.Ok"/> case rather than a <see cref="CheckStatus.Warn"/> one.</summary>
    private static (string CertPath, string KeyPath) WriteSelfSignedPem(string root, string dnsName)
    {
        using var certificate = CertificateBuilder.CreateSelfSigned(
            new CertificateSpec(dnsName, [dnsName], ValidityDays: 90, FriendlyName: null));

        var certPath = Path.Combine(root, "cert.pem");
        var keyPath = Path.Combine(root, "key.pem");
        File.WriteAllText(certPath, certificate.ExportCertificatePem());
        using (var rsa = certificate.GetRSAPrivateKey())
            File.WriteAllText(keyPath, rsa!.ExportPkcs8PrivateKeyPem());

        return (certPath, keyPath);
    }

    /// <summary>Drives the real CLI path (<c>dbdatasync config check --json</c>) rather than the
    /// engine directly, so this one test also proves <see cref="ConfigCommand"/>'s own dispatch and
    /// exit-code plumbing, not just the check list.</summary>
    [Fact]
    public async Task JsonOutput_IsValidAndNamesEveryCheck()
    {
        ServeCommand.Prepare(_root);

        var (exitCode, output) = RunConfigCheck(["check", "--repo", _root, "--json"]);

        Assert.Equal(1, exitCode); // BindingCheck fails — nothing is listening.
        using var document = JsonDocument.Parse(output);
        var names = document.RootElement.EnumerateArray().Select(e => e.GetProperty("Name").GetString()).ToList();
        Assert.Contains("Repo", names);
        Assert.Contains("Service registration", names);
        Assert.Contains("State store", names);
        Assert.Contains("Libraries / drivers", names);
        Assert.Contains("Runtime", names);
        Assert.Contains("Auth", names);
        Assert.Contains("Certificate", names);
        Assert.Contains("Binding", names);
        Assert.Contains("First admin", names);
    }

    /// <summary>Phase 109j: with no library installed at all, there is nothing to check yet — the new
    /// check is silent (<see cref="CheckStatus.Ok"/>), not a warning about an absence
    /// <see cref="LibrariesAndDriversCheck"/> doesn't itself flag either (no connection names an
    /// uninstalled built-in driver here — a built-in is always "installed" as code; only its *library*
    /// might not be).</summary>
    [Fact]
    public async Task NoLibrariesInstalled_LibraryCompatibilityCheckIsSilentlyOk()
    {
        ServeCommand.Prepare(_root);

        var context = ReadinessChecks.BuildContext(["--repo", _root]);
        var results = await ReadinessChecks.RunChecksAsync(context);

        Assert.Equal(CheckStatus.Ok, Find(results, "Library compatibility").Status);
    }

    /// <summary>Real-world confirmation (phase 109j's own bar): the actually-installed, currently-pinned
    /// <c>microsoft-data-sqlclient</c>/<c>npgsql</c>/<c>duckdb</c> against the real, current
    /// <c>MsSqlDriver</c>/<c>PostgresDriver</c>/<c>DuckDbDriver</c> IL — no real network/DB connection
    /// needed, since this is the static, no-execution half. Passes clean: no false positive against real,
    /// current usage.</summary>
    [Fact]
    public async Task EveryBuiltInLibraryInstalledAtItsPinnedVersion_LibraryCompatibilityCheckIsClean()
    {
        ServeCommand.Prepare(_root);
        foreach (var id in new[] { "microsoft-data-sqlclient", "npgsql", "duckdb" })
        {
            var entry = KnownLibraries.TryGetById(id)!;
            await LibraryInstaller.InstallAsync(
                _root, entry.Id, [new PackageRef(entry.PackageId, entry.PinnedVersion)], entry.FactoryType);
        }

        var context = ReadinessChecks.BuildContext(["--repo", _root]);
        var results = await ReadinessChecks.RunChecksAsync(context);

        var check = Find(results, "Library compatibility");
        Assert.Equal(CheckStatus.Ok, check.Status);
    }

    /// <summary>The per-library dedup this check's own doc comment promises (phase doc's open question
    /// 4, resolved as "per-library, deduplicating drivers that share one") — proven directly against
    /// hand-built results, since a real incompatible built-in library version was not found within
    /// reasonable effort (every version this phase actually tried remained compatible).</summary>
    [Fact]
    public void Summarize_TwoDriversSharingOneIncompatibleLibrary_ReportsItOnceNamingBothDrivers()
    {
        var results = new[]
        {
            new DriverLibraryCompatibilityResult("MsSql", "shared-lib", "Shared.Assembly", Compatible: false, ["Shared.Assembly.Widget.DoThing(1 arg(s))"]),
            new DriverLibraryCompatibilityResult("Postgres", "shared-lib", "Shared.Assembly", Compatible: false, ["Shared.Assembly.Widget.DoThing(1 arg(s))"]),
        };

        var check = LibraryCompatibilityCheck.Summarize(results);

        Assert.Equal(CheckStatus.Warn, check.Status);
        // Reported once, not twice — a single "shared-lib" mention, naming both drivers.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(check.Detail, "shared-lib"));
        Assert.Contains("MsSql", check.Detail);
        Assert.Contains("Postgres", check.Detail);
        Assert.Contains("DoThing", check.Detail);
        Assert.NotNull(check.Fix);
    }

    [Fact]
    public void Summarize_EveryResultCompatible_IsOk()
    {
        var results = new[]
        {
            new DriverLibraryCompatibilityResult("MsSql", "microsoft-data-sqlclient", "Microsoft.Data.SqlClient", Compatible: true, []),
        };

        var check = LibraryCompatibilityCheck.Summarize(results);

        Assert.Equal(CheckStatus.Ok, check.Status);
    }

    [Fact]
    public void Summarize_NoResultsAtAll_IsOk()
    {
        var check = LibraryCompatibilityCheck.Summarize([]);

        Assert.Equal(CheckStatus.Ok, check.Status);
    }

    private static CheckResult Find(IReadOnlyList<CheckResult> results, string name) =>
        results.Single(r => r.Name == name);

    private static (int ExitCode, string Output) RunConfigCheck(string[] args)
    {
        var originalOut = Console.Out;
        using var output = new StringWriter();
        Console.SetOut(output);
        try
        {
            var exitCode = ConfigCommand.RunAsync(args).GetAwaiter().GetResult();
            return (exitCode, output.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }
}

internal static class CurrentUserForTests
{
    public static GitAuthor Author { get; } = new("Test", "test@localhost");
}
