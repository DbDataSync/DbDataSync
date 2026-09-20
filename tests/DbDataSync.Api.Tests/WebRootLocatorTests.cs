using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>Finding the console's files when the process was not started in the tool's own directory (phase 160).</summary>
public sealed class WebRootLocatorTests : IDisposable
{
    private readonly string _current = Directory.CreateTempSubdirectory("dbdatasync-cwd-").FullName;
    private readonly string _app = Directory.CreateTempSubdirectory("dbdatasync-app-").FullName;

    public void Dispose()
    {
        Directory.Delete(_current, recursive: true);
        Directory.Delete(_app, recursive: true);
    }

    [Fact]
    public void WhenTheWorkingDirectoryHasNone_TheAppsOwnIsUsed()
    {
        // The installed tool run from a terminal, or from a unit whose WorkingDirectory is the data root.
        Directory.CreateDirectory(Path.Combine(_app, "wwwroot"));

        Assert.Equal(Path.Combine(_app, "wwwroot"), WebRootLocator.Resolve(_current, _app));
    }

    [Fact]
    public void AWwwrootInTheWorkingDirectory_Wins_SoASourceCheckoutIsUnchanged()
    {
        Directory.CreateDirectory(Path.Combine(_current, "wwwroot"));
        Directory.CreateDirectory(Path.Combine(_app, "wwwroot"));

        Assert.Null(WebRootLocator.Resolve(_current, _app));
    }

    [Fact]
    public void WhenTheTwoAreTheSameDirectory_AsInTheContainer_NothingChanges()
    {
        Directory.CreateDirectory(Path.Combine(_app, "wwwroot"));

        Assert.Null(WebRootLocator.Resolve(_app, _app));
    }

    [Fact]
    public void WithNoneAnywhere_TheDefaultStays_SoTheHostStillSaysNoWebAssetsArePublished()
    {
        Assert.Null(WebRootLocator.Resolve(_current, _app));
    }
}
