using LibGit2Sharp;

namespace DbDataSync.Core.Git;

/// <summary>
/// Auto-commit-on-save for the config repo (architecture/detailed-design.md §3.6). Every config
/// write goes through <see cref="CommitChanges"/> immediately after the file(s) hit disk — there is
/// no separate staged/review step in v1.
/// </summary>
public sealed class GitCommitService
{
    private readonly string _repositoryRoot;

    // libgit2's index write takes its own on-disk lock (.git/index.lock) only for the instant it
    // holds the write — too short a window to survive two genuinely concurrent Stage+Commit calls
    // from this same process without collisions (LockedFileException: "the index is locked").
    // GitCommitService is registered as a DI singleton (one instance for the whole API process), and
    // the only writer of this repo is this process (DbDataSync.TaskRunner only reads config) — so an
    // in-process lock around the write path is sufficient; no cross-process coordination is needed.
    // Found via architecture/implementation/done/phase-007-e2e-validation.md's concurrent-run stress test.
    private readonly object _writeLock = new();

    /// <summary>
    /// How much file content one diff may carry. A first commit creating forty mappings is a lot of
    /// YAML, and the editor that renders it is not the constraint — the wire and the operator are.
    /// Callers that want more ask for it; nobody does yet.
    /// </summary>
    public const int DefaultMaxContentBytes = 256 * 1024;

    public GitCommitService(string repositoryRoot)
    {
        Directory.CreateDirectory(repositoryRoot);
        _repositoryRoot = repositoryRoot;
        EnsureInitialized();
    }

    /// <summary>
    /// Create-if-missing, called before every git operation (construction and each use) rather than
    /// once — cheap (a single Directory.Exists check in the common already-initialized case) and
    /// makes the service self-healing if repositoryRoot's .git is ever missing when expected,
    /// whatever the cause (observed in this project's own dual-process — API + Vite dev server —
    /// end-to-end test setup; not reproduced in any single-process usage across the rest of the test
    /// suite, so the cause is very likely specific to that orchestration rather than this class, but
    /// self-healing here is a reasonable robustness measure regardless of the exact cause).
    /// </summary>
    private void EnsureInitialized()
    {
        if (!IsRepositoryAt(_repositoryRoot))
            Repository.Init(_repositoryRoot);
    }

    /// <summary>
    /// True only if <paramref name="path"/> itself contains a .git directory — deliberately not
    /// LibGit2Sharp's Repository.IsValid(path), which discovers upward through parent directories
    /// (like `git status` does). If <paramref name="path"/> is ever nested inside another git working
    /// tree (a config repo under a larger ops monorepo, a test fixture placed under this very
    /// project's own tree), IsValid reports the *parent* repo as valid even though path itself has no
    /// .git of its own — silently skipping initialization here, then failing later when
    /// new Repository(path) (which does not discover) can't find one. Callers that need to
    /// create-if-missing (e.g. DbDataSync.Api's startup) should check this before calling
    /// LibGit2Sharp.Repository.Init(path).
    /// </summary>
    public static bool IsRepositoryAt(string path) => Directory.Exists(Path.Combine(path, ".git"));

    /// <summary>Where the config repository lives — what a caller turns a repository-relative path
    /// (which is what every diff and history API here speaks) back into a file on disk with.</summary>
    public string RepositoryRoot => _repositoryRoot;

    /// <summary>The new commit's sha, or null when there was nothing to record.</summary>
    public string? CommitChanges(IReadOnlyCollection<string> absoluteFilePaths, string message, GitAuthor author)
    {
        lock (_writeLock)
        {
            EnsureInitialized();
            using var repo = new Repository(_repositoryRoot);

            foreach (var path in absoluteFilePaths)
            {
                var relative = Path.GetRelativePath(_repositoryRoot, path).Replace('\\', '/');
                Commands.Stage(repo, relative);
            }

            var signature = new Signature(author.Name, author.Email, DateTimeOffset.Now);
            try
            {
                return repo.Commit(message, signature, signature, new CommitOptions { AllowEmptyCommit = false }).Sha;
            }
            catch (EmptyCommitException)
            {
                // Content is identical to what's already committed (e.g. a no-op re-save) — nothing to record.
                return null;
            }
        }
    }


    /// <summary>
    /// The patch one commit made, scoped to <paramref name="relativePathPrefix"/> — phase 35's
    /// "View changes".
    /// <para>
    /// Scoped rather than whole: a commit can touch a connection and a replication at once, and a
    /// replication's history view showing another one's changes would be answering a question nobody
    /// asked. The first commit in a repository has no parent, which is not an edge case to guard
    /// against but the ordinary way a replication's creation appears — compared against an empty tree,
    /// it reads as every file added.
    /// </para>
    /// </summary>
    public ConfigDiff GetCommitDiff(string relativePathPrefix, string sha, int maxContentBytes = DefaultMaxContentBytes)
    {
        EnsureInitialized();
        using var repo = new Repository(_repositoryRoot);

        var commit = FindCommit(repo, sha);
        return BuildDiff(
            repo, commit.Parents.FirstOrDefault()?.Tree, commit.Tree, relativePathPrefix,
            commit.Sha, commit.MessageShort, maxContentBytes);
    }

