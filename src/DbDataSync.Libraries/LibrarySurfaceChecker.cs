using System.Reflection;
using System.Runtime.InteropServices;

namespace DbDataSync.Libraries;

/// <summary>The answer to "does this installed library still have everything the driver's IL uses" —
/// <see cref="Compatible"/> true and an empty <see cref="MissingMembers"/> for a clean check.</summary>
/// <param name="MissingMembers">Each entry is <see cref="UsedMember.Describe"/>'s own text — a
/// human-readable identification, ready to print in a warning or a report, not a code needing further
/// lookup.</param>
public sealed record LibrarySurfaceCheckResult(bool Compatible, IReadOnlyList<string> MissingMembers)
{
    public static readonly LibrarySurfaceCheckResult Clean = new(true, []);
}

/// <summary>
/// Phase 109j: confirms every <see cref="UsedMember"/> a driver's own IL references still exists in a
/// candidate library, loaded purely as metadata.
/// <para>
/// **Never touches <see cref="System.Runtime.Loader.AssemblyLoadContext.Default"/>.**
/// <see cref="MetadataLoadContext"/> is its own disposable, isolated context — no constructor runs, no
/// static initializer runs, nothing JITs, and nothing is resident in this process once it is disposed.
/// Safe to call from a long-running host (the API's own <c>library install</c>/<c>config check</c> path)
/// as many times as needed, unlike actually opening a connection through the library (phase 109j's own
/// deeper, connection-scoped check, which is why *that* half runs in a spawned child process instead).
/// </para>
/// <para>
/// The resolver setup — every DLL in the candidate library's own <c>lib/</c> directory, plus every DLL
/// in the current runtime's shared framework directory — is the identical, already-proven pattern
/// <see cref="FactoryTypeReflector.Discover"/> uses for the same reason: a candidate assembly's own
/// dependencies (its own multi-package closure) resolve from the first set, and the BCL types its
/// public surface is built from (<c>System.Data.Common.DbConnection</c>, <c>System.String</c>, and so
/// on — a member's *parameter* types are not resolved here at all, see <see cref="UsedMember"/>'s own
/// doc comment on why arity rather than full signature matching is what this checks) resolve from the
/// second.
/// </para>
/// </summary>
public static class LibrarySurfaceChecker
{
    private const BindingFlags AllMembers =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    /// <param name="libDir">The installed library's <c>lib/</c> directory — every top-level <c>*.dll</c>
    /// in it is a candidate to satisfy a used member's declaring type.</param>
    public static LibrarySurfaceCheckResult Check(IReadOnlyList<UsedMember> used, string libDir)
    {
        if (used.Count == 0)
            return LibrarySurfaceCheckResult.Clean;

        var dllPaths = Directory.Exists(libDir)
            ? Directory.GetFiles(libDir, "*.dll", SearchOption.TopDirectoryOnly)
            : [];
        if (dllPaths.Length == 0)
        {
            // Nothing restored to check against — the caller (DriverLibraryCompatibility) is expected
            // to have already ruled this out before calling; treated as "nothing to report" rather than
            // "everything is missing" so a half-installed library doesn't produce a wall of false
            // positives on top of LibrariesAndDriversCheck's own "no restored lib/" finding.
            return LibrarySurfaceCheckResult.Clean;
        }

        var resolverPaths = Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll")
            .Concat(dllPaths)
            .ToArray();
        using var mlc = new MetadataLoadContext(new PathAssemblyResolver(resolverPaths));

        var typesByFullName = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var dllPath in dllPaths)
        {
            Assembly assembly;
            try
            {
                assembly = mlc.LoadFromAssemblyPath(dllPath);
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException)
            {
                continue; // Not a managed assembly, or a duplicate of one already loaded — not a candidate.
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
            }

            foreach (var type in types)
                if (type.FullName is not null)
                    typesByFullName[type.FullName] = type;
        }

        var missing = new List<string>();
        foreach (var member in used)
        {
            if (!typesByFullName.TryGetValue(member.DeclaringTypeFullName, out var type))
            {
                missing.Add($"{member.Describe()} — type not found in the installed library");
                continue;
            }

            var found = member.Kind switch
            {
                UsedMemberKind.Type => true, // The type itself resolved above; nothing further to check.
                UsedMemberKind.Field => type.GetField(member.MemberName!, AllMembers) is not null,
                UsedMemberKind.Method => type.GetMethods(AllMembers).Cast<MethodBase>()
                    .Concat(type.GetConstructors(AllMembers))
                    .Any(m => string.Equals(m.Name, member.MemberName, StringComparison.Ordinal)
                              && m.GetParameters().Length == member.ParameterCount),
                _ => true,
            };

            if (!found)
                missing.Add(member.Describe());
        }

        return missing.Count == 0 ? LibrarySurfaceCheckResult.Clean : new LibrarySurfaceCheckResult(false, missing);
    }
}
