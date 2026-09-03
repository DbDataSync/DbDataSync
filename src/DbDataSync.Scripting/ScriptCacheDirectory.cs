namespace DbDataSync.Scripting;

/// <summary>
/// Compiled script assemblies on disk, keyed by a hash of their source.
/// <para>
/// **On disk, not in memory, and that is the point.** The TaskRunner is a process spawned per
/// replication run, so an in-memory compile cache buys nothing at all — every run would start cold and
/// pay Roslyn's first-compilation cost (on the order of a second) before compiling anything of ours. On
/// a fifteen-second schedule that is most of the interval spent starting a compiler.
/// </para>
/// <para>
/// The API populates this when it validates a script on save, so in the normal case a run loads a DLL
/// and never compiles. The directory is not git-tracked and is safe to delete at any time — a missing
/// entry costs one compilation.
/// </para>
/// </summary>
public sealed class ScriptCacheDirectory
{
    private readonly string _root;

    public ScriptCacheDirectory(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
    }

    /// <summary>Beside the state database, which every process already knows how to find and which is
    /// already the place for things that are derived rather than authored.</summary>
    public static ScriptCacheDirectory BesideStateDatabase(string stateDbPath) =>
        new(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(stateDbPath))!, "script-cache"));

    public string? TryGetAssemblyPath(string hash)
    {
        var path = PathFor(hash);
        return File.Exists(path) ? path : null;
    }

    public void Store(string hash, byte[] assembly)
    {
        // Written under a unique temporary name and moved into place, so a second process reading the
        // cache never sees a half-written assembly. Two processes compiling the same script at once is
        // ordinary here: the API validates on save while a run is already under way.
        var path = PathFor(hash);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temp, assembly);
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException)
        {
            // A cache is an optimisation. Failing to populate it must never fail the compile that
            // produced the bytes — the caller already has them.
            try { File.Delete(temp); } catch (IOException) { }
        }
    }

    private string PathFor(string hash) => Path.Combine(_root, $"{hash}.dll");
}
