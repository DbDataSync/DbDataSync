using System.Security.Cryptography.X509Certificates;

namespace DbDataSync.Certificates;

/// <summary>
/// Phase 130 — tier 2's cross-platform self-signed certificate: generation, storage, and the renewal
/// decision, shared by <c>dbdatasync config cert new-self-signed</c>'s non-Windows branch (the CLI),
/// <c>SelfSignedCertificateService</c> (the daily renewal check), and <c>CertificateCheck</c> (the
/// marker that distinguishes a certificate this codebase manages from one bound by accident).
/// <para>
/// **One well-known path, deliberately.** Everything here keys off <see cref="PfxPath"/> — the same
/// file every caller reads, writes, or compares <c>Kestrel:Certificates:Default:Path</c> against — so
/// there is exactly one place "is this the managed certificate" can ever disagree with itself.
/// </para>
/// </summary>
public static class ManagedSelfSignedCertificate
{
    private const string RelativeDirectory = "tls";
    private const string FileName = "dbdatasync.pfx";

    /// <summary>Full, normalized path to the managed certificate for a given repo root. Every caller
    /// passes whatever repo root it already resolved (a CLI's <c>--repo</c>, the API's
    /// <c>ApiOptions.RepoRoot</c>) through this rather than building the path itself, so a difference in
    /// path separators or a trailing slash can never make two callers disagree about whether a
    /// configured <c>Path</c> is this one.</summary>
    public static string PfxPath(string repoRoot) =>
        Path.GetFullPath(Path.Combine(repoRoot, RelativeDirectory, FileName));

    /// <summary>
    /// Builds the certificate — the identical keypair/SAN shape <see cref="CertificateBuilder.CreateSelfSigned"/>
    /// already produces for the Windows store path, just handed to <see cref="Write"/> instead of
    /// <c>CertificateStore.Install</c>. <paramref name="host"/> is deduplicated against <c>localhost</c>,
    /// not appended blindly — the phase 130 doc's "the console URL's host, plus localhost."
    /// </summary>
    public static X509Certificate2 Generate(string host, int validityDays)
    {
        var dnsNames = new List<string> { host };
        if (!dnsNames.Contains("localhost", StringComparer.OrdinalIgnoreCase))
            dnsNames.Add("localhost");

        var spec = new CertificateSpec(host, dnsNames, validityDays, FriendlyName: null);
        return CertificateBuilder.CreateSelfSigned(spec);
    }

    /// <summary>
    /// Writes <paramref name="certificate"/> to <see cref="PfxPath"/>, unencrypted (matching phase 113's
    /// own supported case — nothing in this codebase splices a PFX password), <c>0600</c> on any
    /// non-Windows OS, and ensures the containing directory is git-ignored the first time it's used —
    /// the same lazy, first-use pattern <see cref="PendingEnrollmentStore"/> already established for a
    /// file this codebase deliberately never commits.
    /// </summary>
    public static void Write(string repoRoot, X509Certificate2 certificate)
    {
        var path = PfxPath(repoRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx));

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        EnsureGitignored(repoRoot);
    }

    /// <summary>
    /// The renewal decision — pure, so it needs no certificate, no file, no clock beyond what is passed
    /// in (the same shape <see cref="CertificateExpiryEvaluator"/> already established for the Windows
    /// store path). Renews within a third of the certificate's own lifetime of expiring, matching tier
    /// 3's own planned threshold for consistency — see the phase 130 doc's "What this builds," #3.
    /// </summary>
    public static bool ShouldRenew(DateTimeOffset notBefore, DateTimeOffset notAfter, DateTimeOffset now) =>
        notAfter - now <= (notAfter - notBefore) / 3;

    /// <summary>
    /// The repo root is a git repository (<c>ServeCommand.Prepare</c>), and this directory is
    /// deliberately never committed to it — a private key has no business in version control. Appended
    /// once, the first time a managed certificate is actually written, rather than by
    /// <c>ServeCommand.Prepare</c> up front — a repo that never generates one never needs the line.
    /// </summary>
    private static void EnsureGitignored(string repoRoot)
    {
        const string entry = $"{RelativeDirectory}/";
        var path = Path.Combine(repoRoot, ".gitignore");
        var existing = File.Exists(path) ? File.ReadAllLines(path) : [];
        if (existing.Contains(entry))
            return;

        using var writer = new StreamWriter(path, append: true);
        if (existing.Length > 0 && !string.IsNullOrEmpty(existing[^1]))
            writer.WriteLine();
        writer.WriteLine(entry);
    }
}
