using System.Text.Json;

namespace DataSync.Certificates;

/// <param name="RequestId">The CA's own request id — what <c>datasync cert retrieve --request-id</c> is
/// given.</param>
/// <param name="KeyName">The <see cref="PendingEnrollmentKeys"/> container holding the private key this
/// request's CSR was built with.</param>
public sealed record PendingEnrollment(
    string RequestId, string KeyName, string SubjectCommonName, IReadOnlyList<string> DnsNames,
    string CaConfig, DateTimeOffset SubmittedAtUtc);

/// <summary>
/// Correlates a CA request id back to the <see cref="PendingEnrollmentKeys"/> container holding its
/// private key — see that class's own doc comment for why this bookkeeping exists at all.
/// <para>
/// **Deliberately not <c>datasync.config.yaml</c>, and never git-committed.** This is per-machine,
/// transient operational state (a key name meaningless on any machine but this one, for a request that
/// will not exist once retrieved) — the opposite of what belongs in a git-tracked config file meant to
/// be reviewed and diffed. A plain JSON file beside it, at the repo root but never staged through
/// <c>GitCommitService</c>, is the right weight for state nothing outside this one machine's CNG key
/// store can use anyway.
/// </para>
/// <para>
/// **Not marked <c>[SupportedOSPlatform("windows")]</c>** — reading and writing a JSON file has nothing
/// OS-specific about it; only its caller (<c>CertCommand</c>, always behind the CLI's own Windows gate)
/// ever has a reason to call it.
/// </para>
/// </summary>
public static class PendingEnrollmentStore
{
    private const string FileName = "pending-certificate-enrollments.json";

    private static string PathIn(string repoRoot) => Path.Combine(repoRoot, FileName);

    public static void Save(string repoRoot, PendingEnrollment enrollment)
    {
        var all = ReadAll(repoRoot).Where(e => e.RequestId != enrollment.RequestId).ToList();
        all.Add(enrollment);
        File.WriteAllText(PathIn(repoRoot), JsonSerializer.Serialize(all, JsonOptions));
        EnsureGitignored(repoRoot);
    }

    /// <summary>
    /// The repo root is a git repository (<c>ServeCommand.Prepare</c>), and this file is deliberately
    /// never committed to it — see the class doc comment. Without an ignore entry, every
    /// <c>git status</c> an operator runs would show this file as untracked for as long as any
    /// enrollment has ever been pending, which reads as something forgotten rather than something
    /// intentional. Appended once, the first time this store is actually used, rather than by
    /// <c>ServeCommand.Prepare</c> up front — a repo that never enrolls a certificate never needs the
    /// line, and this keeps that decision local to the one file that needs it.
    /// </summary>
    private static void EnsureGitignored(string repoRoot)
    {
        var path = Path.Combine(repoRoot, ".gitignore");
        var existing = File.Exists(path) ? File.ReadAllLines(path) : [];
        if (existing.Contains(FileName))
            return;

        using var writer = new StreamWriter(path, append: true);
        if (existing.Length > 0 && !string.IsNullOrEmpty(existing[^1]))
            writer.WriteLine();
        writer.WriteLine(FileName);
    }

    public static PendingEnrollment? Find(string repoRoot, string requestId) =>
        ReadAll(repoRoot).FirstOrDefault(e => e.RequestId == requestId);

    /// <summary>
    /// Every enrollment still awaiting collection — not in the original phase 82 doc, added for phase
    /// 83's admin screen, which needs to show (and hide) "Retrieve pending request" without already
    /// knowing a request id to look for. <c>datasync cert retrieve</c> never needed this: an operator
    /// running it already has the request id phase 82's own <c>enroll</c> printed to the console.
    /// </summary>
    public static IReadOnlyList<PendingEnrollment> List(string repoRoot) => ReadAll(repoRoot);

    public static void Remove(string repoRoot, string requestId)
    {
        var remaining = ReadAll(repoRoot).Where(e => e.RequestId != requestId).ToList();
        File.WriteAllText(PathIn(repoRoot), JsonSerializer.Serialize(remaining, JsonOptions));
    }

    private static List<PendingEnrollment> ReadAll(string repoRoot)
    {
        var path = PathIn(repoRoot);
        if (!File.Exists(path))
            return [];

        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json))
            return [];

        return JsonSerializer.Deserialize<List<PendingEnrollment>>(json, JsonOptions) ?? [];
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}
