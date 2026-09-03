using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using DbDataSync.Api.Configuration;
using DbDataSync.Certificates;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;

namespace DbDataSync.Api.Services;

/// <summary>
/// The Certificates section of the Admin screen's backend (phase 83) — a second door onto every
/// operation phase 82's <c>dbdatasync cert …</c> already exposes, not a wider one. See that phase's own
/// doc comments (<see cref="DbDataSync.Certificates"/>) for what each underlying call actually does; this
/// class only sequences them the way <c>CertCommand</c> already does, and translates the result into
/// JSON a browser can read.
/// <para>
/// **Gated once, at the very top of every public method**, the same shape <c>CertCommand.Run</c> and
/// <see cref="CertificateExpiryService"/> already use: <see cref="OperatingSystem.IsWindows"/> false
/// means "report unavailable," not "try anyway and let a <see cref="PlatformNotSupportedException"/>
/// turn into an unhandled 500." Every genuinely Windows-only call is additionally marked
/// <see cref="SupportedOSPlatformAttribute"/> so the CA1416 analyzer proves the gate is real, not just a
/// comment.
/// </para>
/// <para>
/// **No endpoint this class backs returns a private key, and none exports one.** Every DTO below is a
/// deliberately flat, hand-picked projection of what phase 82's types already expose — thumbprints,
/// subjects, SAN lists, dates, an account name — never a certificate's raw bytes or anything from its
/// private key. See the phase 83 doc's "No endpoint returns a private key" for why that is a bright
/// line and not merely an oversight to avoid.
/// </para>
/// </summary>
public sealed class AdminCertificateService(ApiOptions apiOptions, CertificateOptions certificateOptions, GitCommitService git)
{
    /// <summary>The Windows service name <c>dbdatasync service install</c> registers under — duplicated as
    /// a literal rather than referencing <c>DbDataSync.Cli.ServiceCommand.ServiceName</c>, the same call
    /// <see cref="CertificateExpiryService"/> already makes: <c>DbDataSync.Api</c> does not (and should
    /// not) depend on the CLI project.</summary>
    private const string ServiceName = "DbDataSync";

    private const string WindowsOnlyMessage =
        "Certificate management is Windows-only. On this host, TLS is expected to be terminated in " +
        "front of the app, the way any reverse proxy would.";

    public AdminCertificateStatus GetStatus() =>
        OperatingSystem.IsWindows() ? GetStatusOnWindows() : AdminCertificateStatus.Unavailable(WindowsOnlyMessage);

    public IReadOnlyList<CertificateCandidate> GetCandidates() =>
        OperatingSystem.IsWindows() ? GetCandidatesOnWindows() : [];

    public CertificateActionResult CreateSelfSigned(IReadOnlyList<string> dnsNames, int? validityDays) =>
        OperatingSystem.IsWindows()
            ? CreateSelfSignedOnWindows(dnsNames, validityDays)
            : CertificateActionResult.Failed(WindowsOnlyMessage);

    public CertificateActionResult Enroll(IReadOnlyList<string> dnsNames, string template, string? caConfig) =>
        OperatingSystem.IsWindows()
            ? EnrollOnWindows(dnsNames, template, caConfig)
            : CertificateActionResult.Failed(WindowsOnlyMessage);

    public CertificateActionResult Retrieve(string requestId) =>
        OperatingSystem.IsWindows() ? RetrieveOnWindows(requestId) : CertificateActionResult.Failed(WindowsOnlyMessage);

    public CertificateActionResult Bind(string thumbprint, bool? allowInvalid, GitAuthor author) =>
        OperatingSystem.IsWindows()
            ? BindOnWindows(thumbprint, allowInvalid, author)
            : CertificateActionResult.Failed(WindowsOnlyMessage);

