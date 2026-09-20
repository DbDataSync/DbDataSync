namespace DbDataSync.Updates.Tests;

public class UpdateCommandsTests
{
    private static UpdatePlan Plan(string? installed, string target, ReleaseChannel channel, InstallLocation location, string? source = null) =>
        UpdatePlanner.Build(
            installed is null ? null : ReleaseVersion.Parse(installed),
            new ReleaseInfo(ReleaseVersion.Parse(target), channel), location, source, ServiceSituation.None, false);

    private static readonly InstallLocation ToolPath = new(InstallKind.ToolPath, "/opt/dbdatasync");
    private static readonly InstallLocation Global = new(InstallKind.Global, "/home/dan/.dotnet/tools");

    private static string[] Lines(IReadOnlyList<ToolInvocation> steps) => steps.Select(s => $"{s.Purpose}: {string.Join(' ', s.Arguments)}").ToArray();

    [Fact]
    public void AnUpdate_IsOneCommand()
    {
        Assert.Equal(
            ["update: tool update --tool-path /opt/dbdatasync DbDataSync --version 2026.9.18.1918"],
            Lines(UpdateCommands.Install(Plan("2026.9.16.1005", "2026.9.18.1918", ReleaseChannel.Stable, ToolPath))));
    }

    [Fact]
    public void AGlobalInstall_UsesTheGlobalFlag()
    {
        Assert.Equal(
            ["update: tool update --global DbDataSync --version 2026.9.18.1918"],
            Lines(UpdateCommands.Install(Plan("2026.9.16.1005", "2026.9.18.1918", ReleaseChannel.Stable, Global))));
    }

    [Fact]
    public void ASnapshot_AddsItsStagedFolderAsASource()
    {
        Assert.Equal(
            ["update: tool update --tool-path /opt/dbdatasync DbDataSync --add-source /stage/x --version 2026.9.19.1432-snapshot.g65615e7"],
            Lines(UpdateCommands.Install(Plan("2026.9.18.1918", "2026.9.19.1432-snapshot.g65615e7", ReleaseChannel.Snapshot, ToolPath, "/stage/x"))));
    }

    [Fact]
    public void ALowerVersion_IsAnUninstallThenAnInstall()
    {
        Assert.Equal(
            [
                "remove: tool uninstall --tool-path /opt/dbdatasync DbDataSync",
                "install: tool install --tool-path /opt/dbdatasync DbDataSync --version 2026.9.11.532",
            ],
            Lines(UpdateCommands.Install(Plan("2026.9.16.1005", "2026.9.11.532", ReleaseChannel.Stable, ToolPath))));
    }

    [Fact]
    public void TheVersionAlreadyInstalled_IsNoCommands()
    {
        Assert.Empty(UpdateCommands.Install(Plan("2026.9.18.1918", "2026.9.18.1918", ReleaseChannel.Stable, ToolPath)));
    }

    private static UpdateRequest Applied(string? previous, InstallKind kind = InstallKind.ToolPath) => new(
        "2026.9.19.1432-snapshot.g65615e7", previous, ReleaseChannel.Snapshot, kind,
        kind == InstallKind.Global ? "/home/dan/.dotnet/tools" : "/opt/dbdatasync", "/stage/x", DateTimeOffset.UnixEpoch, null);

    [Fact]
    public void Rollback_FromAKeptPackage_UsesItAsASource()
    {
        Assert.Equal(
            [
                "remove: tool uninstall --tool-path /opt/dbdatasync DbDataSync",
                "install: tool install --tool-path /opt/dbdatasync DbDataSync --add-source /var/lib/dbdatasync/updates/rollback --version 2026.9.18.1918",
            ],
            Lines(UpdateCommands.Rollback(Applied("2026.9.18.1918"), "/var/lib/dbdatasync/updates/rollback")));
    }

    [Fact]
    public void Rollback_WithNoKeptPackage_InstallsByExactVersionFromNuGet()
    {
        Assert.Equal(
            [
                "remove: tool uninstall --global DbDataSync",
                "install: tool install --global DbDataSync --version 2026.9.18.1918",
            ],
            Lines(UpdateCommands.Rollback(Applied("2026.9.18.1918", InstallKind.Global), null)));
    }
}
