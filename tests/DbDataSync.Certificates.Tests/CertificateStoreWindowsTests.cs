using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace DbDataSync.Certificates.Tests;

/// <summary>
/// The doc's own Windows-only test bullet: install into <c>CurrentUser\My</c> (so the test needs no
/// elevation), then assert the private key DACL contains a read ACE for a named account after
/// <see cref="PrivateKeyAccess.Grant"/>. Runs on this sandbox, which is Windows — see the class-level
/// <c>Trait</c>, the same <c>Category=Windows</c> convention this repo already uses for
/// <c>Category=Integration</c>.
/// </summary>
[Trait("Category", "Windows")]
[SupportedOSPlatform("windows")]
public sealed class CertificateStoreWindowsTests : IDisposable
{
    private readonly List<X509Certificate2> _installed = [];

    public void Dispose()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);

        foreach (var certificate in _installed)
        {
            // Removing from the store does not delete the underlying CNG key container — done
            // explicitly, so a repeated test run never accumulates orphaned key files in this
            // sandbox's user profile.
            if (certificate.GetRSAPrivateKey() is RSACng rsaCng)
                rsaCng.Key.Delete();

            store.Remove(certificate);
            certificate.Dispose();
        }
    }

    private X509Certificate2 InstallSelfSigned(string commonName)
    {
        var spec = new CertificateSpec(commonName, [commonName], 365, "DbDataSync Test");
        using var ephemeral = CertificateBuilder.CreateSelfSigned(spec);
        var installed = CertificateStore.Install(ephemeral, StoreLocation.CurrentUser);
        _installed.Add(installed);
        return installed;
    }

    [Fact]
    public void Install_ThenFindByThumbprint_FindsIt()
    {
        var installed = InstallSelfSigned($"dbdatasync-test-{Guid.NewGuid():N}.example.com");

        var found = CertificateStore.FindByThumbprint(installed.Thumbprint, StoreLocation.CurrentUser);

        Assert.NotNull(found);
        Assert.Equal(installed.Thumbprint, found!.Thumbprint);
    }

    [Fact]
    public void Install_ThenListServerAuth_IncludesIt()
    {
        var installed = InstallSelfSigned($"dbdatasync-test-{Guid.NewGuid():N}.example.com");

        var listed = CertificateStore.ListServerAuthCertificates(StoreLocation.CurrentUser);

        Assert.Contains(listed, c => c.Thumbprint == installed.Thumbprint);
    }

    [Fact]
    public void Install_ThenFindBySubject_FindsIt()
    {
        var commonName = $"dbdatasync-test-{Guid.NewGuid():N}.example.com";
        var installed = InstallSelfSigned(commonName);

        var found = CertificateStore.FindBySubject(commonName, StoreLocation.CurrentUser);

        Assert.NotNull(found);
        Assert.Equal(installed.Thumbprint, found!.Thumbprint);
    }

    [Fact]
    public void Install_PersistsAKeyThatPrivateKeyAccessCanGrantOn()
    {
        var installed = InstallSelfSigned($"dbdatasync-test-{Guid.NewGuid():N}.example.com");
        var account = WindowsIdentity.GetCurrent().Name;

        PrivateKeyAccess.Grant(installed, account);

        Assert.True(PrivateKeyAccess.CanRead(installed, account));
    }

    [Fact]
    public void CanRead_BeforeAnyGrant_IsFalseForAnUnrelatedAccount()
    {
        var installed = InstallSelfSigned($"dbdatasync-test-{Guid.NewGuid():N}.example.com");

        // "Guest" is a well-known built-in account name on every Windows install and is never the
        // account running this test process, so this is a real negative rather than a tautology.
        Assert.False(PrivateKeyAccess.CanRead(installed, "Guest"));
    }
}
