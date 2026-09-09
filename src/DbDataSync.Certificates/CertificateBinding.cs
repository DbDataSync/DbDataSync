using System.Security.Cryptography.X509Certificates;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;

namespace DbDataSync.Certificates;

/// <summary>
/// Writes and reads the four <c>Kestrel:Certificates:Default:*</c> keys — a store lookup, not a PFX
/// path, so Kestrel resolves the certificate the same way IIS or a reverse proxy in front of the app
/// would, and so **no certificate password is ever persisted by DbDataSync**: the private key never
/// leaves the Windows store. See the phase 82 doc's "Decisions taken before implementation."
/// <para>
/// **Not marked <c>[SupportedOSPlatform("windows")]</c>.** Writing four YAML keys and reading them back
/// touches no Windows API at all — the OS-specific part is finding the certificate to bind in the first
/// place (<see cref="CertificateStore"/>), which every caller does before calling in here. Keeping this
/// class portable is also what makes <c>bind writes the four Kestrel keys into a temp
/// dbdatasync.config.yaml</c> (the phase 82 doc's own Windows-only test bullet) not actually need to be
/// Windows-only itself — it is listed there because the doc grouped it with the store-touching tests
/// it's usually run alongside, not because this class needs Windows to run.
/// </para>
/// <para>
/// **One section, three literal colons in its name.** <see cref="DbDataSyncConfigFile.SetValue"/> writes
/// a flat, two-level shape only (<c>Section:</c> then indented <c>Key: value</c> lines) — phase 79 and
/// 81 both left arbitrary YAML nesting out of its write side on purpose. Passing the whole dotted path
/// <c>"Kestrel:Certificates:Default"</c> as <paramref name="Section"/> stays inside that two-level
/// shape (one section header, four keys under it) while still producing a YAML document that <see
/// cref="DbDataSyncConfigFile.Read"/>'s flattener turns back into exactly
/// <c>Kestrel:Certificates:Default:Subject</c> and its three siblings — the same keys ASP.NET Core's own
/// configuration binder expects — because <c>Flatten</c> only ever concatenates whatever prefix it is
/// given with a colon; it does not care whether that prefix already contains colons of its own. Proven
/// by <c>CertificateBindingTests</c> round-tripping <see cref="Bind"/> through
/// <see cref="DbDataSyncConfigFile.Read"/>.
/// </para>
/// </summary>
public static class CertificateBinding
{
    public const string Section = "Kestrel:Certificates:Default";

    /// <summary>
    /// The Subject Kestrel is told to look for. Kestrel's own certificate resolver does a subject-name
    /// store lookup, which matches against the certificate's CN — not the full X.500 distinguished
    /// name — so this is <see cref="X509Certificate2.GetNameInfo"/> asking for the simple name, not
    /// <see cref="X509Certificate2.Subject"/> (which would be <c>CN=host, O=Org, …</c> and would not
    /// match).
    /// </summary>
    public static string SubjectCommonName(X509Certificate2 certificate) =>
        certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);

    /// <summary>
    /// Writes the four keys and commits the write — <c>ServeCommand.Prepare</c>'s own two-step pattern
    /// (<c>SetValue</c> then <c>new GitCommitService(root).CommitChanges(...)</c>), so a certificate
    /// binding is git-tracked and attributed like every other config write rather than appearing as an
    /// untracked local edit.
    /// <para>
    /// <paramref name="allowInvalid"/> is the phase 82 doc's own point: <c>true</c> is genuinely
    /// required for a self-signed certificate (Kestrel validates the chain on load and refuses one it
    /// cannot build), and writing it silently would change a security-relevant setting nobody asked
    /// about in the moment — the caller states it, and the CLI prints why.
    /// </para>
    /// <para>
    /// **Nothing takes effect until the service restarts.** This method does not attempt to and never
    /// will — <c>ApiOptions</c> and Kestrel's own certificate are both resolved once at startup, and a
    /// restart interrupts in-flight replication runs, which is the operator's call to make, not this
    /// method's.
    /// </para>
    /// </summary>
    public static void Bind(string repoRoot, X509Certificate2 certificate, bool allowInvalid, GitCommitService gitCommitService, GitAuthor author)
    {
        var subject = SubjectCommonName(certificate);

        // Phase 113 added a second, file-based shape (Path/KeyPath) in this same section. Binding a
        // store-based certificate must drop those, or Kestrel's own certificate loader has both a
        // Subject and a Path to reconcile — undocumented, and not a state this codebase should ever
        // deliberately produce.
        DbDataSyncConfigFile.RemoveValue(repoRoot, Section, "Path");
        DbDataSyncConfigFile.RemoveValue(repoRoot, Section, "KeyPath");

        DbDataSyncConfigFile.SetValue(repoRoot, Section, "Subject", subject);
        DbDataSyncConfigFile.SetValue(repoRoot, Section, "Store", "My");
        DbDataSyncConfigFile.SetValue(repoRoot, Section, "Location", "LocalMachine");
        DbDataSyncConfigFile.SetValue(repoRoot, Section, "AllowInvalid", allowInvalid ? "true" : "false");

        gitCommitService.CommitChanges(
            [DbDataSyncConfigFile.PathIn(repoRoot)],
            $"Bind certificate '{certificate.Thumbprint}' (subject '{subject}') for Kestrel",
            author);
    }

    /// <summary>What is currently bound, straight off the file — null fields where nothing has ever
    /// been bound (a fresh repo root, or one configured entirely by environment variable/CLI flag
    /// instead of this file).</summary>
    public static BoundCertificateConfig Read(string repoRoot)
    {
        var values = DbDataSyncConfigFile.Read(repoRoot);
        return new BoundCertificateConfig(
            values.GetValueOrDefault($"{Section}:Subject"),
            values.GetValueOrDefault($"{Section}:Store") ?? "My",
            values.GetValueOrDefault($"{Section}:Location") ?? "LocalMachine",
            string.Equals(values.GetValueOrDefault($"{Section}:AllowInvalid"), "true", StringComparison.OrdinalIgnoreCase));
    }
}

/// <param name="Subject">Null when nothing is bound yet.</param>
public sealed record BoundCertificateConfig(string? Subject, string Store, string Location, bool AllowInvalid);
