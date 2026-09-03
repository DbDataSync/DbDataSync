using DbDataSync.Core.Config;
using DbDataSync.Core.Git;
using LibGit2Sharp;

namespace DbDataSync.Certificates.Tests;

/// <summary>
/// <see cref="CertificateBinding"/> touches no Windows API — see its own doc comment — so this proves
/// the "one section, three literal colons" trick actually round-trips through
/// <see cref="DbDataSyncConfigFile"/>'s real writer and reader, on any OS, with a real (self-signed, since
/// that needs no store) certificate rather than a store-installed one.
/// </summary>
public sealed class CertificateBindingTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-cert-binding-tests-").FullName;

    public CertificateBindingTests() => Repository.Init(_root);

    public void Dispose() => GitTempDirectory.DeleteRecursively(_root);

    [Fact]
    public void Bind_WritesAllFourKestrelKeys_ReadableBackThroughDbDataSyncConfigFile()
    {
        var spec = new CertificateSpec("dbdatasync.example.com", ["dbdatasync.example.com"], 365, null);
        using var certificate = CertificateBuilder.CreateSelfSigned(spec);

        CertificateBinding.Bind(_root, certificate, allowInvalid: true, new GitCommitService(_root), new GitAuthor("Test", "test@localhost"));

        var values = DbDataSyncConfigFile.Read(_root);
        Assert.Equal("dbdatasync.example.com", values["Kestrel:Certificates:Default:Subject"]);
        Assert.Equal("My", values["Kestrel:Certificates:Default:Store"]);
        Assert.Equal("LocalMachine", values["Kestrel:Certificates:Default:Location"]);
        Assert.Equal("true", values["Kestrel:Certificates:Default:AllowInvalid"]);
    }

    [Fact]
    public void Bind_RoundTripsThroughCertificateBindingReadToo()
    {
        var spec = new CertificateSpec("dbdatasync.example.com", ["dbdatasync.example.com"], 365, null);
        using var certificate = CertificateBuilder.CreateSelfSigned(spec);

        CertificateBinding.Bind(_root, certificate, allowInvalid: false, new GitCommitService(_root), new GitAuthor("Test", "test@localhost"));

        var bound = CertificateBinding.Read(_root);
        Assert.Equal("dbdatasync.example.com", bound.Subject);
        Assert.Equal("My", bound.Store);
        Assert.Equal("LocalMachine", bound.Location);
        Assert.False(bound.AllowInvalid);
    }

    [Fact]
    public void Bind_CommitsToGit()
    {
        var spec = new CertificateSpec("dbdatasync.example.com", ["dbdatasync.example.com"], 365, null);
        using var certificate = CertificateBuilder.CreateSelfSigned(spec);

        CertificateBinding.Bind(_root, certificate, allowInvalid: true, new GitCommitService(_root), new GitAuthor("Test", "test@localhost"));

        using var repo = new Repository(_root);
        var head = repo.Head.Tip;
        Assert.NotNull(head);
        Assert.Contains("dbdatasync.config.yaml", head!.Tree.Select(e => e.Path));
        Assert.Contains(certificate.Thumbprint, head.Message);
    }

    [Fact]
    public void Read_WithNothingBoundYet_ReturnsNullSubject()
    {
        var bound = CertificateBinding.Read(_root);

        Assert.Null(bound.Subject);
        Assert.False(bound.AllowInvalid);
    }

    [Fact]
    public void SubjectCommonName_IsTheSimpleName_NotTheFullDistinguishedName()
    {
        var spec = new CertificateSpec("dbdatasync.example.com", ["dbdatasync.example.com"], 365, null);
        using var certificate = CertificateBuilder.CreateSelfSigned(spec);

        Assert.Equal("dbdatasync.example.com", CertificateBinding.SubjectCommonName(certificate));
        Assert.NotEqual(certificate.Subject, CertificateBinding.SubjectCommonName(certificate));
    }
}
