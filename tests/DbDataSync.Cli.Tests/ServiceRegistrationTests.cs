namespace DbDataSync.Cli.Tests;

/// <summary>
/// <see cref="ServiceRegistration"/>'s own read/write/clear round trip, against a real temp directory —
/// no seam needed, it's a plain JSON file.
/// </summary>
public sealed class ServiceRegistrationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-service-registration-tests-").FullName;

    public void Dispose() => GitTempDirectory.DeleteRecursively(_root);

    [Fact]
    public void NoMarkerWritten_ReadReturnsNull()
    {
        Assert.Null(ServiceRegistration.Read(_root));
    }

    [Fact]
    public void Write_ThenRead_RoundTripsTheAccountAndPlatform()
    {
        ServiceRegistration.Write(_root, "LocalSystem", "windows");

        var info = ServiceRegistration.Read(_root);

        Assert.NotNull(info);
        Assert.Equal("LocalSystem", info!.Account);
        Assert.Equal("windows", info.Platform);
    }

    [Fact]
    public void Write_AddsTheMarkerFileToGitignore()
    {
        ServiceRegistration.Write(_root, "LocalSystem", "windows");

        var gitignore = File.ReadAllText(Path.Combine(_root, ".gitignore"));
        Assert.Contains("service-registration.json", gitignore);
    }

    [Fact]
    public void Write_TwiceWithDifferentAccounts_LatestWins()
    {
        ServiceRegistration.Write(_root, "LocalSystem", "windows");
        ServiceRegistration.Write(_root, "CORP\\svc-dbdatasync", "windows");

        var info = ServiceRegistration.Read(_root);

        Assert.Equal("CORP\\svc-dbdatasync", info!.Account);
    }

    [Fact]
    public void Clear_RemovesTheMarker()
    {
        ServiceRegistration.Write(_root, "LocalSystem", "windows");

        ServiceRegistration.Clear(_root);

        Assert.Null(ServiceRegistration.Read(_root));
    }

    [Fact]
    public void Clear_WithNoMarker_DoesNotThrow()
    {
        ServiceRegistration.Clear(_root);
    }
}
