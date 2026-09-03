using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DbDataSync.Certificates;

/// <summary>
/// Builds the request both issuance paths share — see the phase 82 doc's "Issuance — two paths, one
/// shared spine." A self-signed certificate and a CSR differ only in what happens to the
/// <see cref="CertificateRequest"/> after this returns it: <see cref="CreateSelfSigned"/> signs it with
/// its own key immediately, <see cref="CreateSigningRequest"/> exports it unsigned for an enterprise CA
/// to sign instead.
/// <para>
/// **Not marked <c>[SupportedOSPlatform("windows")]</c>, unlike the rest of this project.**
/// <see cref="System.Security.Cryptography.CertificateRequest"/>, <c>RSA.Create</c> and the extension
/// types below are genuine cross-platform .NET cryptography (backed by OpenSSL off Windows) — nothing
/// here touches the Windows certificate store, CNG key storage, or COM. That is exactly why the phase
/// 82 doc's own unit-test list calls out "a self-signed certificate carries every requested DNS name,"
/// "the CSR round-trips subject and SANs" as tests that must run anywhere: they exercise this class,
/// and this class has nothing platform-specific to gate.
/// </para>
/// </summary>
public static class CertificateBuilder
{
    /// <summary>
    /// A self-signed, in-memory certificate. Its private key is ephemeral until
    /// <see cref="CertificateStore.Install"/> persists it into a real store — see that method's own
    /// doc comment for why that round trip is necessary before <see cref="PrivateKeyAccess.Grant"/> can
    /// do anything with it.
    /// </summary>
    public static X509Certificate2 CreateSelfSigned(CertificateSpec spec)
    {
        using var rsa = RSA.Create(CertificateSpec.DefaultKeySizeBits);
        var request = BuildRequest(spec, rsa);

        // A few minutes in the past, not "now" exactly — clock skew between this machine and whatever
        // validates the certificate first (a browser, another server) is real and small; starting
        // slightly early absorbs it instead of handing back a certificate that is technically not yet
        // valid the moment it is issued.
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = notBefore.AddDays(spec.ValidityDays);

        var certificate = request.CreateSelfSigned(notBefore, notAfter);
        if (spec.FriendlyName is { } friendlyName && OperatingSystem.IsWindows())
            certificate.FriendlyName = friendlyName;

        return certificate;
    }

    /// <summary>
    /// A PKCS#10 certificate signing request, PEM-encoded, over the caller-supplied key — the caller
    /// keeps <paramref name="rsa"/> around to pair with whatever certificate the CA eventually returns,
    /// since the CSR itself carries only the public half.
    /// <para>
    /// **Takes the key rather than creating one**, unlike <see cref="CreateSelfSigned"/> — a CSR that
    /// might come back <c>CR_DISP_UNDER_SUBMISSION</c> (pending) needs its key to still exist, unchanged,
    /// whenever <c>dbdatasync cert retrieve</c> eventually collects it, which could be minutes or days and
    /// a different process invocation later. An ephemeral in-memory key (what this method used before)
    /// cannot survive that; <c>CertCommand.Enroll</c> is the real caller and passes a named, persisted
    /// CNG key (see <c>PendingEnrollmentKeys</c>) for exactly that reason. Tests pass an ordinary
    /// ephemeral <c>RSA.Create(...)</c> instead, which is what keeps this method itself cross-platform
    /// and testable without Windows.
    /// </para>
    /// </summary>
    public static string CreateSigningRequest(CertificateSpec spec, RSA rsa)
    {
        var request = BuildRequest(spec, rsa);
        var derBytes = request.CreateSigningRequest();
        var pemChars = PemEncoding.Write("CERTIFICATE REQUEST", derBytes);
        return new string(pemChars);
    }

    /// <summary>
    /// The part both issuance paths share: subject, every requested SAN, the key-usage flags a TLS
    /// server certificate needs, and the server-authentication EKU — all built from
    /// <see cref="CertificateSpec"/> alone, with no distinction yet between "sign it yourself" and
    /// "hand it to a CA."
    /// </summary>
    private static CertificateRequest BuildRequest(CertificateSpec spec, RSA rsa)
    {
        var request = new CertificateRequest(
            $"CN={spec.SubjectCommonName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var sanBuilder = new SubjectAlternativeNameBuilder();
        foreach (var dnsName in AllDnsNames(spec))
            sanBuilder.AddDnsName(dnsName);
        request.CertificateExtensions.Add(sanBuilder.Build());

        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));

        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid(CertificateSpec.ServerAuthenticationEku)], critical: false));

        return request;
    }

    /// <summary>
    /// Every DNS name the spec asked for, plus this machine's own hostname and FQDN if either is not
    /// already in the list — the phase 82 doc's "and the machine's own FQDN and hostname by default."
    /// A server reached both by a short name on the local network and by its full domain name should
    /// not have to ask for both explicitly.
    /// </summary>
    internal static IReadOnlyList<string> AllDnsNames(CertificateSpec spec)
    {
        var names = new List<string>(spec.DnsNames);

        var hostname = Environment.MachineName;
        if (!names.Contains(hostname, StringComparer.OrdinalIgnoreCase))
            names.Add(hostname);

        // DNS resolution can fail for all sorts of ordinary reasons (no network, an isolated test
        // sandbox, a machine that has never been joined to a domain) — none of which should stop
        // certificate issuance over one optional convenience name.
        try
        {
            var fqdn = System.Net.Dns.GetHostEntry(hostname).HostName;
            if (!string.IsNullOrWhiteSpace(fqdn) && !names.Contains(fqdn, StringComparer.OrdinalIgnoreCase))
                names.Add(fqdn);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException)
        {
            // No FQDN to add; the explicitly requested names and the bare hostname still stand.
        }

        return names;
    }
}
