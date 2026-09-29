namespace DbDataSync.Updates.Tests;

public class SlotPathsTests
{
    /// <summary>Lays down what <c>dotnet tool install --tool-path</c> leaves in a slot, as far as the launcher looks.</summary>
    internal static string PutPayload(string slotDirectory, string version, string framework = "net10.0")
    {
        var any = Path.Combine(slotDirectory, ".store", "dbdatasync", version, "dbdatasync", version, "tools", framework, "any");
        Directory.CreateDirectory(any);
        var path = Path.Combine(any, SlotPaths.PayloadAssemblyName);
        File.WriteAllText(path, "payload");
        return path;
    }

    [Theory]
    [InlineData("a", "a")]
    [InlineData("b\n", "b")]
    [InlineData("  B \r\n", "b")]
    [InlineData("c", null)]
    [InlineData("", null)]
    [InlineData("a b", null)]
    [InlineData(null, null)]
    public void OnlyAOrBIsASlot_WhitespaceAside(string? text, string? expected) =>
        Assert.Equal(expected, SlotPaths.ParseSlot(text));

    [Fact]
    public void TheOtherSlot_IsTheOtherOne()
    {
        Assert.Equal("b", SlotPaths.Other("a"));
        Assert.Equal("a", SlotPaths.Other("b"));
    }

    [Fact]
    public void ReadPointer_IsNullWithoutAFile_AndForAnythingButASlot()
    {
        using var root = new TempDirectory();
        Assert.Null(SlotPaths.ReadPointer(root.Path));

        File.WriteAllText(SlotPaths.PointerPath(root.Path), "b\n");
        Assert.Equal("b", SlotPaths.ReadPointer(root.Path));

        File.WriteAllText(SlotPaths.PointerPath(root.Path), "versions/b");
        Assert.Null(SlotPaths.ReadPointer(root.Path));
    }

    [Fact]
    public void FindsThePayload_InTheStoreLayoutDotnetToolProduces()
    {
        using var root = new TempDirectory();
        var slot = SlotPaths.SlotDirectory(root.Path, "a");
        var expected = PutPayload(slot, "2026.9.28.1");

        Assert.Equal([expected], SlotPaths.FindPayloads(slot));
        Assert.Equal(["2026.9.28.1"], SlotPaths.InstalledVersions(slot));
    }

    [Fact]
    public void AnEmptyOrMissingSlot_HasNoPayloadAndNoVersion()
    {
        using var root = new TempDirectory();
        var slot = SlotPaths.SlotDirectory(root.Path, "b");

        Assert.Empty(SlotPaths.FindPayloads(slot));
        Assert.Empty(SlotPaths.InstalledVersions(slot));

        // A version directory an interrupted install left without its tools/ is a version, but not a payload.
        Directory.CreateDirectory(Path.Combine(slot, ".store", "dbdatasync", "2026.9.28.1"));
        Assert.Empty(SlotPaths.FindPayloads(slot));
        Assert.Equal(["2026.9.28.1"], SlotPaths.InstalledVersions(slot));
    }

    [Fact]
    public void TwoInstallsInOneSlot_AreBothReported_SoTheCallerCanRefuseToGuess()
    {
        using var root = new TempDirectory();
        var slot = SlotPaths.SlotDirectory(root.Path, "a");
        PutPayload(slot, "2026.9.28.1");
        PutPayload(slot, "2026.9.29.1");

        Assert.Equal(2, SlotPaths.FindPayloads(slot).Count);
    }

    [Fact]
    public void ThePortableRid_IsOneALauncherIsBuiltFor()
    {
        // Whatever this host is, the answer must be one of the directories the package carries — never the
        // distribution-specific RID a source-built runtime reports.
        string[] built = ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "linux-musl-x64", "linux-musl-arm64", "osx-x64", "osx-arm64"];
        var rid = SlotPaths.PortableRuntimeIdentifier();
        Assert.NotNull(rid);
        Assert.Contains(rid, built);
    }
}
