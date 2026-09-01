using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace DataSync.Certificates;

/// <summary>
/// The Windows certificate store — install, list, find. Every member here is genuinely Windows-only
/// (<see cref="X509Store"/> off Windows throws <see cref="PlatformNotSupportedException"/> the moment
/// it is opened), so every one is marked and every caller in this codebase checks
/// <see cref="OperatingSystem.IsWindows"/> first — see <c>CertCommand</c> and
/// <c>CertificateExpiryService</c>, which are the only two.
/// </summary>
[SupportedOSPlatform("windows")]
public static class CertificateStore
{
    /// <summary>
    /// Installs a certificate into <c>&lt;location&gt;\My</c>, private key non-exportable.
    /// <para>
    /// **Round-trips through a PFX**, not a direct <c>store.Add(certificate)</c>. A certificate fresh
    /// off <see cref="CertificateBuilder.CreateSelfSigned"/> (or returned from an AD CS submission) has
    /// an *ephemeral* private key — never written to a real CNG key container — and adding it to a
    /// store as-is puts the certificate there with no private key <see cref="PrivateKeyAccess.Grant"/>
    /// or a future <c>X509Certificate2.GetRSAPrivateKey()</c>-then-open could ever find on disk. The
    /// export/import round trip is what actually persists the key: <see
    /// cref="X509KeyStorageFlags.PersistKeySet"/> tells CNG to write a real key container, and the
    /// deliberate *absence* of <see cref="X509KeyStorageFlags.Exportable"/> is the phase 82 doc's
    /// "non-exportable private keys" decision — nothing in this feature exports one, and this is the
    /// one place that could.
    /// </para>
    /// <para>
    /// The PFX password is a fresh random GUID that exists only for the instant between
    /// <see cref="X509Certificate2.Export"/> and <see cref="X509CertificateLoader.LoadPkcs12(byte[],string?,X509KeyStorageFlags)"/>
    /// below — never returned, never logged, never the certificate's actual protection (the store ACL
    /// and, after install, <see cref="PrivateKeyAccess"/> are).
    /// </para>
    /// </summary>
    public static X509Certificate2 Install(X509Certificate2 certificate, StoreLocation location)
    {
        var exportPassword = Guid.NewGuid().ToString("N");
        var pfxBytes = certificate.Export(X509ContentType.Pfx, exportPassword);

        var flags = X509KeyStorageFlags.PersistKeySet
            | (location == StoreLocation.LocalMachine ? X509KeyStorageFlags.MachineKeySet : X509KeyStorageFlags.UserKeySet);

        var persisted = X509CertificateLoader.LoadPkcs12(pfxBytes, exportPassword, flags);

        using var store = new X509Store(StoreName.My, location);
        store.Open(OpenFlags.ReadWrite);
        store.Add(persisted);

        return persisted;
    }

    /// <summary>Every certificate in <c>&lt;location&gt;\My</c> whose Enhanced Key Usage includes
    /// server authentication — the ones <c>datasync cert list</c> shows, since a certificate without
    /// this EKU cannot be bound for TLS regardless of what else is in the store.</summary>
    public static IReadOnlyList<X509Certificate2> ListServerAuthCertificates(StoreLocation location)
    {
        using var store = new X509Store(StoreName.My, location);
        store.Open(OpenFlags.ReadOnly);

        return [.. store.Certificates.Where(HasServerAuthEku)];
    }

    public static X509Certificate2? FindByThumbprint(string thumbprint, StoreLocation location)
    {
        using var store = new X509Store(StoreName.My, location);
        store.Open(OpenFlags.ReadOnly);

        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        return matches.Count > 0 ? matches[0] : null;
    }

    /// <summary>
    /// The certificate a subject-name lookup finds — how Kestrel itself resolves
    /// <c>Kestrel:Certificates:Default:Subject</c>, and so how <c>datasync cert status</c> and
    /// <see cref="CertificateExpiryService"/> find the certificate that is actually bound. When more
    /// than one certificate shares the subject (a renewal that has not yet replaced the old one in
    /// config), the one with the latest <c>NotAfter</c> wins — the same "prefer the newest" rule
    /// Kestrel's own certificate resolver applies.
    /// </summary>
    public static X509Certificate2? FindBySubject(string subjectCommonName, StoreLocation location)
    {
        using var store = new X509Store(StoreName.My, location);
        store.Open(OpenFlags.ReadOnly);

        var matches = store.Certificates.Find(
            X509FindType.FindBySubjectName, subjectCommonName, validOnly: false);

        return matches.Count == 0
            ? null
            : matches.OrderByDescending(c => c.NotAfter).First();
    }

    private static bool HasServerAuthEku(X509Certificate2 certificate)
    {
        foreach (var extension in certificate.Extensions)
        {
            if (extension is not X509EnhancedKeyUsageExtension eku)
                continue;

            foreach (var oid in eku.EnhancedKeyUsages)
            {
                if (oid.Value == CertificateSpec.ServerAuthenticationEku)
                    return true;
            }
        }

        return false;
    }
}
