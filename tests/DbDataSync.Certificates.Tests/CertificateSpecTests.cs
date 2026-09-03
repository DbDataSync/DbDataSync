namespace DbDataSync.Certificates.Tests;

public sealed class CertificateSpecTests
{
    [Fact]
    public void EmptyDnsNames_IsRejectedAtConstruction()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new CertificateSpec("dbdatasync.example.com", [], 365, null));

        Assert.Contains("browser", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NonEmptyDnsNames_ConstructsFine()
    {
        var spec = new CertificateSpec("dbdatasync.example.com", ["dbdatasync.example.com"], 365, "DbDataSync");

        Assert.Single(spec.DnsNames);
        Assert.Equal("DbDataSync", spec.FriendlyName);
    }

}
