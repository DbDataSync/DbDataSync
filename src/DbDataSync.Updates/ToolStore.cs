namespace DbDataSync.Updates;

/// <summary>
/// Finds things inside a <c>dotnet tool</c> install. A tool's package is kept in its own store as
/// <c>&lt;root&gt;/.store/dbdatasync/&lt;version&gt;/dbdatasync/&lt;version&gt;/dbdatasync.&lt;version&gt;.nupkg</c>
/// (checked against a real install, 2026-09-19) — and is deleted when that version is replaced, which is why
/// an update copies it aside first: it is the only offline copy of the version being left.
/// </summary>
public static class ToolStore
{
    public static string? FindInstalledNupkg(string toolRoot, string version)
    {
        var normalized = version.ToLowerInvariant();
        var directory = Path.Combine(toolRoot, ".store", "dbdatasync", normalized, "dbdatasync", normalized);
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.nupkg").FirstOrDefault()
            : null;
    }
}
