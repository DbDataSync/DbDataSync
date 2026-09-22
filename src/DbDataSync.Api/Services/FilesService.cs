using DbDataSync.Api.Configuration;
using DbDataSync.Libraries;

namespace DbDataSync.Api.Services;

/// <param name="UsedBy">Every JDBC-backed descriptor driver (on disk, via <see cref="DriverDescriptorScanner"/>)
/// whose <c>jdbc.driverJarPaths</c> names this file — what <c>DELETE /api/files/{name}</c> checks before
/// refusing to remove one still in use, the identical shape <see cref="LibrariesService.UsedBy"/> already
/// uses for a library id.</param>
public sealed record FileSummary(string Name, long SizeBytes, DateTimeOffset UploadedAt, IReadOnlyList<string> UsedBy);

/// <summary>
/// Phase 173V. <c>&lt;repo&gt;/files/</c> — see <c>architecture/planning/todo/user-provided-files-store.md</c>
/// for the full design this implements: a flat store for anything an operator uploads for a driver to
/// reference (a JDBC jar, so far the only real case), distinct from <c>libraries/</c> (NuGet-restored) and
/// <c>drivers/</c> (authored descriptors). Not gitignored — an upload here is config an operator chose,
/// the same posture a <c>driver.yaml</c> already has, not derived build output.
/// </summary>
public sealed class FilesService(ApiOptions apiOptions)
{
    public IReadOnlyList<FileSummary> List()
    {
        var dir = FilesPaths.FilesDir(apiOptions.RepoRoot);
        if (!Directory.Exists(dir))
            return [];

        var usedBy = UsedByAllFiles();

        return new DirectoryInfo(dir).GetFiles()
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => new FileSummary(f.Name, f.Length, f.LastWriteTimeUtc, usedBy.GetValueOrDefault(f.Name, [])))
            .ToList();
    }

    /// <summary>Every JDBC-backed descriptor driver on disk whose <c>jdbc.driverJarPaths</c> names
    /// <paramref name="fileName"/> — what a delete request checks before refusing.</summary>
    public IReadOnlyList<string> UsedBy(string fileName) => UsedByAllFiles().GetValueOrDefault(fileName, []);

    private Dictionary<string, IReadOnlyList<string>> UsedByAllFiles() =>
        DriverDescriptorScanner.Scan(apiOptions.RepoRoot)
            .SelectMany(e => e.JdbcJarNames.Select(name => (Name: name, e.DriverId)))
            .GroupBy(x => x.Name)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.DriverId).OrderBy(x => x, StringComparer.Ordinal).ToList());

    /// <summary>Refused (409, by the caller) if <paramref name="name"/> already exists — replace is an
    /// explicit delete-then-reupload, never an implicit overwrite (design doc's own decision).</summary>
    public bool Exists(string name) => File.Exists(FilesPaths.FilePath(apiOptions.RepoRoot, name));

    public async Task SaveAsync(string name, Stream content, CancellationToken cancellationToken)
    {
        var dir = FilesPaths.FilesDir(apiOptions.RepoRoot);
        Directory.CreateDirectory(dir);
        await using var target = File.Create(FilesPaths.FilePath(apiOptions.RepoRoot, name));
        await content.CopyToAsync(target, cancellationToken);
    }

    public void Delete(string name) => File.Delete(FilesPaths.FilePath(apiOptions.RepoRoot, name));
}