    [SupportedOSPlatform("windows")]
    private AdminCertificateStatus GetStatusOnWindows()
    {
        var bound = CertificateBinding.Read(apiOptions.RepoRoot);
        var location = ParseLocation(bound.Location);
        var certificate = bound.Subject is null ? null : CertificateStore.FindBySubject(bound.Subject, location);

        var config = DbDataSyncConfigFile.Read(apiOptions.RepoRoot);
        var caConfig = config.GetValueOrDefault("DbDataSync:Certificates:CaConfig");
        var templates = CertificateTemplateCatalog.List(caConfig);

        return new AdminCertificateStatus(
            Available: true,
            UnavailableReason: null,
            Certificate: certificate is null ? null : ToCurrentCertificateInfo(certificate),
            Binding: new BindingInfo(bound.Subject, bound.Store, bound.Location, bound.AllowInvalid, certificate is not null),
            KeyAccess: EvaluateKeyAccess(certificate),
            PendingEnrollments: [.. PendingEnrollmentStore.List(apiOptions.RepoRoot).Select(ToPendingSummary)],
            Templates: templates,
            ExpiryWarningDays: certificateOptions.ExpiryWarningDays);
    }

    [SupportedOSPlatform("windows")]
    private IReadOnlyList<CertificateCandidate> GetCandidatesOnWindows() =>
        [.. CertificateStore.ListServerAuthCertificates(StoreLocation.LocalMachine).Select(ToCandidate)];

    [SupportedOSPlatform("windows")]
    private CertificateActionResult CreateSelfSignedOnWindows(IReadOnlyList<string> dnsNames, int? validityDays)
    {
        if (dnsNames.Count == 0)
            return CertificateActionResult.Failed("At least one DNS name is required.");

        CertificateSpec spec;
        try
        {
            spec = new CertificateSpec(dnsNames[0], dnsNames, validityDays is > 0 ? validityDays.Value : 397, "DbDataSync");
        }
        catch (ArgumentException ex)
        {
            return CertificateActionResult.Failed(ex.Message);
        }

        var certificate = CertificateBuilder.CreateSelfSigned(spec);
        var installed = CertificateStore.Install(certificate, StoreLocation.LocalMachine);
        GrantIfNeeded(installed);

        return CertificateActionResult.Ok(
            $"Issued and installed a self-signed certificate (thumbprint {installed.Thumbprint}). " +
            "Bind it below to put it into service.",
            requestId: null, GetStatusOnWindows());
    }

    [SupportedOSPlatform("windows")]
    private CertificateActionResult EnrollOnWindows(IReadOnlyList<string> dnsNames, string template, string? caConfigOverride)
    {
        if (dnsNames.Count == 0)
            return CertificateActionResult.Failed("At least one DNS name is required.");

        if (string.IsNullOrWhiteSpace(template))
        {
            return CertificateActionResult.Failed(
                "A template name is required — pick one from the list, or type a known template name.");
        }

        var config = DbDataSyncConfigFile.Read(apiOptions.RepoRoot);
        var caConfig = string.IsNullOrWhiteSpace(caConfigOverride)
            ? config.GetValueOrDefault("DbDataSync:Certificates:CaConfig")
            : caConfigOverride;
        if (string.IsNullOrWhiteSpace(caConfig))
        {
            return CertificateActionResult.Failed(
                "No CA is configured. Set DbDataSync:Certificates:CaConfig in dbdatasync.config.yaml first.");
        }

        CertificateSpec spec;
        try
        {
            // ValidityDays is unused on this path, same as CertCommand.Enroll — an AD CS template
            // decides how long an enrolled certificate is valid for, not this codebase.
            spec = new CertificateSpec(dnsNames[0], dnsNames, ValidityDays: 0, FriendlyName: "DbDataSync");
        }
        catch (ArgumentException ex)
        {
            return CertificateActionResult.Failed(ex.Message);
        }

        return SubmitAndHandle(caConfig, template, spec);
    }

    /// <summary>The shared middle of <see cref="EnrollOnWindows"/> and <see cref="RetrieveOnWindows"/> —
    /// build a CSR over a named, persisted key (or, for retrieve, reuse one already built), submit or
    /// collect it, and hand the disposition to <see cref="HandleEnrollmentResult"/>. Mirrors
    /// <c>CertCommand</c>'s own <c>SubmitAndHandle</c>/<c>HandleEnrollmentResult</c> split exactly — this
    /// is the same sequence, wrapped for HTTP instead of a console exit code.</summary>
    [SupportedOSPlatform("windows")]
    private CertificateActionResult SubmitAndHandle(string caConfig, string template, CertificateSpec spec)
    {
        var keyName = PendingEnrollmentKeys.NewKeyName();
        using var privateKey = PendingEnrollmentKeys.Create(keyName);
        var csrPem = CertificateBuilder.CreateSigningRequest(spec, privateKey);

        EnrollmentResult result;
        try
        {
            result = AdcsEnrollment.Submit(caConfig, template, csrPem);
        }
        catch (Exception ex)
        {
            PendingEnrollmentKeys.Delete(keyName);
            return CertificateActionResult.Failed($"Enrollment failed: {ex.Message}");
        }

        return HandleEnrollmentResult(result, keyName, spec, caConfig);
    }