    /// <summary>
    /// What restoring to <paramref name="sha"/> would change — the diff from **now** to then, which is
    /// not the same thing as the patch that commit made.
    /// <para>
    /// This distinction is the whole reason it is a second method. A commit's own patch answers "what
    /// did this change"; a restore's confirmation has to answer "what will this change", and once
    /// anything has happened since, those are different sets. Reverting to a commit that only renamed a
    /// column may well delete three mappings created after it, none of which appear in that commit's
    /// own patch.
    /// </para>
    /// </summary>
    public ConfigDiff GetRestoreDiff(string relativePathPrefix, string sha, int maxContentBytes = DefaultMaxContentBytes)
    {
        EnsureInitialized();
        using var repo = new Repository(_repositoryRoot);

        var commit = FindCommit(repo, sha);
        return BuildDiff(
            repo, repo.Head.Tip?.Tree, commit.Tree, relativePathPrefix,
            commit.Sha, commit.MessageShort, maxContentBytes);
    }

    /// <summary>
    /// Every text file under <paramref name="relativePathPrefix"/> as it was at
    /// <paramref name="sha"/>, keyed by repository-relative path with forward slashes.
    /// <para>
    /// A restore is a *restore*, not a `git revert`: it reads the tree at a commit rather than
    /// computing an inverse patch, so it cannot conflict, and it is what an operator means by "put it
    /// back the way it was". An empty result means the prefix did not exist at that commit, which the
    /// caller has to treat as a refusal rather than as "restore to nothing".
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, string> GetTextFilesAt(string relativePathPrefix, string sha)
    {
        EnsureInitialized();
        using var repo = new Repository(_repositoryRoot);

        var commit = FindCommit(repo, sha);
        var normalized = relativePathPrefix.Replace('\\', '/').TrimEnd('/');
        var files = new Dictionary<string, string>(StringComparer.Ordinal);

        if (commit[normalized]?.Target is Tree tree)
            CollectBlobs(tree, normalized, files);
        else if (commit[normalized]?.Target is Blob blob)
            files[normalized] = blob.GetContentText();

        return files;
    }

    private static void CollectBlobs(Tree tree, string prefix, Dictionary<string, string> into)
    {
        foreach (var entry in tree)
        {
            var path = $"{prefix}/{entry.Name}";
            switch (entry.Target)
            {
                case Tree subtree:
                    CollectBlobs(subtree, path, into);
                    break;
                case Blob blob:
                    into[path] = blob.GetContentText();
                    break;
            }
        }
    }

    /// <summary>
    /// A full or abbreviated sha, or a refusal naming it. <see cref="Repository.Lookup{T}(string)"/>
    /// returns null for anything it cannot resolve — including a sha that is real but belongs to
    /// another object kind — so this is where "no such commit" becomes something a caller can turn into
    /// a 404 rather than a null reference further down.
    /// </summary>
    private static Commit FindCommit(Repository repo, string sha)
    {
        if (string.IsNullOrWhiteSpace(sha))
            throw new ArgumentException("A commit sha is required.", nameof(sha));

        return repo.Lookup<Commit>(sha)
            ?? throw new GitCommitNotFoundException(sha);
    }

    /// <summary>
    /// Both sides of every changed file under the prefix.
    /// <para>
    /// Before and after rather than a unified patch, because the thing rendering this computes its own
    /// diff from two documents — and because a patch is a worse answer to "what did this look like
    /// before": it shows changed hunks and elides everything else, which for a config file is most of
    /// the context somebody is reading it for.
    /// </para>
    /// <para>
    /// **The file list is always complete; only the content is capped.** A first commit creating forty
    /// mappings is a lot of YAML, and truncating the list would answer "what changed" with a lie where
    /// truncating the content answers it with less detail. Once the budget is spent, later files come
    /// back named but empty, and <see cref="ConfigDiff.Truncated"/> says so.
    /// </para>
    /// </summary>
    private static ConfigDiff BuildDiff(
        Repository repo, Tree? from, Tree to, string relativePathPrefix, string sha, string message, int maxContentBytes)
    {
        var normalizedPrefix = relativePathPrefix.Replace('\\', '/').TrimEnd('/');
        var entries = repo.Diff.Compare<TreeChanges>(from, to)
            .Where(change => MatchesPrefix(change.Path, normalizedPrefix)
                          || MatchesPrefix(change.OldPath, normalizedPrefix))
            .OrderBy(change => change.Path, StringComparer.Ordinal)
            .ToList();

        var changes = new List<ConfigFileChange>(entries.Count);
        var budget = maxContentBytes;
        var truncated = false;

        foreach (var entry in entries)
        {
            var before = TextOf(repo, entry.OldOid);
            var after = TextOf(repo, entry.Oid);
            var cost = (before?.Length ?? 0) + (after?.Length ?? 0);

            if (cost > budget)
            {
                truncated = true;
                before = null;
                after = null;
            }
            else
            {
                budget -= cost;
            }

            changes.Add(new ConfigFileChange(
                entry.Path.Replace('\\', '/'), Kind(entry.Status), before, after));
        }

        return new ConfigDiff(sha, message, changes, truncated);
    }

