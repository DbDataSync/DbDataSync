namespace DataSync.Certificates.Tests;

/// <summary>Mirrors <c>DataSync.Cli.Tests.GitTempDirectory</c> — libgit2 writes its object files
/// read-only, and plain <see cref="Directory.Delete(string, bool)"/> refuses to remove a read-only file
/// on Windows. Duplicated rather than shared: there is no common test-support project between the two
/// test assemblies to put a single copy in, and this is twelve lines.</summary>
internal static class GitTempDirectory
{
    public static void DeleteRecursively(string path)
    {
        if (!Directory.Exists(path))
            return;

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);

        Directory.Delete(path, recursive: true);
    }
}