    [SupportedOSPlatform("windows")]
    private CertificateActionResult HandleEnrollmentResult(
        EnrollmentResult result, string keyName, CertificateSpec spec, string caConfig)
    {
        switch (result.Outcome)
        {
            case EnrollmentOutcome.Issued:
            {
                var reopenedKey = PendingEnrollmentKeys.Open(keyName)
                    ?? throw new InvalidOperationException(
                        $"The private key for this enrollment ('{keyName}') could not be reopened.");
                X509Certificate2 installed;
                using (reopenedKey)
                {
                    var withKey = result.Certificate!.CopyWithPrivateKey(reopenedKey);
                    installed = CertificateStore.Install(withKey, StoreLocation.LocalMachine);
                }

                PendingEnrollmentKeys.Delete(keyName);
                PendingEnrollmentStore.Remove(apiOptions.RepoRoot, result.RequestId ?? "");
                GrantIfNeeded(installed);

                return CertificateActionResult.Ok(
                    $"Certificate issued and installed (thumbprint {installed.Thumbprint}). Bind it below " +
                    "to put it into service.",
                    requestId: null, GetStatusOnWindows());
            }

            case EnrollmentOutcome.Pending:
                // Saved so the screen can still offer "Retrieve pending request" after a browser reload
                // — see PendingEnrollmentStore.List's own doc comment for why that lookup exists at all.
                PendingEnrollmentStore.Save(apiOptions.RepoRoot, new PendingEnrollment(
                    result.RequestId!, keyName, spec.SubjectCommonName, spec.DnsNames, caConfig, DateTimeOffset.UtcNow));
                return CertificateActionResult.Ok(result.Message, result.RequestId, GetStatusOnWindows());

            default:
                PendingEnrollmentKeys.Delete(keyName);
                return CertificateActionResult.Failed(result.Message);
        }
    }

    [SupportedOSPlatform("windows")]
    private CertificateActionResult RetrieveOnWindows(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            return CertificateActionResult.Failed("A request id is required.");

        var pending = PendingEnrollmentStore.Find(apiOptions.RepoRoot, requestId);
        if (pending is null)
        {
            return CertificateActionResult.Failed(
                $"No pending enrollment recorded here for request id '{requestId}'. It may have been " +
                "submitted from a different machine or repo root.");
        }

        EnrollmentResult result;
        try
        {
            result = AdcsEnrollment.Retrieve(pending.CaConfig, requestId);
        }
        catch (Exception ex)
        {
            return CertificateActionResult.Failed($"Retrieval failed: {ex.Message}");
        }

        var spec = new CertificateSpec(pending.SubjectCommonName, pending.DnsNames, ValidityDays: 0, FriendlyName: "DbDataSync");
        return HandleEnrollmentResult(result, pending.KeyName, spec, pending.CaConfig);
    }

    [SupportedOSPlatform("windows")]
    private CertificateActionResult BindOnWindows(string thumbprint, bool? allowInvalidOverride, GitAuthor author)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
            return CertificateActionResult.Failed("A thumbprint is required.");

        var certificate = CertificateStore.FindByThumbprint(thumbprint, StoreLocation.LocalMachine);
        if (certificate is null)
            return CertificateActionResult.Failed($"No certificate with thumbprint '{thumbprint}' found in LocalMachine\\My.");

        // Same "report it, don't touch it" AllowInvalid resolution as CertCommand.Bind — see that
        // method's own comment for the reasoning (the phase 82 doc's own open question).
        var isSelfSigned = string.Equals(certificate.Subject, certificate.Issuer, StringComparison.Ordinal);
        var existing = CertificateBinding.Read(apiOptions.RepoRoot);
        var allowInvalid = allowInvalidOverride ?? (existing.Subject is null ? isSelfSigned : existing.AllowInvalid);

