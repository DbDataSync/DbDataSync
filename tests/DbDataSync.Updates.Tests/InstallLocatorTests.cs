namespace DbDataSync.Updates.Tests;

public class InstallLocatorTests
{
    // The layout `dotnet tool install` really produces (checked against a real install on 2026-09-19):
    // <root>/.store/dbdatasync/<version>/dbdatasync/<version>/tools/net10.0/any/
    private const string Layout = ".store/dbdatasync/2026.9.18.1918/dbdatasync/2026.9.18.1918/tools/net10.0/any/";

    [Fact]
    public void APerUserGlobalInstall_IsRecognised()
    {
        var location = InstallLocator.Locate($"/home/dan/.dotnet/tools/{Layout}", "/home/dan/.dotnet/tools", inContainer: false);

        Assert.Equal(InstallKind.Global, location.Kind);
        Assert.Equal("/home/dan/.dotnet/tools", location.ToolRoot);
    }

    [Fact]
    public void AToolPathInstall_IsRecognised_WithItsRoot()
    {
        var location = InstallLocator.Locate($"/opt/dbdatasync/{Layout}", "/home/dan/.dotnet/tools", inContainer: false);

        Assert.Equal(InstallKind.ToolPath, location.Kind);
        Assert.Equal("/opt/dbdatasync", location.ToolRoot);
    }

    [Fact]
    public void AWindowsToolPathInstall_KeepsItsSpacesAndBackslashes()
    {
        var location = InstallLocator.Locate(
            @"C:\Program Files\DbDataSync\.store\dbdatasync\2026.9.18.1918\dbdatasync\2026.9.18.1918\tools\net10.0\any\",
            @"C:\Users\Dan\.dotnet\tools",
            inContainer: false);

        Assert.Equal(InstallKind.ToolPath, location.Kind);
        Assert.Equal(@"C:\Program Files\DbDataSync", location.ToolRoot);
    }

    [Fact]
    public void AWindowsGlobalInstall_IsRecognised_RegardlessOfCaseOrSeparators()
    {
        var location = InstallLocator.Locate(
            @"c:\users\dan\.dotnet\tools\.store\dbdatasync\2026.9.18.1918\dbdatasync\2026.9.18.1918\tools\net10.0\any\",
            @"C:\Users\Dan\.dotnet\tools\",
            inContainer: false);

        Assert.Equal(InstallKind.Global, location.Kind);
    }

    [Fact]
    public void ATrailingSeparatorOnTheGlobalDirectory_DoesNotHideAGlobalInstall()
    {
        var location = InstallLocator.Locate($"/home/dan/.dotnet/tools/{Layout}", "/home/dan/.dotnet/tools/", inContainer: false);

        Assert.Equal(InstallKind.Global, location.Kind);
    }

    [Theory]
    [InlineData("/mnt/data/Projects/DbDataSync/src/DbDataSync.Cli/bin/Debug/net10.0/")]
    [InlineData("/app/")]
    [InlineData(@"C:\src\DbDataSync\src\DbDataSync.Cli\bin\Debug\net10.0\")]
    // Somebody else's tool in the same store is not this one.
    [InlineData("/opt/dbdatasync/.store/othertool/1.0.0/othertool/1.0.0/tools/net10.0/any/")]
    // The marker has to be a whole path segment, not a substring of one.
    [InlineData("/opt/x.store/dbdatasync/1.0.0/")]
    public void ABuildOrUnpackedCopy_IsNotAToolInstall(string baseDirectory)
    {
        var location = InstallLocator.Locate(baseDirectory, "/home/dan/.dotnet/tools", inContainer: false);

        Assert.Equal(InstallKind.NotAToolInstall, location.Kind);
        Assert.Null(location.ToolRoot);
    }

    [Fact]
    public void AContainer_IsAContainer_EvenIfItsPathLooksLikeAToolStore()
    {
        var location = InstallLocator.Locate($"/opt/dbdatasync/{Layout}", "/home/dan/.dotnet/tools", inContainer: true);

        Assert.Equal(InstallKind.Container, location.Kind);
        Assert.Null(location.ToolRoot);
    }

    [Fact]
    public void AToolStoreAtTheFilesystemRoot_HasTheRootAsItsToolPath()
    {
        var location = InstallLocator.Locate($"/{Layout}", "/home/dan/.dotnet/tools", inContainer: false);

        Assert.Equal(InstallKind.ToolPath, location.Kind);
        Assert.Equal("/", location.ToolRoot);
    }

    [Fact]
    public void TheLastStoreInAPathWins()
    {
        // A tool root that itself lives under something called .store/dbdatasync/ must resolve to the
        // innermost store, the one this tool is actually in.
        var location = InstallLocator.Locate($"/srv/.store/dbdatasync/backup/tools/{Layout}", "/home/dan/.dotnet/tools", inContainer: false);

        Assert.Equal(InstallKind.ToolPath, location.Kind);
        Assert.Equal("/srv/.store/dbdatasync/backup/tools", location.ToolRoot);
    }
}
