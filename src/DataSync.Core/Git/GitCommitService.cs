using LibGit2Sharp;

namespace DataSync.Core.Git;

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
    // the only writer of this repo is this process (DataSync.TaskRunner only reads config) — so an
    // in-process lock around the write path is sufficient; no cross-process coordination is needed.
    // Found via architecture/implementation/phase-7-e2e-validation.md's concurrent-run stress test.
    private readonly object _writeLock = new();

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
    /// create-if-missing (e.g. DataSync.Api's startup) should check this before calling
    /// LibGit2Sharp.Repository.Init(path).
    /// </summary>
    public static bool IsRepositoryAt(string path) => Directory.Exists(Path.Combine(path, ".git"));

    public void CommitChanges(IReadOnlyCollection<string> absoluteFilePaths, string message, GitAuthor author)
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
                repo.Commit(message, signature, signature, new CommitOptions { AllowEmptyCommit = false });
            }
            catch (EmptyCommitException)
            {
                // Content is identical to what's already committed (e.g. a no-op re-save) — nothing to record.
            }
        }
    }

    /// <summary>Commits that touched anything under <paramref name="relativePathPrefix"/> (e.g.
    /// "config/replications/crm-sync"), newest first — the SPA's read-only Config History view.</summary>
    public IReadOnlyList<CommitInfo> GetHistory(string relativePathPrefix, int limit = 50)
    {
        EnsureInitialized();
        using var repo = new Repository(_repositoryRoot);
        if (repo.Head.Tip is null)
            return [];

        var normalizedPrefix = relativePathPrefix.Replace('\\', '/').TrimEnd('/') + "/";

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
        return changes.Any(change => change.Path.Replace('\\', '/').StartsWith(normalizedPrefix, StringComparison.Ordinal));
    }
}

public sealed record CommitInfo(string Sha, string Message, string AuthorName, string AuthorEmail, DateTimeOffset WhenUtc);
