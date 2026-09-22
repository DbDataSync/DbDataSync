namespace DbDataSync.Libraries;

/// <summary>
/// The on-disk layout for <c>&lt;repo&gt;/files/</c> — a standard place for user-provided files (a JDBC
/// driver jar, so far the only real case), sitting alongside <see cref="LibraryPaths.LibrariesDir"/> and
/// <c>drivers/</c> but distinct from both: not NuGet-package-shaped like a library, not a descriptor like
/// a driver. See <c>architecture/planning/todo/user-provided-files-store.md</c> for the full design —
/// this class is the one piece of it phase 169V needs early, to resolve a <c>driver.yaml</c>'s
/// <c>jdbc.driverJarPaths</c> entries (names, not paths — see that field's own doc comment) against real
/// files on disk. The store's own API/GUI (list/upload/delete) is separate, later work built on top of
/// this same convention, not part of what phase 169V needed.
/// <para>
/// Flat, not per-driver-scoped — a name is unique across the whole store, the same posture
/// <see cref="LibraryPaths.LibraryDir"/> already takes for library ids.
/// </para>
/// </summary>
public static class FilesPaths
{
    public static string FilesDir(string repoRoot) => Path.Combine(repoRoot, "files");
    public static string FilePath(string repoRoot, string name) => Path.Combine(FilesDir(repoRoot), name);
}
