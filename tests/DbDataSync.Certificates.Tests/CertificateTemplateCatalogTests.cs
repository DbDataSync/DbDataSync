using System.Runtime.InteropServices;

namespace DbDataSync.Certificates.Tests;

/// <summary>The doc's own bullet: "template listing returns empty and a reason for each failure path...
/// and never throws." <see cref="CertificateTemplateCatalog.List"/>'s CaConfig-unset check and
/// <see cref="CertificateTemplateCatalog.ClassifyDirectoryFailure"/> are both plain, portable logic —
/// see that class's own doc comment for why only the actual LDAP query is Windows-only — so this class
/// runs on any OS.</summary>
public sealed class CertificateTemplateCatalogTests
{
    [Fact]
    public void NullCaConfig_ReturnsEmptyWithCaConfigNotSetReason()
    {
        var result = CertificateTemplateCatalog.List(null);

        Assert.Empty(result.Templates);
        Assert.Equal(TemplateListReason.CaConfigNotSet, result.Reason);
        Assert.NotNull(result.Detail);
    }

    [Fact]
    public void EmptyCaConfig_ReturnsEmptyWithCaConfigNotSetReason()
    {
        var result = CertificateTemplateCatalog.List("   ");

        Assert.Empty(result.Templates);
        Assert.Equal(TemplateListReason.CaConfigNotSet, result.Reason);
    }

    [Fact]
    public void UnauthorizedAccessException_ClassifiesAsAccessDenied()
    {
        var reason = CertificateTemplateCatalog.ClassifyDirectoryFailure(new UnauthorizedAccessException("denied"));

        Assert.Equal(TemplateListReason.AccessDenied, reason);
    }

    [Fact]
    public void ComExceptionWithAccessDeniedHResult_ClassifiesAsAccessDenied()
    {
        var reason = CertificateTemplateCatalog.ClassifyDirectoryFailure(
            new COMException("insufficient rights", unchecked((int)0x80072020)));

        Assert.Equal(TemplateListReason.AccessDenied, reason);
    }

    [Fact]
    public void OtherComException_ClassifiesAsDirectoryUnreachable()
    {
        var reason = CertificateTemplateCatalog.ClassifyDirectoryFailure(
            new COMException("server down", unchecked((int)0x8007203A)));

        Assert.Equal(TemplateListReason.DirectoryUnreachable, reason);
    }

    [Fact]
    public void UnrecognisedException_ClassifiesAsUnknown_ButStillHasAReason()
    {
        var reason = CertificateTemplateCatalog.ClassifyDirectoryFailure(new InvalidOperationException("huh"));

        Assert.Equal(TemplateListReason.Unknown, reason);
    }

    // No test exercises List's own `if (!OperatingSystem.IsWindows())` branch — this sandbox is
    // Windows, this repo's test projects carry no conditional-skip package (xunit.skippablefact or
    // similar) today, and adding one for a single branch was judged not worth a new dependency. That
    // branch is two lines, structurally identical to the CaConfigNotSet check just above (which *is*
    // tested), and is verified by inspection rather than by an executed non-Windows run — see the
    // phase 82 retrospective.
}
