using DbDataSync.Updates;
using Xunit;

namespace DbDataSync.Cli.Tests;

/// <summary>
/// <c>dbdatasync internal apply-update</c> is what the systemd unit runs as root before every start. It reads a
/// file the unprivileged service wrote, so it does nothing unless it really is root — otherwise there is no
/// boundary it is protecting, and it would be installing on the word of whoever could write that file.
/// </summary>
public sealed class InternalApplyUpdateTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-internal-update-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static async Task<(int Exit, string Out)> RunAsync(params string[] args)
    {
        var original = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            return (await InternalCommand.RunAsync(["apply-update", .. args]), writer.ToString());
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    [Fact]
    public async Task NotRunningAsRoot_NothingIsApplied_TheRequestIsLeftAlone_AndItExitsZero()
    {
        if (Environment.IsPrivilegedProcess)
            return; // Running as root, where it is meant to act; there is nothing to refuse.

        var store = new UpdateStateStore(new UpdateWorkspace(_root));
        store.WritePending(new PendingUpdate("2026.9.18.1918", DateTimeOffset.UtcNow, "mallory"));

        var (exit, output) = await RunAsync("--repo", _root);

        Assert.Equal(0, exit);
        Assert.Contains("not running as root, so nothing was applied", output);
        Assert.NotNull(store.ReadPending());
        Assert.Null(store.ReadApplied());
        Assert.Null(store.ReadState().Current);
    }

    [Fact]
    public async Task WithNothingPending_AsTheCurrentUser_ItIsAQuietNoOp()
    {
        var (exit, output) = await RunAsync("--repo", _root, "--as-current-user", "--state-dir", Path.Combine(_root, "priv"));

        Assert.Equal(0, exit);
        Assert.Equal("", output);
    }

    [Fact]
    public async Task ARequestItCannotAct_OnIsRecordedAsFailed_NotThrown_AndItStillExitsZero()
    {
        // Not a dotnet tool install (this is the test host), so there is nothing to update in place: refused
        // before any network call is made, recorded, and the service is still free to start.
        var store = new UpdateStateStore(new UpdateWorkspace(_root));
        store.WritePending(new PendingUpdate("2026.9.18.1918", DateTimeOffset.UtcNow, "dan"));

        var (exit, output) = await RunAsync("--repo", _root, "--as-current-user", "--state-dir", Path.Combine(_root, "priv"));

        Assert.Equal(0, exit);
        Assert.Contains("not a dotnet tool install", output);
        Assert.Null(store.ReadPending());
        Assert.Equal(UpdatePhase.Failed, store.ReadState().Current!.Phase);
    }
}
