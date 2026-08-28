namespace DataSync.Verification;

/// <summary>
/// Where a check's result file lives: <c>&lt;state-dir&gt;/verification/&lt;replication&gt;/&lt;runId&gt;/&lt;check&gt;.parquet</c>.
/// <para>
/// Beside the state database, for the same reason the runner's spill journals are: it is the one
/// directory every process in a deployment already agrees on. Grouped by replication and then by run,
/// so a whole run's results can be found — or removed — together, which is what a retention policy
/// will eventually want and what nothing has to guess at meanwhile.
/// </para>
/// </summary>
public static class VerificationPaths
{
    public static string RootFor(string stateDbPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(stateDbPath))!, "verification");

    public static string DirectoryFor(string stateDbPath, string taskName, Guid runId) =>
        Path.Combine(RootFor(stateDbPath), Sanitize(taskName), runId.ToString("N"));

    public static string For(string stateDbPath, string taskName, Guid runId, string checkName) =>
        Path.Combine(DirectoryFor(stateDbPath, taskName, runId), $"{Sanitize(checkName)}.parquet");

    /// <summary>A replication and a check name are both already restricted by <c>ValidateName</c>;
    /// this is belt-and-braces for one that arrived some other way, since these become path
    /// segments.</summary>
    private static string Sanitize(string name) =>
        new(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
}
