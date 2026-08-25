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

    public GitCommitService(string repositoryRoot)
    {
        if (!Repository.IsValid(repositoryRoot))
            throw new InvalidOperationException($"'{repositoryRoot}' is not a git repository.");

        _repositoryRoot = repositoryRoot;
    }

    public void CommitChanges(IReadOnlyCollection<string> absoluteFilePaths, string message, GitAuthor author)
    {
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
