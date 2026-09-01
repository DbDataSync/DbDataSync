using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DataSync.Certificates.Tests;

/// <summary>Covers the doc's own unit-test bullets for issuance: "a self-signed certificate carries
/// every requested DNS name as a SAN, the server-auth EKU, and the requested validity" and "the CSR
/// round-trips subject and SANs." Both <see cref="CertificateBuilder.CreateSelfSigned"/> and
/// <see cref="CertificateBuilder.CreateSigningRequest"/> are cross-platform .NET cryptography — see
/// that class's own doc comment — so this test class runs on any OS, matching the doc's "runnable
/// anywhere" requirement for these tests.</summary>
public sealed class CertificateBuilderTests
{
    [Fact]
    public void SelfSigned_CarriesEveryRequestedDnsNameAsASan()
    {
        var spec = new CertificateSpec("datasync.example.com", ["datasync.example.com", "datasync"], 365, null);
        using var certificate = CertificateBuilder.CreateSelfSigned(spec);

        var sanNames = CertificateSanReader.GetDnsNames(certificate);
        Assert.Contains("datasync.example.com", sanNames);
        Assert.Contains("datasync", sanNames);
    }

    [Fact]
    public void SelfSigned_AlsoCarriesTheMachinesOwnHostname()
    {
        var spec = new CertificateSpec("datasync.example.com", ["datasync.example.com"], 365, null);
        using var certificate = CertificateBuilder.CreateSelfSigned(spec);

        var sanNames = CertificateSanReader.GetDnsNames(certificate);
        Assert.Contains(Environment.MachineName, sanNames, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelfSigned_CarriesTheServerAuthenticationEku()
    {
        var spec = new CertificateSpec("datasync.example.com", ["datasync.example.com"], 365, null);
        using var certificate = CertificateBuilder.CreateSelfSigned(spec);

        var eku = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single();
        Assert.Contains(eku.EnhancedKeyUsages.Cast<Oid>(), oid => oid.Value == CertificateSpec.ServerAuthenticationEku);
    }

    [Fact]
    public void SelfSigned_CarriesDigitalSignatureAndKeyEnciphermentKeyUsage()
    {
        var spec = new CertificateSpec("datasync.example.com", ["datasync.example.com"], 365, null);
        using var certificate = CertificateBuilder.CreateSelfSigned(spec);

        var keyUsage = certificate.Extensions.OfType<X509KeyUsageExtension>().Single();
        Assert.Equal(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            keyUsage.KeyUsages & (X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment));
    }

    [Fact]
    public void SelfSigned_HonoursTheRequestedValidityPeriod()
    {
        var spec = new CertificateSpec("datasync.example.com", ["datasync.example.com"], 90, null);
        using var certificate = CertificateBuilder.CreateSelfSigned(spec);

        var validityDays = (certificate.NotAfter - certificate.NotBefore).TotalDays;
        Assert.InRange(validityDays, 89.9, 90.1);
    }

    [Fact]
    public void Csr_RoundTripsSubjectAndSans()
    {
        var spec = new CertificateSpec("datasync.example.com", ["datasync.example.com", "datasync"], 365, null);
        using var rsa = RSA.Create(CertificateSpec.DefaultKeySizeBits);

        var pem = CertificateBuilder.CreateSigningRequest(spec, rsa);

        // UnsafeLoadCertificateExtensions: without it, CertificateExtensions comes back empty by
        // design — a CSR's requested extensions are attacker-controlled, unverified content, and .NET
        // makes an application ask explicitly before trusting them. Reading them back here to prove
        // the round trip is exactly that deliberate, informed ask; nothing here treats them as verified.
        var loaded = CertificateRequest.LoadSigningRequestPem(
            pem, HashAlgorithmName.SHA256,
            CertificateRequestLoadOptions.SkipSignatureValidation | CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);

        Assert.Equal("CN=datasync.example.com", loaded.SubjectName.Name);

        var sanExtension = loaded.CertificateExtensions.Single(e => e.Oid?.Value == "2.5.29.17");
        // Reuse the certificate-side SAN parser's own decoding logic — the DER encoding is identical
        // whether it came off an X509Certificate2's extension or a CertificateRequest's.
        Assert.Contains("datasync.example.com", ExtractDnsNames(sanExtension.RawData));
        Assert.Contains("datasync", ExtractDnsNames(sanExtension.RawData));
    }

    /// <summary>Same DER-decoding approach as <see cref="CertificateSanReader"/>, applied directly to
    /// the SAN extension bytes rather than to a certificate — <see cref="CertificateRequest"/> has no
    /// certificate to hand <see cref="CertificateSanReader.GetDnsNames"/> itself.</summary>
    private static List<string> ExtractDnsNames(byte[] sanExtensionRawData)
    {
        var reader = new System.Formats.Asn1.AsnReader(sanExtensionRawData, System.Formats.Asn1.AsnEncodingRules.DER);
        var sequence = reader.ReadSequence();
        var names = new List<string>();
        while (sequence.HasData)
        {
            var tag = sequence.PeekTag();
            if (tag.TagClass == System.Formats.Asn1.TagClass.ContextSpecific && tag.TagValue == 2)
            {
                var bytes = sequence.ReadOctetString(new System.Formats.Asn1.Asn1Tag(System.Formats.Asn1.TagClass.ContextSpecific, 2));
                names.Add(System.Text.Encoding.ASCII.GetString(bytes));
            }
            else
            {
                sequence.ReadEncodedValue();
            }
        }

        return names;
    }
}
