namespace DbDataSync.TaskRunner.Tests;

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
    /// cause was buried mid-log.
    /// </para>
    /// <para>
    /// If the retries are still losing after five seconds this no longer throws — it warns to stderr,
    /// naming the directory and the files still holding it open, and leaves the directory for the OS
    /// to reclaim. A leaked directory under the OS temp path is what that path is for; failing the job
    /// over it turned an all-green test run red for a cleanup step nothing under test actually depends
    /// on, and for a loaded assembly's own file (a non-collectible load context, confirmed) the retry
    /// could never have won regardless — the wait was for something that only happens at process exit.
    /// The message is still the point: a human reads a cause instead of a bare exception type, just
    /// without the job dying to deliver it. See
    /// architecture/planning/todo/follow-up-a-temp-dir-that-cannot-be-deleted-fails-a-job-whose-tests-all-passed.md.
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
                    // Don't fail the run over a leaked temp directory — the OS temp path is exactly
                    // what cleans these up eventually, and for a loaded assembly's own file (confirmed
                    // cause: a non-collectible AssemblyLoadContext holds it for the process's whole
                    // life) no amount of retrying was ever going to win. Loud and non-fatal is what the
                    // evidence supports: everything under test passed, only cleanup couldn't finish.
                    // See architecture/planning/todo/follow-up-a-temp-dir-that-cannot-be-deleted-fails-a-job-whose-tests-all-passed.md.
                    var locked = StillOpen(path);
                    Console.Error.WriteLine(
                        $"WARNING: could not delete the temp directory '{path}' after retrying for 5s. "
                        + (locked.Count == 0
                            ? "No individual file reported as locked, so the directory itself is likely the one held open."
                            : $"Still open: {string.Join(", ", locked)}.")
                        + $" Underlying error: {ex.Message}. Leaving it for the OS to reclaim.");
                    return;
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
