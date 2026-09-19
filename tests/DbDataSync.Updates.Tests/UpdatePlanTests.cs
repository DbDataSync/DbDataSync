namespace DbDataSync.Updates.Tests;

public class UpdatePlanTests
{
    private static readonly ReleaseInfo Stable = new(ReleaseVersion.Parse("2026.9.18.1918"), ReleaseChannel.Stable);
    private static readonly ReleaseInfo Beta = new(ReleaseVersion.Parse("2026.9.12.721-beta"), ReleaseChannel.Beta);
    private static readonly ReleaseInfo Snapshot = new(ReleaseVersion.Parse("2026.9.19.1432-snapshot.g65615e7"), ReleaseChannel.Snapshot);

    private static readonly InstallLocation ToolPath = new(InstallKind.ToolPath, "/opt/dbdatasync");
    private static readonly InstallLocation Global = new(InstallKind.Global, "/home/dan/.dotnet/tools");

    private static UpdatePlan Plan(
        string? installed, ReleaseInfo target, InstallLocation? location = null, string? staged = null,
        ServiceSituation? service = null, bool elevated = false) =>
        UpdatePlanner.Build(
            installed is null ? null : ReleaseVersion.Parse(installed),
            target, location ?? ToolPath, staged, service ?? ServiceSituation.None, elevated);

    // --- choosing the operation -------------------------------------------------------------------------

    [Fact]
    public void ANewerTarget_IsAnUpdate()
    {
        Assert.Equal(PlanOperation.Update, Plan("2026.9.16.1005", Stable).Operation);
    }

    [Fact]
    public void AnOlderTarget_IsAReinstall_BecauseUpdateWillNotGoDown()
    {
        Assert.Equal(PlanOperation.Reinstall, Plan("2026.9.19.100", Stable).Operation);
    }

    [Fact]
    public void TheSameVersion_IsAlreadyInstalled_EvenWithABuildSuffixOnWhatIsRunning()
    {
        Assert.Equal(PlanOperation.AlreadyInstalled, Plan("2026.9.18.1918+3462587066f4d3f3d49b675694f58a1c348bccfd", Stable).Operation);
    }

    [Fact]
    public void ABetaOfTheInstalledCore_IsBelowItsStable_SoInstallingItIsAReinstall()
    {
        Assert.Equal(PlanOperation.Reinstall, Plan("2026.9.12.721", Beta).Operation);
    }

    [Fact]
    public void AnUnreadableInstalledVersion_IsTreatedAsAnUpdate()
    {
        Assert.Equal(PlanOperation.Update, Plan(null, Stable).Operation);
    }

    [Fact]
    public void ASnapshot_MustBeStagedBeforeItCanBePlanned()
    {
        Assert.Throws<ArgumentException>(() => Plan("2026.9.18.1918", Snapshot));
    }

    [Fact]
    public void OnlyASnapshotCarriesASourceDirectory()
    {
        Assert.Equal("/stage/x", Plan("2026.9.18.1918", Snapshot, staged: "/stage/x").SourceDirectory);
        Assert.Null(Plan("2026.9.12.100", Stable, staged: "/stage/x").SourceDirectory);
    }

    [Theory]
    [InlineData(InstallKind.Global, true)]
    [InlineData(InstallKind.ToolPath, true)]
    [InlineData(InstallKind.Container, false)]
    [InlineData(InstallKind.NotAToolInstall, false)]
    public void IsUpdatable_MeansThereIsAToolStoreToActOn(InstallKind kind, bool updatable)
    {
        Assert.Equal(updatable, Plan("2026.9.16.1005", Stable, new InstallLocation(kind, null)).IsUpdatable);
    }

    // --- what is printed ---------------------------------------------------------------------------------

    [Fact]
    public void Render_AStableUpdate_OfAMachineWideInstall_UnderSystemd()
    {
        var plan = Plan("2026.9.16.1005", Stable, ToolPath, service: new(ServiceManager.Systemd, "dbdatasync"), elevated: true);

        Assert.Equal(
            """
            Installed   2026.9.16.1005
            Selected    2026.9.18.1918  (stable)

            Nothing has been changed. To install it:

              1. Stop the service
                   sudo systemctl stop dbdatasync
              2. Update
                   sudo dotnet tool update --tool-path /opt/dbdatasync DbDataSync --version 2026.9.18.1918
              3. Start the service
                   sudo systemctl start dbdatasync
              4. Check
                   dbdatasync version

            """.ReplaceLineEndings("\n"),
            UpdatePlanRenderer.Render(plan, isWindows: false));
    }

