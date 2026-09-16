namespace DbDataSync.Cli.Tests;

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
    /// <summary>
    /// Read-only attributes are only half of what stops a delete on Windows; the other half is an open
    /// handle, which fails with <see cref="IOException"/> rather than
    /// <see cref="UnauthorizedAccessException"/> and which clearing attributes does nothing for. At the
    /// end of a test that ran a real host, a handle can still be closing — a pooled SQLite connection,
    /// libgit2's pack files, a spawned worker process on its way out — so this retries rather than
    /// failing the first time it loses that race.
    /// <para>
    /// Worth the retry loop because of how the failure presented: xUnit reports a fixture teardown
    /// throw as a "Test Class Cleanup Failure", which sets the run's exit code **without appearing in
    /// any project's pass/fail counts**. A CI job where every project printed `Passed!` went red for
    /// three consecutive runs with nothing in the summary to explain it, and the one line naming the
    /// cause was buried mid-log. If the retries are still losing after five seconds this throws — but
    /// names the directory and the files still holding it open, so the next person reads a cause
    /// instead of a bare exception type.
    /// </para>
    /// </summary>
    public static void DeleteRecursively(string path)
    {
        if (!Directory.Exists(path))
            return;

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);

                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    var locked = StillOpen(path);
                    throw new IOException(
                        $"Could not delete the temp directory '{path}' after retrying for 5s. "
                        + (locked.Count == 0
                            ? "No individual file reported as locked, so the directory itself is likely the one held open."
                            : $"Still open: {string.Join(", ", locked)}.")
                        + $" Underlying error: {ex.Message}",
                        ex);
                }

                Thread.Sleep(attempt < 10 ? 10 : 100);
            }
        }
    }

    /// <summary>Which files under <paramref name="root"/> another handle still has open — probed by
    /// trying to open each one exclusively, which is the only portable way to ask.</summary>
    private static List<string> StillOpen(string root)
    {
        var locked = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    using var probe = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    locked.Add(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Enumeration itself can fail mid-teardown; the caller's message still names the directory.
        }

        return locked;
    }
}
