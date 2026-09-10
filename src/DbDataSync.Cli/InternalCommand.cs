using DbDataSync.Libraries;

namespace DbDataSync.Cli;

/// <summary>
/// <c>dbdatasync internal ...</c> — commands for the build, not for an operator. Deliberately absent
/// from <see cref="Help.Print"/> and from every user-facing doc; the Dockerfile is the only caller.
/// </summary>
public static class InternalCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: dbdatasync internal build-catalog-cache <out-dir>");
            return 1;
        }

        return args[0].ToLowerInvariant() switch
        {
            "build-catalog-cache" => await BuildCatalogCacheAsync(args[1..]),
            var other => Unknown(other),
        };
    }

    /// <summary>
    /// Phase 121: restores every <see cref="KnownLibraries"/> entry at its
    /// <see cref="LibraryCatalogEntry.PinnedVersion"/> into <paramref name="args"/>[0], shaped exactly
    /// like a repo's own <c>libraries/</c> directory (because it's the same <see cref="LibraryInstaller"/>
    /// writing it) — so <c>COPY</c>ing the result into the runtime-only image at
    /// <see cref="LibraryInstaller.DefaultCacheRoot"/> is all the Dockerfile needs to do, and
    /// <c>LibraryInstaller.InstallOrDeferAsync</c> reads it back with the same
    /// <see cref="LibraryPaths"/> helpers used to write it.
    /// </summary>
    private static async Task<int> BuildCatalogCacheAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: dbdatasync internal build-catalog-cache <out-dir>");
            return 1;
        }

        var outDir = args[0];
        Directory.CreateDirectory(outDir);

        foreach (var entry in KnownLibraries.All)
        {
            Console.WriteLine($"Caching '{entry.Id}' ({entry.PackageId} {entry.PinnedVersion})...");
            await LibraryInstaller.InstallAsync(
                outDir, entry.Id, [new PackageRef(entry.PackageId, entry.PinnedVersion)], entry.FactoryType);
        }

        Console.WriteLine($"Catalog cache built at '{outDir}' ({KnownLibraries.All.Count} entries).");
        return 0;
    }

    private static int Unknown(string sub)
    {
        Console.Error.WriteLine($"Unknown internal subcommand '{sub}'.");
        return 1;
    }
}
