namespace DbDataSync.Updates.Tests;

public class UpdateStateStoreTests
{
    private static UpdateStateStore Store(TempDirectory root) => new(new UpdateWorkspace(root.Path));

    [Fact]
    public void TheWorkspace_IsTheDataRootsUpdatesDirectory_WithAbsolutePaths()
    {
        var workspace = new UpdateWorkspace("data");

        Assert.Equal(Path.Combine(Path.GetFullPath("data"), "updates", "update-state.json"), workspace.StatePath);
        Assert.Equal(Path.Combine(Path.GetFullPath("data"), "updates", "update.log"), workspace.LogPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("[]")]
    public void ACorruptFile_IsTreatedAsAbsent(string content)
    {
        using var root = new TempDirectory();
        var store = Store(root);
        Directory.CreateDirectory(store.Workspace.Directory);
        File.WriteAllText(store.Workspace.StatePath, content);

        Assert.Null(store.ReadState().Current);
    }

    [Fact]
    public void AFileFarLargerThanAnythingWritten_IsNotOneOfOurs()
    {
        using var root = new TempDirectory();
        var store = Store(root);
        Directory.CreateDirectory(store.Workspace.Directory);
        File.WriteAllText(store.Workspace.StatePath, "{\"current\":null,\"history\":[],\"padding\":\"" + new string('x', 100_000) + "\"}");

        Assert.Null(store.ReadState().Current);
        Assert.Empty(store.ReadState().History);
    }

    [Fact]
    public void AStateFileWrittenBefore196L_WithItsRetiredPhases_StillReads()
    {
        using var root = new TempDirectory();
        var store = Store(root);
        Directory.CreateDirectory(store.Workspace.Directory);
        File.WriteAllText(store.Workspace.StatePath,
            """{"current":{"phase":"draining","message":"Waiting.","atUtc":"2026-09-19T00:00:00Z"},"history":[]}""");

        Assert.Equal(UpdatePhase.Draining, store.ReadState().Current!.Phase);
    }

    // --- the directory is writable by the service, so it is read as hostile -------------------------------

    [NonWindowsFact]
    public void AStateFileThatIsASymbolicLink_IsNotFollowed()
    {
        using var root = new TempDirectory();
        var store = Store(root);
        Directory.CreateDirectory(store.Workspace.Directory);
        var elsewhere = Path.Combine(root.Path, "elsewhere.json");
        File.WriteAllText(elsewhere, """{"current":{"phase":"succeeded","atUtc":"2026-09-19T00:00:00Z"},"history":[]}""");
        File.CreateSymbolicLink(store.Workspace.StatePath, elsewhere);

        Assert.Null(store.ReadState().Current);
    }

    [NonWindowsFact]
    public void AnUpdatesDirectoryThatIsASymbolicLink_IsNotReadFrom_OrWrittenThrough()
    {
        using var root = new TempDirectory();
        using var target = new TempDirectory();
        File.WriteAllText(Path.Combine(target.Path, "update-state.json"), """{"current":{"phase":"succeeded","atUtc":"2026-09-19T00:00:00Z"},"history":[]}""");
        Directory.CreateSymbolicLink(Path.Combine(root.Path, "updates"), target.Path);
        var store = Store(root);

        Assert.Null(store.ReadState().Current);
        var ex = Assert.Throws<IOException>(() => store.Record(UpdatePhase.Failed, "x", null, null, null));
        Assert.Contains("symbolic link", ex.Message);
        Assert.Equal(["update-state.json"], Directory.GetFiles(target.Path).Select(Path.GetFileName));
    }

    [NonWindowsFact]
    public void WritingOverAPathSwappedForASymbolicLink_ReplacesTheLink_NotWhatItPointedAt()
    {
        using var root = new TempDirectory();
        var store = Store(root);
        Directory.CreateDirectory(store.Workspace.Directory);
        var precious = Path.Combine(root.Path, "precious.txt");
        File.WriteAllText(precious, "do not touch");
        File.CreateSymbolicLink(store.Workspace.StatePath, precious);

        store.Record(UpdatePhase.Failed, "it broke", null, null, null);

        Assert.Equal("do not touch", File.ReadAllText(precious));
        Assert.Equal(UpdatePhase.Failed, store.ReadState().Current!.Phase);
    }

    // --- state ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Record_ANonTerminalPhase_SetsCurrent_WithoutTouchingHistory()
    {
        using var root = new TempDirectory();
        var store = Store(root);

        store.Record(UpdatePhase.Applying, "Installing 2.0 into slot b.", "1.0", "2.0", "dan");

        var state = store.ReadState();
        Assert.Equal(UpdatePhase.Applying, state.Current!.Phase);
        Assert.Equal("Installing 2.0 into slot b.", state.Current.Message);
        Assert.Equal("dan", state.Current.RequestedBy);
        Assert.Empty(state.History);
    }

    [Fact]
    public void Record_AFinishedOutcome_JoinsTheHistory_NewestFirst_AndIsCapped()
    {
        using var root = new TempDirectory();
        var store = Store(root);

        for (var i = 1; i <= 12; i++)
            store.Record(i % 2 == 0 ? UpdatePhase.Succeeded : UpdatePhase.Failed, $"outcome {i}", "1.0", "2.0", null);
        store.Record(UpdatePhase.Applying, "in flight", "1.0", "2.0", null);

        var state = store.ReadState();
        Assert.Equal(UpdatePhase.Applying, state.Current!.Phase);
        Assert.Equal(10, state.History.Count);
        Assert.Equal("outcome 12", state.History[0].Message);
        Assert.Equal("outcome 3", state.History[^1].Message);
        Assert.All(state.History, h => Assert.True(h.IsTerminal));
    }

    [Fact]
    public void DisplayText_IsStrippedOfControlCharacters_AndBounded()
    {
        using var root = new TempDirectory();
        var store = Store(root);

        store.Record(UpdatePhase.Failed, "line one\nline\u001b[31m two", "1.0", "2.0", new string('a', 900) + "\r\nb");

        var current = store.ReadState().Current!;
        Assert.Equal("line oneline[31m two", current.Message);
        Assert.Equal(500, current.RequestedBy!.Length);
        Assert.DoesNotContain('\n', current.RequestedBy);
    }

    [Fact]
    public void Writes_LeaveNoTempFilesBehind()
    {
        using var root = new TempDirectory();
        var store = Store(root);

        store.Record(UpdatePhase.Applying, "one", null, null, null);
        store.Record(UpdatePhase.Succeeded, "two", null, null, null);

        Assert.Equal("two", store.ReadState().Current!.Message);
        Assert.Empty(Directory.GetFiles(store.Workspace.Directory, "*.tmp"));
    }

    [Fact]
    public void Log_AppendsTimestampedLines_AndNeverThrows()
    {
        using var root = new TempDirectory();
        var store = Store(root);

        store.Log("first");
        store.Log("second");

        var lines = File.ReadAllLines(store.Workspace.LogPath);
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("Z  first", lines[0]);

        // A log that cannot be written (its path is a directory) is skipped, not an exception half way through.
        Directory.Delete(store.Workspace.Directory, recursive: true);
        Directory.CreateDirectory(store.Workspace.LogPath);
        store.Log("third");
    }
}
