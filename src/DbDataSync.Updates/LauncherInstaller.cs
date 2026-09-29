namespace DbDataSync.Updates;

/// <summary>
/// Copies a package's launcher (<c>launcher/&lt;rid&gt;/</c>, beside the payload) into the tool directory — at a
/// slot install's first setup, and when it is repaired (phase 196L). **Never** part of an ordinary update: the
/// launcher is the file the service and PATH name, and not replacing it is the point of the design.
/// </summary>
public static class LauncherInstaller
{
    private const string AsideMarker = ".old-";

    /// <summary>This machine's launcher in a payload directory, or null when the package has none for this platform
    /// (a package from before phase 196L, or a platform no launcher is built for).</summary>
    public static string? SourceDirectory(string payloadDirectory)
    {
        var rid = SlotPaths.PortableRuntimeIdentifier();
        if (rid is null)
            return null;

        var directory = Path.Combine(payloadDirectory, SlotPaths.LauncherDirectoryName, rid);
        return Directory.Exists(directory) && Directory.EnumerateFiles(directory, SlotPaths.LauncherName + "*").Any()
            ? directory
            : null;
    }

    /// <summary>
    /// Copies each launcher file that differs into <paramref name="root"/>: to a temp name, then renamed into place.
    /// A file that cannot be replaced because it is running — Windows will not overwrite an executable image in use,
    /// but will rename it — is renamed aside first; <see cref="CleanUp"/> removes it on a later run.
    /// </summary>
    /// <returns>How many files were replaced; 0 when the installed launcher is already this one.</returns>
    public static int Install(string sourceDirectory, string root)
    {
        Directory.CreateDirectory(root);
        var replaced = 0;
        foreach (var source in Directory.EnumerateFiles(sourceDirectory))
        {
            var name = Path.GetFileName(source);
            if (name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
                continue;

            var target = Path.Combine(root, name);
            if (SameContent(source, target))
                continue;

            var temp = Path.Combine(root, $".{name}.{Guid.NewGuid():N}.tmp");
            File.Copy(source, temp);
            try
            {
                File.Move(temp, target, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                File.Move(target, target + AsideMarker + Guid.NewGuid().ToString("N"));
                File.Move(temp, target);
            }

            replaced++;
        }

        return replaced;
    }

    /// <summary>Removes launcher files renamed aside by an earlier <see cref="Install"/>. Best-effort: one still in
    /// use is left for next time.</summary>
    public static void CleanUp(string root)
    {
        if (!Directory.Exists(root))
            return;

        foreach (var aside in Directory.EnumerateFiles(root, SlotPaths.LauncherName + "*" + AsideMarker + "*"))
        {
            try
            {
                File.Delete(aside);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static bool SameContent(string a, string b)
    {
        if (!File.Exists(b))
            return false;

        var left = new FileInfo(a);
        var right = new FileInfo(b);
        return left.Length == right.Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
    }
}
