using System.Security.Cryptography.X509Certificates;

namespace DbDataSync.Certificates;

/// <summary>The shape <c>dbdatasync config cert status</c> and <c>dbdatasync config cert list</c> both report —
/// everything about a certificate that matters to an operator deciding whether it's the right one or
/// whether it's about to be a problem.</summary>
public sealed record CertificateInfo(
    string Thumbprint,
    string SubjectCommonName,
    IReadOnlyList<string> DnsNames,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    string? FriendlyName)
{
    public int DaysRemaining(DateTimeOffset now) => (int)Math.Floor((NotAfter - now).TotalDays);

    /// <summary>Cross-platform — see <see cref="CertificateSanReader"/>'s own doc comment for why
    /// reading a certificate's own fields needs no Windows API, even though finding the certificate in
    /// the first place (<see cref="CertificateStore"/>) does.</summary>
    public static CertificateInfo From(X509Certificate2 certificate) => new(
        certificate.Thumbprint ?? "",
        certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false),
        CertificateSanReader.GetDnsNames(certificate),
        certificate.NotBefore,
        certificate.NotAfter,
        string.IsNullOrEmpty(certificate.FriendlyName) ? null : certificate.FriendlyName);
}
