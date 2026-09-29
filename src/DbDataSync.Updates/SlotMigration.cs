namespace DbDataSync.Updates;

public sealed record SlotMigrationResult(bool Succeeded, string Message);

/// <summary>
/// Turns a pre-196L <c>dotnet tool install --tool-path &lt;dir&gt;</c> into a slot install **in the same directory**:
/// the running version becomes slot <c>a</c>, the pointer names it, and the launcher replaces the tool's own shim at
/// the same path — so a service registration or PATH entry that named that shim now names the launcher, unchanged.
/// <para>
/// Order matters, because it can be interrupted anywhere: slot <c>a</c> first (harmless on its own), then the pointer
/// (ignored by the legacy shim), then the launcher (which needs both). Run again after an interruption, each step
/// that is already done is skipped.
/// </para>
/// <para>
/// The legacy <c>.store</c> is **not** removed here: the process converting is running from it. A later run from
/// the launcher does it (<see cref="RemoveLegacyStore"/>).
/// </para>
/// </summary>
public sealed class SlotMigration(IToolCommandRunner runner, UpdateStateStore store)
{
    /// <param name="root">The legacy tool root — where the shim and <c>.store</c> are, and where the launcher goes.</param>
    /// <param name="runningVersion">This process's version, which is what the legacy install holds.</param>
    /// <param name="payloadDirectory">This process's own directory, which carries the launcher to install.</param>
    public async Task<SlotMigrationResult> ConvertAsync(string root, string runningVersion, string payloadDirectory, CancellationToken cancellationToken)
    {
        if (!ReleaseVersion.TryParse(runningVersion, out var version))
            return new SlotMigrationResult(false, $"The running version ({runningVersion}) could not be read, so it cannot be put into slot a.");

        var launcher = LauncherInstaller.SourceDirectory(payloadDirectory);
        if (launcher is null)
        {
            return new SlotMigrationResult(false,
                $"This package has no launcher for this platform ({SlotPaths.PortableRuntimeIdentifier() ?? "unsupported"}), so the install " +
                "cannot be converted to slots. Nothing was changed.");
        }

        var layout = new SlotLayout(root);
        store.Log($"converting {root} to slots; {version.Text} becomes slot a");

        // The running version's own package, kept by `dotnet tool` in the legacy store: no network needed. A copy under
        // the canonical file name, which is what a folder feed matches on.
        string? source = null;
        var kept = ToolStore.FindInstalledNupkg(root, version.Text);
        DirectoryInfo? feed = null;
        try
        {
            if (kept is not null)
            {
                feed = Directory.CreateTempSubdirectory("dbdatasync-slot-a-");
                File.Copy(kept, Path.Combine(feed.FullName, $"{ReleaseSources.PackageId}.{version.Text}.nupkg"));
                source = feed.FullName;
            }
            else if (version.Channel is not (ReleaseChannel.Stable or ReleaseChannel.Beta))
            {
                return new SlotMigrationResult(false,
                    $"The package for {version.Text} was not found in {Path.Combine(root, ".store")}, and nuget.org does not have it " +
                    "(only stable and beta versions are there). Nothing was changed.");
            }

            var installed = await new SlotInstaller(runner, store).InstallAsync(layout, SlotPaths.SlotA, version.Text, source, cancellationToken);
            if (!installed.Succeeded)
                return new SlotMigrationResult(false, installed.Message);
        }
        finally
        {
            try
            {
                feed?.Delete(recursive: true);
            }
            catch (IOException)
            {
            }
        }

        if (layout.Current != SlotPaths.SlotA)
            layout.Flip(SlotPaths.SlotA);

        var replaced = LauncherInstaller.Install(launcher, root);
        store.Log($"launcher installed into {root} ({replaced} file(s) replaced)");
        return new SlotMigrationResult(true, $"Converted {root}: {version.Text} is slot a, and the launcher is in place.");
    }

    /// <summary>
    /// The pre-196L install's <c>.store</c>, left behind by <see cref="ConvertAsync"/>. Removed best-effort, and only
    /// from a slot install (never from the legacy one it belongs to). While it is there, <c>dotnet tool list
    /// --tool-path &lt;root&gt;</c> still lists it, and a hand-run <c>dotnet tool update --tool-path &lt;root&gt;</c> would
    /// write a shim over the launcher.
    /// </summary>
    /// <returns>True when it was there and is now gone.</returns>
    public static bool RemoveLegacyStore(SlotLayout layout)
    {
        var store = Path.Combine(layout.Root, ".store");
        if (!layout.Exists || !Directory.Exists(store))
            return false;

        try
        {
            Directory.Delete(store, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