        CertificateBinding.Bind(apiOptions.RepoRoot, certificate, allowInvalid, git, author);

        return CertificateActionResult.Ok(
            $"Bound certificate '{thumbprint}' (subject '{CertificateBinding.SubjectCommonName(certificate)}'). " +
            "Nothing takes effect until the DbDataSync service restarts.",
            requestId: null, GetStatusOnWindows());
    }

    [SupportedOSPlatform("windows")]
    private void GrantIfNeeded(X509Certificate2 certificate)
    {
        var account = InstalledServiceAccount.Resolve(ServiceName);
        if (account is null || string.Equals(account, "LocalSystem", StringComparison.OrdinalIgnoreCase))
            return;

        PrivateKeyAccess.Grant(certificate, account);
    }

    [SupportedOSPlatform("windows")]
    private static KeyAccessInfo EvaluateKeyAccess(X509Certificate2? certificate) =>
        EvaluateKeyAccess(certificate, InstalledServiceAccount.Resolve(ServiceName));

    /// <summary>
    /// The pure half of the key-access check — pulled out, like <see cref="CertificateExpiryEvaluator"/>
    /// pulls the expiry decision out of <see cref="CertificateExpiryService"/>, so a test can drive every
    /// one of the three states (<c>Ok</c>, <c>Warning</c>, <c>Unknown</c>) directly rather than needing a
    /// real installed Windows service to produce an account name to test against. <c>internal</c> for
    /// exactly that — see <c>AdminCertificateServiceWindowsTests</c>.
    /// <para>
    /// **"Unknown" is the state that matters most to get right**, per the phase 83 doc: a host with no
    /// DbDataSync service installed genuinely cannot answer this question, and reporting <c>Ok</c> there
    /// (the way <c>CertCommand.Status</c>'s own <c>ResolveAccount(args) ?? "LocalSystem"</c> convenience
    /// does, for a CLI context where "assume LocalSystem" is a reasonable default to print) would be
    /// exactly the false green the doc warns about. This deliberately does not fall back to LocalSystem
    /// the way the CLI does — a real, disclosed divergence from that precedent, not an oversight.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static KeyAccessInfo EvaluateKeyAccess(X509Certificate2? certificate, string? account)
    {
        if (account is null)
        {
            return new KeyAccessInfo(KeyAccessState.Unknown, null,
                "No DbDataSync Windows service is installed on this host, so there is no service account to check.");
        }

        if (string.Equals(account, "LocalSystem", StringComparison.OrdinalIgnoreCase))
        {
            return new KeyAccessInfo(KeyAccessState.Ok, account,
                "LocalSystem always has access to every LocalMachine\\My private key.");
        }

        if (certificate is null)
        {
            return new KeyAccessInfo(KeyAccessState.Unknown, account,
                "No certificate is currently bound, so there is no private key to check.");
        }

        return PrivateKeyAccess.CanRead(certificate, account)
            ? new KeyAccessInfo(KeyAccessState.Ok, account, null)
            : new KeyAccessInfo(KeyAccessState.Warning, account,
                $"'{account}' cannot read the private key. The next service restart will fail its TLS handshake.");
    }

    private static CurrentCertificateInfo ToCurrentCertificateInfo(X509Certificate2 certificate)
    {
        var info = CertificateInfo.From(certificate);
        return new CurrentCertificateInfo(
            info.Thumbprint,
            info.SubjectCommonName,
            info.DnsNames,
            certificate.Issuer,
            info.NotBefore,
            info.NotAfter,
            info.DaysRemaining(DateTimeOffset.UtcNow),
            SelfSigned: string.Equals(certificate.Subject, certificate.Issuer, StringComparison.Ordinal));
    }

    private static CertificateCandidate ToCandidate(X509Certificate2 certificate)
    {
        var info = CertificateInfo.From(certificate);
        return new CertificateCandidate(
            info.Thumbprint, info.SubjectCommonName, info.DnsNames, info.NotAfter,
            info.DaysRemaining(DateTimeOffset.UtcNow),
            SelfSigned: string.Equals(certificate.Subject, certificate.Issuer, StringComparison.Ordinal));
    }

    private static PendingEnrollmentSummary ToPendingSummary(PendingEnrollment pending) =>
        new(pending.RequestId, pending.SubjectCommonName, pending.DnsNames, pending.SubmittedAtUtc);

    private static StoreLocation ParseLocation(string value) =>
        Enum.TryParse<StoreLocation>(value, ignoreCase: true, out var location) ? location : StoreLocation.LocalMachine;
}