    [Fact]
    public void Render_ASnapshot_OfAPerUserInstall_WithNoService_UsesTheStagedFolderAsASource()
    {
        var plan = Plan("2026.9.18.1918", Snapshot, Global, staged: "/home/dan/.local/share/DbDataSync/updates/2026.9.19.1432-snapshot.g65615e7");

        Assert.Equal(
            """
            Installed   2026.9.18.1918
            Selected    2026.9.19.1432-snapshot.g65615e7  (snapshot)
            Staged      /home/dan/.local/share/DbDataSync/updates/2026.9.19.1432-snapshot.g65615e7  (checksum verified)

            Nothing has been changed. To install it:

              1. Stop any running `dbdatasync serve` — files that are in use cannot be replaced
              2. Update
                   dotnet tool update --global DbDataSync --add-source /home/dan/.local/share/DbDataSync/updates/2026.9.19.1432-snapshot.g65615e7 --version 2026.9.19.1432-snapshot.g65615e7
              3. Check
                   dbdatasync version

            """.ReplaceLineEndings("\n"),
            UpdatePlanRenderer.Render(plan, isWindows: false));
    }

    [Fact]
    public void Render_ADowngrade_OnWindows_UnderAWindowsService_UninstallsFirst_AndQuotesAPathWithSpaces()
    {
        var plan = Plan(
            "2026.9.19.100", Stable, new InstallLocation(InstallKind.ToolPath, @"C:\Program Files\DbDataSync"),
            service: new(ServiceManager.WindowsService, "DbDataSync"), elevated: true);

        Assert.Equal(
            """
            Installed   2026.9.19.100
            Selected    2026.9.18.1918  (stable)

            Nothing has been changed. To install it:

              1. Stop the service (from an elevated prompt)
                   sc.exe stop DbDataSync
              2. Remove the installed version — `dotnet tool update` will not go down to an older one (from an elevated prompt)
                   dotnet tool uninstall --tool-path "C:\Program Files\DbDataSync" DbDataSync
              3. Install (from an elevated prompt)
                   dotnet tool install --tool-path "C:\Program Files\DbDataSync" DbDataSync --version 2026.9.18.1918
              4. Start the service (from an elevated prompt)
                   sc.exe start DbDataSync
              5. Check
                   dbdatasync version

            """.ReplaceLineEndings("\n"),
            UpdatePlanRenderer.Render(plan, isWindows: true));
    }

    [Fact]
    public void Render_ABeta_NamesTheExactVersion_SoNoPrereleaseFlagIsNeeded()
    {
        var plan = Plan("2026.9.11.532", Beta, Global);

        var text = UpdatePlanRenderer.Render(plan, isWindows: false);

        Assert.Contains("dotnet tool update --global DbDataSync --version 2026.9.12.721-beta", text);
        Assert.DoesNotContain("--prerelease", text);
        Assert.DoesNotContain("--add-source", text);
    }

    [Fact]
    public void Render_APerUserInstall_NeverNeedsSudoOrElevation()
    {
        var plan = Plan("2026.9.16.1005", Stable, Global, elevated: false);

        Assert.DoesNotContain("sudo", UpdatePlanRenderer.Render(plan, isWindows: false));
        Assert.DoesNotContain("elevated", UpdatePlanRenderer.Render(plan, isWindows: true));
    }

    [Fact]
    public void Render_TheSameVersion_SaysThereIsNothingToDo()
    {
        var plan = Plan("2026.9.18.1918", Stable);

        Assert.Equal(
            """
            Installed   2026.9.18.1918
            Selected    2026.9.18.1918  (stable)

            2026.9.18.1918 is already installed — nothing to do.

            """.ReplaceLineEndings("\n"),
            UpdatePlanRenderer.Render(plan, isWindows: false));
    }

    [Fact]
    public void Render_ADevelopmentBuild_DeclinesToPrintInstallCommands_ButSaysWhereAStagedSnapshotIs()
    {
        var plan = Plan(
            "2026.9.19.1432-alpha.1789", Snapshot, new InstallLocation(InstallKind.NotAToolInstall, null),
            staged: "/stage/2026.9.19.1432-snapshot.g65615e7");

        var text = UpdatePlanRenderer.Render(plan, isWindows: false);

        Assert.Contains("was not installed as a dotnet tool", text);
        Assert.Contains("The staged snapshot is in /stage/2026.9.19.1432-snapshot.g65615e7", text);
        Assert.DoesNotContain("dotnet tool update", text);
        Assert.DoesNotContain("Nothing has been changed", text);
    }

    [Fact]
    public void Render_AContainer_PointsAtAnImageTagInsteadOfAPackage()
    {
        var plan = Plan("2026.9.16.1005", Stable, new InstallLocation(InstallKind.Container, null));

        var text = UpdatePlanRenderer.Render(plan, isWindows: false);

        Assert.Contains("container image", text);
        Assert.Contains("newer image tag", text);
        Assert.DoesNotContain("dotnet tool update", text);
    }

    [Fact]
    public void Render_AnUnknownInstalledVersion_SaysSo()
    {
        var text = UpdatePlanRenderer.Render(Plan(null, Stable), isWindows: false);

        Assert.StartsWith("Installed   (unknown)\n", text);
    }

    [Fact]
    public void Render_UsesUnixLineEndingsOnEveryPlatform()
    {
        Assert.DoesNotContain('\r', UpdatePlanRenderer.Render(Plan("2026.9.16.1005", Stable), isWindows: true));
    }
}
