namespace DataSync.Certificates.Tests;

public sealed class CertificateSpecTests
{
    [Fact]
    public void EmptyDnsNames_IsRejectedAtConstruction()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new CertificateSpec("datasync.example.com", [], 365, null));

        Assert.Contains("browser", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NonEmptyDnsNames_ConstructsFine()
    {
        var spec = new CertificateSpec("datasync.example.com", ["datasync.example.com"], 365, "DataSync");

        Assert.Single(spec.DnsNames);
        Assert.Equal("DataSync", spec.FriendlyName);
    }

}
