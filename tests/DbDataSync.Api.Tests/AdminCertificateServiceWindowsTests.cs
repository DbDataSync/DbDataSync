using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using DbDataSync.Api.Configuration;
using DbDataSync.Api.Services;
using DbDataSync.Certificates;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// <see cref="AdminCertificateService"/> exercised directly, without a host — the same reasoning
/// <c>AdminConfigServiceTests</c> already states: this sandbox's <c>TestServer</c> cannot run any
/// authenticated request once Negotiate is registered, so HTTP-level coverage of authorization
/// (<c>AdminCertificateControllerTests</c>) is written correctly but fails here for a reason that has
/// nothing to do with this phase's logic. Building the service by hand verifies the actual logic without
/// a host at all.
/// <para>
/// <c>Category=Windows</c>, like every one of phase 82's own store-touching tests, because
/// <see cref="AdminCertificateService.GetStatus"/> always resolves the service account
/// (<see cref="InstalledServiceAccount.Resolve"/>) even with nothing bound — there is no path through
/// this class that avoids at least one Windows-only call.
/// </para>
/// <para>
/// **Every test here uses <see cref="StoreLocation.CurrentUser"/>, not <see cref="StoreLocation.LocalMachine"/>**
/// — this sandbox's process is not elevated (confirmed before writing these tests), and writing to
/// <c>LocalMachine\My</c> needs elevation the same way phase 82's own <c>CertificateStoreWindowsTests</c>
/// avoided it. <see cref="AdminCertificateService.CreateSelfSigned"/>, <c>Enroll</c>'s issued branch and
/// <c>Bind</c>'s successful path all hardcode <see cref="StoreLocation.LocalMachine"/> (matching
/// <c>CertCommand</c>'s own production behaviour exactly), so their *installing* half is not exercised
/// end-to-end here — the same honest gap phase 82's own retrospective already accepted for
/// <c>CertCommand.NewSelfSigned</c>/<c>Enroll</c> themselves. What is tested here is everything that does
/// not require a machine-store write: every validation branch, every failure path, the full <c>GetStatus</c>
/// read path against a real <c>CurrentUser</c>-installed certificate, and the key-access decision logic
/// against a real private key.
/// </para>
/// </summary>
[Trait("Category", "Windows")]
[SupportedOSPlatform("windows")]
public sealed class AdminCertificateServiceWindowsTests : IDisposable
{
    private readonly string _repoRoot = Directory.CreateTempSubdirectory("admin-cert-service-tests-").FullName;
    private readonly List<X509Certificate2> _installed = [];

    // Same libgit2-writes-read-only-object-files workaround AdminConfigServiceTests uses — Bind's failure
    // path here never actually commits, but AdminCertificateService's constructor still stands up a real
    // GitCommitService against _repoRoot, which is enough for EnsureInitialized to create a .git directory.
    public void Dispose()
    {
        using (var store = new X509Store(StoreName.My, StoreLocation.CurrentUser))
        {
            store.Open(OpenFlags.ReadWrite);
            foreach (var certificate in _installed)
            {
                if (certificate.GetRSAPrivateKey() is RSACng rsaCng)
                    rsaCng.Key.Delete();

                store.Remove(certificate);
                certificate.Dispose();
            }
        }

        GitTempDirectory.DeleteRecursively(_repoRoot);
    }

    private AdminCertificateService Build() =>
        new(new ApiOptions { RepoRoot = _repoRoot, StateDbPath = "unused", TaskRunnerDllPath = "unused", CliDllPath = "unused" },
            new CertificateOptions(), new GitCommitService(_repoRoot));

    private X509Certificate2 InstallSelfSigned(string commonName)
    {
        var spec = new CertificateSpec(commonName, [commonName], 365, "DbDataSync Test");
        using var ephemeral = CertificateBuilder.CreateSelfSigned(spec);
        var installed = CertificateStore.Install(ephemeral, StoreLocation.CurrentUser);
        _installed.Add(installed);
        return installed;
    }

    /// <summary>Points <c>Kestrel:Certificates:Default:*</c> at a CurrentUser-store certificate — what
    /// production never does (always LocalMachine), but what lets <see cref="AdminCertificateService.GetStatus"/>'s
    /// read path be exercised against a real, store-resident certificate without elevation.</summary>
    private void BindManually(string subject, string location = "CurrentUser", bool allowInvalid = true)
    {
        DbDataSyncConfigFile.SetValue(_repoRoot, CertificateBinding.Section, "Subject", subject);
        DbDataSyncConfigFile.SetValue(_repoRoot, CertificateBinding.Section, "Store", "My");
        DbDataSyncConfigFile.SetValue(_repoRoot, CertificateBinding.Section, "Location", location);
        DbDataSyncConfigFile.SetValue(_repoRoot, CertificateBinding.Section, "AllowInvalid", allowInvalid ? "true" : "false");
    }

