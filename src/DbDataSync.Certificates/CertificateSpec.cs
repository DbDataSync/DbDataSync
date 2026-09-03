namespace DbDataSync.Certificates;

/// <summary>
/// What to ask for, whether the certificate that comes back is self-signed or issued by an enterprise
/// CA — see <see cref="CertificateBuilder"/>, which both issuance paths build their request from.
/// </summary>
/// <param name="SubjectCommonName">Becomes the certificate's <c>CN=</c>. Not itself required to be one
/// of <paramref name="DnsNames"/> — a browser never looks at the CN, only the SAN list — but every
/// caller in this codebase passes the same value for both, which is the ordinary shape.</param>
/// <param name="DnsNames">Never empty — see the validation on the property below.</param>
/// <param name="ValidityDays">How long the certificate is valid for, starting a few minutes before now
/// (to absorb clock skew between this machine and whatever validates the certificate first).</param>
/// <param name="FriendlyName">The Windows-store-only display name (<c>X509Certificate2.FriendlyName</c>).
/// Null leaves it unset, which every certificate not created by this codebase already looks like.</param>
public sealed record CertificateSpec(
    string SubjectCommonName,
    IReadOnlyList<string> DnsNames,
    int ValidityDays,
    string? FriendlyName)
{
    /// <summary>
    /// Redeclared over the positional parameter so construction validates it, rather than only the
    /// call sites that remember to check.
    /// <para>
    /// **Rejected here, not left to fail at the browser.** A certificate with only a CN and no Subject
    /// Alternative Name installs cleanly and binds cleanly — Kestrel does not look at SAN content, only
    /// that a certificate exists — and then fails silently in every current browser, which stopped
    /// falling back to the CN years ago. The failure has to happen at the one point someone can still
    /// do something about it, which is here, before anything is issued.
    /// </para>
    /// <para>
    /// **Construction only, not a <c>with</c> expression.** A record's compiler-generated copy
    /// constructor (what <c>with</c> uses) assigns backing fields directly and does not re-run an
    /// auto-property's initializer, so <c>spec with { DnsNames = [] }</c> does not throw. Nothing in
    /// this codebase uses <c>with</c> on a <see cref="CertificateSpec"/>, so this is stated here as a
    /// known limit rather than worked around with a hand-written init accessor for a case that does not
    /// arise.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> DnsNames { get; init; } = DnsNames.Count > 0
        ? DnsNames
        : throw new ArgumentException(
            "A certificate needs at least one DNS name for its Subject Alternative Name. A CN alone " +
            "is not enough — every current browser (Chrome removed CN fallback years ago) rejects a " +
            "server certificate with no SAN entries, regardless of what the CN says.",
            nameof(DnsNames));

    /// <summary>The well-known server-authentication EKU OID — without it Windows will not offer a
    /// certificate for TLS server use, and Kestrel would refuse to serve it.</summary>
    public const string ServerAuthenticationEku = "1.3.6.1.5.5.7.3.1";

    /// <summary>RSA-2048, the default for both issuance paths — recorded as a constant so
    /// <see cref="CertificateBuilder"/> and every test asserting a key size agree on the same number.</summary>
    public const int DefaultKeySizeBits = 2048;
}
