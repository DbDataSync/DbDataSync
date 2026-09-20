namespace DbDataSync.Updates.Tests;

public class ToolStoreTests
{
    private static string Put(string root, string version, string fileName)
    {
        var directory = Path.Combine(root, ".store", "dbdatasync", version.ToLowerInvariant(), "dbdatasync", version.ToLowerInvariant());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, "package");
        return path;
    }

    [Fact]
    public void FindsTheInstalledPackage_UnderTheStoreLayoutDotnetToolProduces()
    {
        using var root = new TempDirectory();
        var expected = Put(root.Path, "2026.9.18.1918", "dbdatasync.2026.9.18.1918.nupkg");
        Put(root.Path, "2026.9.18.1918", "dbdatasync.2026.9.18.1918.nupkg.sha512");

        Assert.Equal(expected, ToolStore.FindInstalledNupkg(root.Path, "2026.9.18.1918"));
    }

    [Fact]
    public void TheVersionDirectoryIsLowercase_SoALabelInAnyCaseIsFound()
    {
        using var root = new TempDirectory();
        Put(root.Path, "2026.9.19.1432-snapshot.gabc1234", "dbdatasync.2026.9.19.1432-snapshot.gabc1234.nupkg");

        Assert.NotNull(ToolStore.FindInstalledNupkg(root.Path, "2026.9.19.1432-Snapshot.Gabc1234"));
    }

    [Fact]
    public void AVersionThatIsNotInstalled_IsNull()
    {
        using var root = new TempDirectory();
        Put(root.Path, "2026.9.18.1918", "dbdatasync.2026.9.18.1918.nupkg");

        Assert.Null(ToolStore.FindInstalledNupkg(root.Path, "2026.9.16.1005"));
        Assert.Null(ToolStore.FindInstalledNupkg(Path.Combine(root.Path, "nowhere"), "2026.9.18.1918"));
    }
}