    [WindowsOnlyFact]
    public void GetStatus_NothingBoundYet_ReportsNoCertificate_AndKeyAccessUnknown()
    {
        var status = Build().GetStatus();

        Assert.True(status.Available);
        Assert.Null(status.Certificate);
        Assert.Null(status.Binding!.Subject);
        Assert.False(status.Binding.CertificateFound);

        // No "DbDataSync" Windows service is installed in this sandbox (confirmed before writing this
        // test with `sc.exe qc DbDataSync`) — this is the real, ordinary "the question does not apply"
        // case the phase 83 doc calls the one that matters most to get right.
        Assert.Equal(KeyAccessState.Unknown, status.KeyAccess!.State);
        Assert.Null(status.KeyAccess.Account);
    }

    [WindowsOnlyFact]
    public void GetStatus_NoCaConfigured_TemplatesReportTheReason()
    {
        var status = Build().GetStatus();

        Assert.NotNull(status.Templates);
        Assert.Equal(TemplateListReason.CaConfigNotSet, status.Templates!.Reason);
        Assert.Empty(status.Templates.Templates);
        Assert.NotNull(status.Templates.Detail);
    }

    [WindowsOnlyFact]
    public void GetStatus_WithABoundAndInstalledCertificate_ReportsExpiryAndBindingState()
    {
        var commonName = $"dbdatasync-test-{Guid.NewGuid():N}.example.com";
        var installed = InstallSelfSigned(commonName);
        BindManually(commonName);

        var status = Build().GetStatus();

        Assert.NotNull(status.Certificate);
        Assert.Equal(installed.Thumbprint, status.Certificate!.Thumbprint);
        Assert.Equal(commonName, status.Certificate.SubjectCommonName);
        Assert.True(status.Certificate.SelfSigned);
        // Freshly issued for 365 days — comfortably inside a wide "still has most of a year left" band,
        // without this test caring about the exact number.
        Assert.InRange(status.Certificate.DaysRemaining, 300, 366);

        Assert.True(status.Binding!.CertificateFound);
        Assert.Equal(commonName, status.Binding.Subject);
        Assert.True(status.Binding.AllowInvalid);
    }

    [WindowsOnlyFact]
    public void GetStatus_BoundSubjectWithNoMatchingCertificate_ReportsCertificateNotFound()
    {
        BindManually("no-such-certificate.example.com");

        var status = Build().GetStatus();

        Assert.Null(status.Certificate);
        Assert.Equal("no-such-certificate.example.com", status.Binding!.Subject);
        Assert.False(status.Binding.CertificateFound);
    }

    [WindowsOnlyFact]
    public void GetStatus_WithNoPendingEnrollment_ListIsEmpty() =>
        Assert.Empty(Build().GetStatus().PendingEnrollments);

    [WindowsOnlyFact]
    public void GetStatus_WithAPendingEnrollment_ListsIt_AndRemovingItEmptiesTheListAgain()
    {
        PendingEnrollmentStore.Save(_repoRoot, new PendingEnrollment(
            "req-1", "some-key", "dbdatasync.example.com", ["dbdatasync.example.com"],
            "CASERVER\\CA Name", DateTimeOffset.UtcNow));

        var withPending = Build().GetStatus();
        var pending = Assert.Single(withPending.PendingEnrollments);
        Assert.Equal("req-1", pending.RequestId);
        Assert.Equal("dbdatasync.example.com", pending.SubjectCommonName);

        PendingEnrollmentStore.Remove(_repoRoot, "req-1");

        Assert.Empty(Build().GetStatus().PendingEnrollments);
    }

    [WindowsOnlyFact]
    public void CreateSelfSigned_NoDnsNames_FailsBeforeTouchingTheStore() =>
        Assert.False(Build().CreateSelfSigned([], validityDays: null).Succeeded);

    [WindowsOnlyFact]
    public void Enroll_NoCaConfigured_FailsWithAClearMessage()
    {
        var result = Build().Enroll(["dbdatasync.example.com"], "WebServer", caConfig: null);

        Assert.False(result.Succeeded);
        Assert.Contains("CaConfig", result.Message);
    }

    [WindowsOnlyFact]
    public void Enroll_NoDnsNames_Fails() =>
        Assert.False(Build().Enroll([], "WebServer", caConfig: "CASERVER\\CA Name").Succeeded);

    [WindowsOnlyFact]
    public void Enroll_NoTemplate_Fails() =>
        Assert.False(Build().Enroll(["dbdatasync.example.com"], "", caConfig: "CASERVER\\CA Name").Succeeded);

    [WindowsOnlyFact]
    public void Retrieve_UnknownRequestId_Fails()
    {
        var result = Build().Retrieve("never-submitted");

        Assert.False(result.Succeeded);
        Assert.Contains("never-submitted", result.Message);
    }

