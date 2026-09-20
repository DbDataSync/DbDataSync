namespace DbDataSync.Updates.Tests;

public class UpdateStateStoreTests
{
    private static PendingUpdate Pending(string target = "2026.9.19.1432-snapshot.g65615e7") =>
        new(target, new DateTimeOffset(2026, 9, 19, 14, 40, 0, TimeSpan.Zero), "dan");

    private static UpdateRequest Applied(string target = "2026.9.19.1432-snapshot.g65615e7") => new(
        target, "2026.9.18.1918", ReleaseChannel.Snapshot, InstallKind.ToolPath, "/opt/dbdatasync", null,
        new DateTimeOffset(2026, 9, 19, 14, 40, 0, TimeSpan.Zero), "dan");

    private static UpdateStateStore Store(TempDirectory root, TempDirectory? privileged = null) =>
        new(new UpdateWorkspace(root.Path, privileged?.Path));

    [Fact]
    public void ThePendingRequest_HoldsAVersionAndWhoAsked_AndNothingElse()
    {
        using var root = new TempDirectory();
        var store = Store(root);

        store.WritePending(Pending());

        Assert.Equal(Pending(), store.ReadPending());
        // There is nowhere in the request to put a source, a folder, a package or a path.
        var properties = System.Text.Json.JsonDocument.Parse(File.ReadAllText(store.Workspace.PendingPath))
            .RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray();
        Assert.Equal(["requestedBy", "requestedUtc", "targetVersion"], properties);
    }

    [Fact]
    public void Applied_And_Confirmed_RoundTrip()
    {
        using var root = new TempDirectory();
        var store = Store(root);

        store.WriteApplied(Applied());
        store.WriteConfirmed("2026.9.19.1432-snapshot.g65615e7");

        Assert.Equal(Applied(), store.ReadApplied());
        Assert.Equal("2026.9.19.1432-snapshot.g65615e7", store.ReadConfirmed()!.TargetVersion);
    }

    [Fact]
    public void AbsentFiles_ReadAsNull_AndClearIsHarmless()
    {
        using var root = new TempDirectory();
        var store = Store(root);

        Assert.Null(store.ReadPending());
        Assert.Null(store.ReadApplied());
        Assert.Null(store.ReadConfirmed());
        Assert.Empty(store.ReadState().History);
        Assert.Null(store.ReadState().Current);
        store.ClearPending();
        store.ClearApplied();
        store.ClearConfirmed();
    }

    [Fact]
    public void Clear_RemovesTheFile()
    {
        using var root = new TempDirectory();
        var store = Store(root);
        store.WritePending(Pending());

        store.ClearPending();

        Assert.Null(store.ReadPending());
        Assert.False(File.Exists(store.Workspace.PendingPath));
    }

