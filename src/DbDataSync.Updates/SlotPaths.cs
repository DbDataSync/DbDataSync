using System.Runtime.InteropServices;

namespace DbDataSync.Updates;

/// <summary>
/// The on-disk layout of a slot install (phase 196L), written down once:
/// <code>
/// &lt;tool dir&gt;/dbdatasync[.exe]   the launcher — what the service and PATH run
/// &lt;tool dir&gt;/current.txt        "a" or "b"
/// &lt;tool dir&gt;/versions/a/        a `dotnet tool install --tool-path` root
/// &lt;tool dir&gt;/versions/b/
/// </code>
/// <para>
/// **Compiled into two assemblies**: this one, and <c>DbDataSync.Launcher</c> as a linked file. The launcher has
/// no references at all — so an ordinary update never has to replace it — yet it and the updater must agree
/// exactly on where a slot's payload is. Keep this file to the BCL for that reason.
/// </para>
/// </summary>
public static class SlotPaths
{
    public const string PointerFileName = "current.txt";

    public const string VersionsDirectoryName = "versions";

    /// <summary>The payload's own assembly — what the launcher loads and calls.</summary>
    public const string PayloadAssemblyName = "DbDataSync.Cli.dll";

    /// <summary>Where each package carries its launchers, one directory per RID, beside the payload.</summary>
    public const string LauncherDirectoryName = "launcher";

    /// <summary>The launcher's own assembly name, and so its apphost's (<c>dbdatasync</c>/<c>dbdatasync.exe</c>).</summary>
    public const string LauncherName = "dbdatasync";

    /// <summary><see cref="AppContext"/> data the launcher sets before calling in: the tool directory.</summary>
    public const string RootDataKey = "DbDataSync.Launcher.Root";

    /// <summary><see cref="AppContext"/> data the launcher sets before calling in: the slot it loaded.</summary>
    public const string SlotDataKey = "DbDataSync.Launcher.Slot";

    public const string SlotA = "a";
    public const string SlotB = "b";

    public static string PointerPath(string root) => Path.Combine(root, PointerFileName);

    public static string SlotDirectory(string root, string slot) => Path.Combine(root, VersionsDirectoryName, slot);

    public static string Other(string slot) => slot == SlotA ? SlotB : SlotA;

    /// <summary>The slot a pointer file's text names, or null for anything but <c>a</c> or <c>b</c> (surrounding
    /// whitespace allowed — an editor's trailing newline must not stop a service starting).</summary>
    public static string? ParseSlot(string? text) =>
        text?.Trim().ToLowerInvariant() switch
        {
            SlotA => SlotA,
            SlotB => SlotB,
            _ => null,
        };

    /// <summary>The slot the pointer names; null when there is no pointer or it names neither slot.</summary>
    public static string? ReadPointer(string root)
    {
        var path = PointerPath(root);
        return File.Exists(path) ? ParseSlot(File.ReadAllText(path)) : null;
    }

    /// <summary>The versions installed in a slot — the <c>.store/dbdatasync/&lt;version&gt;</c> directory names
    /// <c>dotnet tool</c> lays down. Normally exactly one.</summary>
    public static IReadOnlyList<string> InstalledVersions(string slotDirectory)
    {
        var store = Path.Combine(slotDirectory, ".store", "dbdatasync");
        return Directory.Exists(store)
            ? Directory.EnumerateDirectories(store).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToList()
            : [];
    }

    /// <summary>
    /// Every payload assembly in a slot:
    /// <c>.store/dbdatasync/&lt;v&gt;/dbdatasync/&lt;v&gt;/tools/&lt;tfm&gt;/any/DbDataSync.Cli.dll</c>, the layout
    /// <c>dotnet tool install --tool-path</c> produces (checked against a real install, 2026-09-28). Normally exactly
    /// one; the caller decides what zero or several mean.
    /// </summary>
    public static IReadOnlyList<string> FindPayloads(string slotDirectory)
    {
        var found = new List<string>();
        foreach (var version in InstalledVersions(slotDirectory))
        {
            var tools = Path.Combine(slotDirectory, ".store", "dbdatasync", version, "dbdatasync", version, "tools");
            if (!Directory.Exists(tools))
                continue;

            foreach (var framework in Directory.EnumerateDirectories(tools))
            {
                var candidate = Path.Combine(framework, "any", PayloadAssemblyName);
                if (File.Exists(candidate))
                    found.Add(candidate);
            }
        }

        return found;
    }

    /// <summary>
    /// The portable RID the package's launcher directories are named by (<c>linux-x64</c>, <c>win-arm64</c>,
    /// <c>linux-musl-x64</c> …). Worked out from the OS and architecture, **not** from
    /// <see cref="RuntimeInformation.RuntimeIdentifier"/>: a distribution's source-built .NET reports its own
    /// non-portable RID there (<c>ubuntu.24.04-x64</c> on the machine this was written on). Null for a platform
    /// no launcher is built for.
    /// </summary>
    public static string? PortableRuntimeIdentifier()
    {
        var architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => null,
        };
        if (architecture is null)
            return null;

        if (OperatingSystem.IsWindows())
            return "win-" + architecture;
        if (OperatingSystem.IsMacOS())
            return "osx-" + architecture;
        if (OperatingSystem.IsLinux())
            return (IsMusl() ? "linux-musl-" : "linux-") + architecture;
        return null;
    }

    private static bool IsMusl() =>
        RuntimeInformation.RuntimeIdentifier.Contains("musl", StringComparison.OrdinalIgnoreCase)
        || (Directory.Exists("/lib") && Directory.EnumerateFiles("/lib", "ld-musl-*").Any());
}