/// <param name="Available">False on a non-Windows host — see <see cref="UnavailableReason"/>, the only
/// other field set in that case.</param>
/// <param name="Certificate">Null when nothing is bound, or the bound subject has no matching
/// certificate in the store (see <see cref="Binding"/>'s own <c>CertificateFound</c>).</param>
/// <param name="ExpiryWarningDays">The same threshold <c>CertificateExpiryService</c>'s daily check uses
/// (<c>DbDataSync:Certificates:ExpiryWarningDays</c>) — sent along so the SPA colours "days remaining" by
/// the same window that actually raises a notification, rather than guessing a different number.</param>
public sealed record AdminCertificateStatus(
    bool Available,
    string? UnavailableReason,
    CurrentCertificateInfo? Certificate,
    BindingInfo? Binding,
    KeyAccessInfo? KeyAccess,
    IReadOnlyList<PendingEnrollmentSummary> PendingEnrollments,
    TemplateListResult? Templates,
    int ExpiryWarningDays = 30)
{
    public static AdminCertificateStatus Unavailable(string reason) => new(false, reason, null, null, null, [], null);
}

public sealed record CurrentCertificateInfo(
    string Thumbprint,
    string SubjectCommonName,
    IReadOnlyList<string> DnsNames,
    string Issuer,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    int DaysRemaining,
    bool SelfSigned);

/// <param name="Subject">Null when nothing has ever been bound.</param>
/// <param name="CertificateFound">False when <paramref name="Subject"/> names a certificate that is no
/// longer in the store — the same "binds a subject, but nothing matches it there" case
/// <c>dbdatasync cert status</c> reports as an error.</param>
public sealed record BindingInfo(string? Subject, string Store, string Location, bool AllowInvalid, bool CertificateFound);

public enum KeyAccessState { Ok, Warning, Unknown }

/// <param name="Account">Null only when <see cref="KeyAccessState.Unknown"/> because no service is
/// installed at all — every other state, including the other <c>Unknown</c> case (no certificate to
/// check), has a real account name.</param>
public sealed record KeyAccessInfo(KeyAccessState State, string? Account, string? Detail);

public sealed record PendingEnrollmentSummary(
    string RequestId, string SubjectCommonName, IReadOnlyList<string> DnsNames, DateTimeOffset SubmittedAtUtc);

public sealed record CertificateCandidate(
    string Thumbprint, string SubjectCommonName, IReadOnlyList<string> DnsNames,
    DateTimeOffset NotAfter, int DaysRemaining, bool SelfSigned);

/// <param name="Succeeded">False only for a genuine failure (denied, no CA configured, no such
/// thumbprint, …) — the controller turns that into a 400 with <see cref="Message"/> as the error. An
/// enrollment that came back pending is still <c>true</c>: it is not an error, it is the expected shape
/// for a template requiring approval.</param>
/// <param name="RequestId">Set only when an enrollment came back pending — what the operator needs to
/// paste into "Retrieve pending request" later, though the screen also lists it from
/// <see cref="AdminCertificateStatus.PendingEnrollments"/> without anyone having to remember it.</param>
/// <param name="Status">The refreshed <see cref="AdminCertificateStatus"/> after this action, on
/// success — saves the SPA a second round trip. Null on failure, since nothing changed.</param>
public sealed record CertificateActionResult(bool Succeeded, string Message, string? RequestId, AdminCertificateStatus? Status)
{
    public static CertificateActionResult Ok(string message, string? requestId, AdminCertificateStatus status) =>
        new(true, message, requestId, status);

    public static CertificateActionResult Failed(string message) => new(false, message, null, null);
}