    [WindowsOnlyFact]
    public void Bind_UnknownThumbprint_Fails_WithoutTouchingGit()
    {
        // LocalMachine\My is read, not written, here — Bind fails at the FindByThumbprint lookup, which
        // needs no elevation, before it would ever reach CertificateBinding.Bind's git commit.
        var result = Build().Bind("0000000000000000000000000000000000dead", allowInvalid: null, CurrentAuthor());

        Assert.False(result.Succeeded);
        Assert.Contains("0000000000000000000000000000000000dead", result.Message);
    }

    [WindowsOnlyFact]
    public void GetCandidates_RunsAgainstLocalMachine_AndEveryEntryHasAThumbprint()
    {
        // Reading LocalMachine\My needs no elevation (only writing to it does) — this does not assert
        // any particular certificate is present, since this sandbox's real machine store is outside
        // this test's control, only that the read-and-project path works end to end.
        var candidates = Build().GetCandidates();

        Assert.All(candidates, c => Assert.False(string.IsNullOrEmpty(c.Thumbprint)));
    }

    [WindowsOnlyFact]
    public void EvaluateKeyAccess_NoServiceInstalled_IsUnknown()
    {
        var result = AdminCertificateService.EvaluateKeyAccess(certificate: null, account: null);

        Assert.Equal(KeyAccessState.Unknown, result.State);
        Assert.Null(result.Account);
    }

    [WindowsOnlyFact]
    public void EvaluateKeyAccess_LocalSystemAccount_IsOk_EvenWithNoCertificate()
    {
        var result = AdminCertificateService.EvaluateKeyAccess(certificate: null, account: "LocalSystem");

        Assert.Equal(KeyAccessState.Ok, result.State);
    }

    [WindowsOnlyFact]
    public void EvaluateKeyAccess_NonLocalSystemAccount_ButNoCertificateToCheck_IsUnknown()
    {
        var result = AdminCertificateService.EvaluateKeyAccess(certificate: null, account: "SomeAccount");

        Assert.Equal(KeyAccessState.Unknown, result.State);
        Assert.Equal("SomeAccount", result.Account);
    }

    [WindowsOnlyFact]
    public void EvaluateKeyAccess_RealCertificate_IsWarningForAnUnrelatedAccount()
    {
        var installed = InstallSelfSigned($"dbdatasync-test-{Guid.NewGuid():N}.example.com");

        // "Guest" — the same unrelated-account choice CertificateStoreWindowsTests already makes for
        // exactly this reason: the *owning* user already has implicit access to a CurrentUser-store
        // key's own container by inheritance, so asserting "warning" against that account would be a
        // false negative waiting to happen, not a real test of the no-access path.
        var result = AdminCertificateService.EvaluateKeyAccess(installed, "Guest");

        Assert.Equal(KeyAccessState.Warning, result.State);
        Assert.Equal("Guest", result.Account);
    }

    [WindowsOnlyFact]
    public void EvaluateKeyAccess_RealCertificate_IsOkAfterAnExplicitGrant()
    {
        var installed = InstallSelfSigned($"dbdatasync-test-{Guid.NewGuid():N}.example.com");

        Assert.Equal(KeyAccessState.Warning, AdminCertificateService.EvaluateKeyAccess(installed, "Guest").State);

        PrivateKeyAccess.Grant(installed, "Guest");

        Assert.Equal(KeyAccessState.Ok, AdminCertificateService.EvaluateKeyAccess(installed, "Guest").State);
    }

    /// <summary>
    /// The phase 83 doc's own verification bullet: "no response body contains key material, asserted
    /// directly rather than by inspection." Serializes a real <see cref="AdminCertificateStatus"/> (from
    /// a certificate with a real, store-resident private key) with the same JSON options
    /// <c>DbDataSyncHost.Build</c> configures for every controller (camelCase, string enums), and checks
    /// the text for PEM markers and the certificate's own raw export — the two shapes a private key or a
    /// certificate's raw bytes would actually show up as if a future change accidentally added a field
    /// carrying either.
    /// </summary>
    [WindowsOnlyFact]
    public void SerializedStatus_NeverContainsKeyMaterial()
    {
        var commonName = $"dbdatasync-test-{Guid.NewGuid():N}.example.com";
        var installed = InstallSelfSigned(commonName);
        BindManually(commonName);

        var status = Build().GetStatus();
        var json = JsonSerializer.Serialize(status, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        Assert.DoesNotContain("PRIVATE KEY", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-----BEGIN", json, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(installed.RawData), json, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(installed.Export(X509ContentType.Cert)), json, StringComparison.Ordinal);
    }

    private static GitAuthor CurrentAuthor() => new("Test Admin", "admin@example.com");
}
