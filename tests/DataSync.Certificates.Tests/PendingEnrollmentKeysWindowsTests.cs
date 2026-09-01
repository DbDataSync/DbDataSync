using System.Runtime.Versioning;

namespace DataSync.Certificates.Tests;

/// <summary>
/// The key-persistence primitive behind AD CS enrollment's pending-request handling — see
/// <see cref="PendingEnrollmentKeys"/>'s own doc comment for why it exists. Needs no CA at all, unlike
/// <see cref="AdcsEnrollment"/> itself, so it gets real coverage even though the enrollment path around
/// it does not.
/// <para>
/// Every call passes <c>machineKey: false</c> — the same "no elevation needed" reasoning as the doc's
/// own <c>CurrentUser\My</c> store test: a machine-scoped CNG key needs an elevated prompt to create,
/// which real usage already has (every real call site targets <c>LocalMachine\My</c>) but a test
/// shouldn't have to.
/// </para>
/// </summary>
[Trait("Category", "Windows")]
[SupportedOSPlatform("windows")]
public sealed class PendingEnrollmentKeysWindowsTests
{
    [Fact]
    public void Create_ThenOpen_ReturnsAUsableKeyOfTheRightSize()
    {
        var keyName = PendingEnrollmentKeys.NewKeyName();
        using (PendingEnrollmentKeys.Create(keyName, machineKey: false)) { }

        using var reopened = PendingEnrollmentKeys.Open(keyName, machineKey: false);

        try
        {
            Assert.NotNull(reopened);
            Assert.Equal(CertificateSpec.DefaultKeySizeBits, reopened!.KeySize);
        }
        finally
        {
            PendingEnrollmentKeys.Delete(keyName, machineKey: false);
        }
    }

    [Fact]
    public void Open_UnknownKeyName_ReturnsNull()
    {
        var result = PendingEnrollmentKeys.Open($"DataSync-Nonexistent-{Guid.NewGuid():N}", machineKey: false);

        Assert.Null(result);
    }

    [Fact]
    public void Delete_ThenOpen_ReturnsNull()
    {
        var keyName = PendingEnrollmentKeys.NewKeyName();
        using (PendingEnrollmentKeys.Create(keyName, machineKey: false)) { }

        PendingEnrollmentKeys.Delete(keyName, machineKey: false);

        Assert.Null(PendingEnrollmentKeys.Open(keyName, machineKey: false));
    }

    [Fact]
    public void Delete_OnAKeyThatWasNeverCreated_DoesNotThrow()
    {
        PendingEnrollmentKeys.Delete($"DataSync-Nonexistent-{Guid.NewGuid():N}", machineKey: false);
    }
}