    [Fact]
    public void TheFilesAreReadableJson_WithEnumsAsWords()
    {
        using var root = new TempDirectory();
        var store = Store(root);
        store.WriteApplied(Applied());

        var text = File.ReadAllText(store.Workspace.AppliedPath);

        Assert.Contains("\"targetChannel\": \"snapshot\"", text);
        Assert.Contains("\"installKind\": \"toolPath\"", text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("[]")]
    public void ACorruptFile_IsTreatedAsAbsent_NotAsAnErrorThatWedgesEveryStart(string content)
    {
        using var root = new TempDirectory();
        var store = Store(root);
        Directory.CreateDirectory(store.Workspace.Directory);
        File.WriteAllText(store.Workspace.PendingPath, content);
        File.WriteAllText(store.Workspace.ConfirmedPath, content);
        File.WriteAllText(store.Workspace.AppliedPath, content);
        File.WriteAllText(store.Workspace.StatePath, content);

        Assert.Null(store.ReadPending());
        Assert.Null(store.ReadConfirmed());
        Assert.Null(store.ReadApplied());
        Assert.Null(store.ReadState().Current);
    }

    [Fact]
    public void AFileFarLargerThanAnythingWritten_IsNotOneOfOurs()
    {
        using var root = new TempDirectory();
        var store = Store(root);
        Directory.CreateDirectory(store.Workspace.Directory);
        File.WriteAllText(store.Workspace.PendingPath, "{\"targetVersion\":\"1.0\",\"padding\":\"" + new string('x', 100_000) + "\"}");

        Assert.Null(store.ReadPending());
    }

    // --- the service's directory is hostile ----------------------------------------------------------------

    [NonWindowsFact]
    public void ARequestThatIsASymbolicLink_IsNotFollowed()
    {
        using var root = new TempDirectory();
        var store = Store(root);
        Directory.CreateDirectory(store.Workspace.Directory);
        var elsewhere = Path.Combine(root.Path, "elsewhere.json");
        File.WriteAllText(elsewhere, "{\"targetVersion\":\"2026.9.18.1918\",\"requestedUtc\":\"2026-09-19T00:00:00Z\"}");
        File.CreateSymbolicLink(store.Workspace.PendingPath, elsewhere);

        Assert.Null(store.ReadPending());
    }

    [NonWindowsFact]
    public void AnUpdatesDirectoryThatIsASymbolicLink_IsNotReadFrom_OrWrittenThrough()
    {
        using var root = new TempDirectory();
        using var target = new TempDirectory();
        File.WriteAllText(Path.Combine(target.Path, "pending-update.json"), "{\"targetVersion\":\"2026.9.18.1918\",\"requestedUtc\":\"2026-09-19T00:00:00Z\"}");
        Directory.CreateSymbolicLink(Path.Combine(root.Path, "updates"), target.Path);
        var store = Store(root);

        Assert.Null(store.ReadPending());
        var ex = Assert.Throws<IOException>(() => store.Record(UpdatePhase.Failed, "x", null, null, null));
        Assert.Contains("symbolic link", ex.Message);
        Assert.Equal(["pending-update.json"], Directory.GetFiles(target.Path).Select(Path.GetFileName));
    }

    [NonWindowsFact]
    public void WritingOverAPathSweptForASymbolicLink_ReplacesTheLink_NotWhatItPointedAt()
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

    // --- the two trust levels ----------------------------------------------------------------------------------

    [Fact]
    public void WhatDecidesAnUpdate_LivesInRootsDirectory_AndWhatTheServiceWrites_InTheServices()
    {
        using var root = new TempDirectory();
        using var privileged = new TempDirectory();
        var store = Store(root, privileged);

        store.WritePending(Pending());
        store.WriteConfirmed("2026.9.19.1432-snapshot.g65615e7");
        store.Record(UpdatePhase.Restarting, "waiting", "1.0", "2.0", null);
        store.WriteApplied(Applied());

        var service = Path.Combine(root.Path, "updates");
        Assert.Equal(["confirmed-update.json", "pending-update.json", "update-state.json"], Directory.GetFiles(service).Select(Path.GetFileName).Order());
        Assert.Equal(["applied-update.json"], Directory.GetFiles(privileged.Path).Select(Path.GetFileName));
    }

    [NonWindowsFact]
    public void ThePrivilegedDirectory_IsCreatedReadableByAll_AndWritableOnlyByItsCreator()
    {
        using var root = new TempDirectory();
        var privileged = Path.Combine(root.Path, "priv");
        var store = new UpdateStateStore(new UpdateWorkspace(root.Path, privileged));

        store.WriteApplied(Applied());

        var mode = File.GetUnixFileMode(privileged);
        Assert.False(mode.HasFlag(UnixFileMode.GroupWrite));
        Assert.False(mode.HasFlag(UnixFileMode.OtherWrite));
        Assert.True(mode.HasFlag(UnixFileMode.OtherRead));
    }

    [Fact]
    public void WithoutABoundary_BothAreTheSameDirectory()
    {
        // GetFullPath because the workspace normalizes: "/var/lib/..." is rooted on the current drive on Windows.
        var dataRoot = Path.GetFullPath("/var/lib/dbdatasync");
        var workspace = new UpdateWorkspace(dataRoot);

        Assert.Equal(workspace.Directory, workspace.PrivilegedDirectory);
        Assert.Equal(Path.Combine(dataRoot, "updates", "pending-update.json"), workspace.PendingPath);
        Assert.Equal(Path.Combine(dataRoot, "updates", "applied-update.json"), workspace.AppliedPath);
    }

    /// <summary>Found by running the real thing: the CLI was given <c>--state-dir state</c>, the child <c>dotnet</c>
    /// runs from that directory, and a relative <c>--add-source state/rollback</c> became <c>state/state/rollback</c> —
    /// so a rollback uninstalled the new version and could not install the old one.</summary>
    [Fact]
    public void RelativePaths_BecomeAbsolute_BecauseDotnetRunsFromADirectoryOfItsOwn()
    {
        var workspace = new UpdateWorkspace("data", "state");

        Assert.Equal(Path.GetFullPath("data"), workspace.DataRoot);
        Assert.Equal(Path.GetFullPath("state"), workspace.PrivilegedDirectory);
        Assert.True(Path.IsPathRooted(workspace.RollbackDirectory));
        Assert.True(Path.IsPathRooted(workspace.PendingPath));
        Assert.True(Path.IsPathRooted(new UpdateWorkspace("data").PrivilegedDirectory));
    }

    [Fact]
    public void TheDefaultPrivilegedDirectory_IsOutsideTheDataRoot_WhichTheServicesUserOwns()
    {
        Assert.Equal("/var/lib/dbdatasync-update", UpdateWorkspace.DefaultPrivilegedDirectory);
        Assert.False(UpdateWorkspace.DefaultPrivilegedDirectory.StartsWith("/var/lib/dbdatasync/", StringComparison.Ordinal));
    }

    // --- state ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Record_ANonTerminalPhase_SetsCurrent_WithoutTouchingHistory()
    {
        using var root = new TempDirectory();
        var store = Store(root);

        store.Record(UpdatePhase.Draining, "Waiting for runs to finish.", "1.0", "2.0", "dan");

        var state = store.ReadState();
        Assert.Equal(UpdatePhase.Draining, state.Current!.Phase);
        Assert.Equal("Waiting for runs to finish.", state.Current.Message);
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
    public void Writes_LeaveNoTempFilesBehind_AndReplaceAnExistingFile()
    {
        using var root = new TempDirectory();
        var store = Store(root);

        store.WritePending(Pending("2026.9.19.100"));
        store.WritePending(Pending("2026.9.19.200"));

        Assert.Equal("2026.9.19.200", store.ReadPending()!.TargetVersion);
        Assert.Empty(Directory.GetFiles(store.Workspace.Directory, "*.tmp"));
    }

    [NonWindowsFact]
    public void AFileTheWriterDidNotCreateCanStillBeReplaced_WhichIsWhatARootOwnedFileNeeds()
    {
        // The privileged step and the service are different users: replacing by rename needs only the
        // directory, so a file made by one can be replaced by the other. A read-only file stands in for that.
        using var root = new TempDirectory();
        var store = Store(root);
        store.WritePending(Pending("2026.9.19.100"));
        File.SetAttributes(store.Workspace.PendingPath, FileAttributes.ReadOnly);
        try
        {
            File.SetUnixFileMode(store.Workspace.PendingPath, UnixFileMode.UserRead);

            store.WritePending(Pending("2026.9.19.200"));

            Assert.Equal("2026.9.19.200", store.ReadPending()!.TargetVersion);
        }
        finally
        {
            try { File.SetAttributes(store.Workspace.PendingPath, FileAttributes.Normal); } catch (IOException) { }
        }
    }

    [Fact]
    public void NoGitignoreIsTouched_BecauseTheDataRootIsNotARepository()
    {
        using var root = new TempDirectory();
        var store = Store(root);

        store.WritePending(Pending());

        Assert.False(File.Exists(Path.Combine(root.Path, ".gitignore")));
    }
}
