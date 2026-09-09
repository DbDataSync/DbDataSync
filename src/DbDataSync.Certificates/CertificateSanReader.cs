using System.Formats.Asn1;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace DbDataSync.Certificates;

/// <summary>
/// Reads the DNS names back out of a certificate's Subject Alternative Name extension. .NET has a
/// builder for this extension (<see cref="SubjectAlternativeNameBuilder"/>, used by
/// <see cref="CertificateBuilder"/>) but no typed reader, so this decodes the extension's DER bytes
/// directly — the doc's own unit test ("a self-signed certificate carries every requested DNS name as a
/// SAN") needs exactly this to assert against.
/// <para>
/// **Cross-platform, deliberately.** ASN.1 decoding is not an OS API; the only reason this lives beside
/// Windows-only code is that every caller of it (<c>dbdatasync config cert status</c>/<c>list</c>, the daily
/// expiry check) happens to be working with a certificate that came from the Windows store.
/// </para>
/// </summary>
public static class CertificateSanReader
{
    private const string SubjectAlternativeNameOid = "2.5.29.17";

    /// <summary>The dNSName entries of the SAN extension, in the order they appear — empty if the
    /// certificate has no SAN extension at all (which <see cref="CertificateSpec"/> already refuses to
    /// let this codebase's own issuance produce, but an externally-created certificate could still have
    /// none).</summary>
    public static IReadOnlyList<string> GetDnsNames(X509Certificate2 certificate)
    {
        var extension = certificate.Extensions[SubjectAlternativeNameOid];
        if (extension is null)
            return [];

        var names = new List<string>();
        var sequence = new AsnReader(extension.RawData, AsnEncodingRules.DER).ReadSequence();

        while (sequence.HasData)
        {
            var tag = sequence.PeekTag();

            // GeneralName ::= CHOICE { ..., dNSName [2] IA5String, ... } — an IMPLICIT tag, so on the
            // wire it is a plain length-prefixed byte string under context tag 2, which ReadOctetString
            // with an explicit expected tag reads correctly (IA5String content is already ASCII bytes;
            // there is no further structure to decode).
            if (tag.TagClass == TagClass.ContextSpecific && tag.TagValue == 2)
            {
                var bytes = sequence.ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 2));
                names.Add(Encoding.ASCII.GetString(bytes));
            }
            else
            {
                // Every other GeneralName choice (rfc822Name, iPAddress, ...) — skipped; this codebase
                // never issues one and only wants dNSName entries back.
                sequence.ReadEncodedValue();
            }
        }

        return names;
    }
}