    /// <summary>The blob's text, or null when this side of the change has none — an added file has no
    /// before, a deleted one no after. A zero oid is how libgit2 spells "not present".</summary>
    private static string? TextOf(Repository repo, ObjectId? oid) =>
        oid is null || oid == ObjectId.Zero ? null : repo.Lookup<Blob>(oid)?.GetContentText();

    private static ConfigChangeKind Kind(ChangeKind status) => status switch
    {
        ChangeKind.Added => ConfigChangeKind.Added,
        ChangeKind.Deleted => ConfigChangeKind.Deleted,
        ChangeKind.Renamed => ConfigChangeKind.Renamed,
        _ => ConfigChangeKind.Modified,
    };
    /// <summary>Commits that touched anything under <paramref name="relativePathPrefix"/> (e.g.
    /// "config/replications/crm-sync"), newest first — the SPA's Config History view.</summary>
    public IReadOnlyList<CommitInfo> GetHistory(string relativePathPrefix, int limit = 50)
    {
        EnsureInitialized();
        using var repo = new Repository(_repositoryRoot);
        if (repo.Head.Tip is null)
            return [];

        var normalizedPrefix = relativePathPrefix.Replace('\\', '/').TrimEnd('/');

        return repo.Commits
            .QueryBy(new CommitFilter { SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time })
            .Where(commit => CommitTouchesPrefix(repo, commit, normalizedPrefix))
            .Take(limit)
            .Select(commit => new CommitInfo(
                commit.Sha, commit.MessageShort, commit.Author.Name, commit.Author.Email, commit.Author.When))
            .ToList();
    }

    private static bool CommitTouchesPrefix(Repository repo, Commit commit, string normalizedPrefix)
    {
        var parentTree = commit.Parents.FirstOrDefault()?.Tree;
        var changes = repo.Diff.Compare<TreeChanges>(parentTree, commit.Tree);
        return changes.Any(change => MatchesPrefix(change.Path, normalizedPrefix));
    }

    /// <summary>
    /// Whether a repository path is <paramref name="normalizedPrefix"/> or sits under it.
    /// <para>
    /// **The equality half is not redundant**, and phase 81's retrospective is where its absence was
    /// first noticed. A directory prefix only ever matches paths *below* it, so matching on
    /// <c>prefix + "/"</c> alone works for every replication — and silently matches nothing at all for
    /// a bare file at the repository root: <c>dbdatasync.config.yaml</c> would be normalized to
    /// <c>dbdatasync.config.yaml/</c>, which that file's own path does not start with. The file was
    /// git-tracked and diffable from the command line the whole time, and invisible to every query
    /// here.
    /// </para>
    /// </summary>
    private static bool MatchesPrefix(string? path, string normalizedPrefix)
    {
        if (path is null)
            return false;

        var normalized = path.Replace('\\', '/');
        return normalized.Equals(normalizedPrefix, StringComparison.Ordinal)
            || normalized.StartsWith(normalizedPrefix + "/", StringComparison.Ordinal);
    }
}

/// <summary>A sha that resolves to no commit in this repository — a caller's 404 rather than a null
/// reference three frames later.</summary>
public sealed class GitCommitNotFoundException(string sha)
    : Exception($"No commit '{sha}' exists in the config repository.")
{
    public string Sha { get; } = sha;
}

public sealed record CommitInfo(string Sha, string Message, string AuthorName, string AuthorEmail, DateTimeOffset WhenUtc);

/// <summary>What changed, and both sides of each changed file — what a diff editor renders from.</summary>
/// <param name="Changes">Always complete, even when <paramref name="Truncated"/> is true: the answer
/// to "what changed" is not something to elide. Empty is a real answer rather than an error — a commit
/// can be in a replication's history for one file and change nothing in another.</param>
/// <param name="Truncated">Whether some file's content was too large to carry. A first commit creating
/// forty mappings is a lot of YAML, and streaming all of it into a browser to render something nobody
/// reads past the first screen of is not a service to anyone.</param>
public sealed record ConfigDiff(
    string Sha,
    string Message,
    IReadOnlyList<ConfigFileChange> Changes,
    bool Truncated);

/// <param name="Before">The file as it was, or null when there was no before — an added file.</param>
/// <param name="After">The file as it became, or null when there is no after — a deleted one. Both
/// are null for a file whose content did not fit the diff's budget; the change is still listed.</param>
public sealed record ConfigFileChange(string Path, ConfigChangeKind Kind, string? Before, string? After);

public enum ConfigChangeKind
{
    Added,
    Modified,
    Deleted,
    Renamed,
}
