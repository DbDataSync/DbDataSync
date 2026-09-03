using System.Runtime.Versioning;

namespace DbDataSync.Certificates.Tests;

/// <summary>
/// <see cref="InstalledServiceAccount.ParseServiceStartName"/> against a literal, hand-captured sample
/// of real <c>sc.exe qc</c> output — see that method's own doc comment for why the doc's claim that
/// <c>dbdatasync service status</c> "already parses" this turned out not to be true, and why this is a
/// fresh parser rather than a reused one.
/// </summary>
public sealed class InstalledServiceAccountTests
{
    private const string RealScQcOutput =
        "[SC] QueryServiceConfig SUCCESS\r\n" +
        "\r\n" +
        "SERVICE_NAME: DbDataSync\r\n" +
        "        TYPE               : 10  WIN32_OWN_PROCESS\r\n" +
        "        START_TYPE         : 2   AUTO_START\r\n" +
        "        ERROR_CONTROL      : 1   NORMAL\r\n" +
        "        BINARY_PATH_NAME   : \"C:\\Program Files\\DbDataSync\\dbdatasync.exe\" serve --repo \"C:\\data\" --url http://localhost:5080\r\n" +
        "        LOAD_ORDER_GROUP   : \r\n" +
        "        TAG                : 0\r\n" +
        "        DISPLAY_NAME       : DbDataSync\r\n" +
        "        DEPENDENCIES       : \r\n" +
        "        SERVICE_START_NAME : CORP\\svc-dbdatasync\r\n";

    [Fact]
    public void ParsesTheDomainAccount()
    {
        Assert.Equal("CORP\\svc-dbdatasync", InstalledServiceAccount.ParseServiceStartName(RealScQcOutput));
    }

    [Fact]
    public void ParsesLocalSystem()
    {
        var output = RealScQcOutput.Replace("SERVICE_START_NAME : CORP\\svc-dbdatasync", "SERVICE_START_NAME : LocalSystem");

        Assert.Equal("LocalSystem", InstalledServiceAccount.ParseServiceStartName(output));
    }

    [Fact]
    public void NoMatchingLine_ReturnsNull()
    {
        Assert.Null(InstalledServiceAccount.ParseServiceStartName("nothing useful here"));
    }

    [Fact]
    public void EmptyOutput_ReturnsNull()
    {
        Assert.Null(InstalledServiceAccount.ParseServiceStartName(""));
    }
}

/// <summary>The other half of <see cref="InstalledServiceAccountTests"/> — the actual <c>sc.exe</c>
/// invocation, which needs Windows. Only the graceful-failure path is exercised: this sandbox has no
/// installed 'DbDataSync' service (and installing one needs elevation this test should not require), so
/// this proves the "not installed" case falls through to null cleanly rather than throwing — the same
/// shape <c>CertCommand</c>'s "which account" resolution relies on.</summary>
[Trait("Category", "Windows")]
[SupportedOSPlatform("windows")]
public sealed class InstalledServiceAccountWindowsTests
{
    [Fact]
    public void UnknownServiceName_ReturnsNull()
    {
        var result = InstalledServiceAccount.Resolve($"DbDataSync-Does-Not-Exist-{Guid.NewGuid():N}");

        Assert.Null(result);
    }
}
