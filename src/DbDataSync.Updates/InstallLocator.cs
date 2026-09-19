namespace DbDataSync.Updates;

public enum InstallKind
{
    /// <summary><c>dotnet tool install --global</c>: the per-user tool directory.</summary>
    Global,

    /// <summary><c>dotnet tool install --tool-path &lt;dir&gt;</c> — including the machine-wide copy
    /// <c>dbdatasync tool install</c> puts on the PATH.</summary>
    ToolPath,

    /// <summary>A container image. An update there is a new image tag, not a package.</summary>
    Container,

    /// <summary>Anything else: a development build, <c>dotnet run</c>, a copy of the binaries somebody
    /// unpacked. There is no tool store to update.</summary>
    NotAToolInstall,
}

/// <param name="ToolRoot">The directory <c>dotnet tool</c> was pointed at — the one holding <c>.store</c> and
/// the command shims. Null unless <see cref="Kind"/> is <see cref="InstallKind.ToolPath"/> or
/// <see cref="InstallKind.Global"/>.</param>
public sealed record InstallLocation(InstallKind Kind, string? ToolRoot);

/// <summary>
/// Works out how this copy of the tool was installed, from where its own assemblies are.
/// <para>
/// A <c>dotnet tool</c> lays a package out as <c>&lt;root&gt;/.store/&lt;id&gt;/&lt;version&gt;/&lt;id&gt;/&lt;version&gt;/tools/…</c>,
/// with only a small shim in <c>&lt;root&gt;</c> itself — which is why this looks at the assembly directory and
/// not <see cref="Environment.ProcessPath"/> (the shim). Pure, taking its inputs as arguments, so every
/// layout — including Windows paths — is testable on any platform.
/// </para>
/// </summary>
public static class InstallLocator
{
    private const string StoreMarker = "/.store/dbdatasync/";

    public static InstallLocation Locate(string baseDirectory, string globalToolsDirectory, bool inContainer)
    {
        if (inContainer)
            return new InstallLocation(InstallKind.Container, null);

        var normalized = baseDirectory.Replace('\\', '/');
        var at = normalized.LastIndexOf(StoreMarker, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
            return new InstallLocation(InstallKind.NotAToolInstall, null);

        var root = baseDirectory[..at];
        if (root.Length == 0)
            root = baseDirectory[..1];

        var isGlobal = string.Equals(Trim(root), Trim(globalToolsDirectory), StringComparison.OrdinalIgnoreCase);
        return new InstallLocation(isGlobal ? InstallKind.Global : InstallKind.ToolPath, root);
    }

    private static string Trim(string path) => path.Replace('\\', '/').TrimEnd('/');
}
