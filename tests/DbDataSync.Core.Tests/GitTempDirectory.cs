namespace DbDataSync.Core.Tests;

/// <summary>
/// Deletes a temp directory that a test pointed <c>LibGit2Sharp.Repository.Init</c> at.
/// <para>
/// libgit2 writes its object files read-only, and plain <see cref="Directory.Delete(string, bool)"/>
/// refuses to remove a read-only file on Windows — an environment quirk of test teardown, not
/// something any test here is actually about, so it's isolated in one place instead of copied into
/// every <c>Dispose()</c> that touches a git repo.
/// </para>
/// </summary>
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
