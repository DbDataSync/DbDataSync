using Mono.Cecil;

namespace DbDataSync.Libraries;

/// <summary>What kind of metadata table entry <see cref="UsedMember"/> came from.</summary>
public enum UsedMemberKind
{
    /// <summary>A bare type reference — the driver names the type but this extraction found no
    /// member reference into it (rare in practice; almost every real usage also touches a member).</summary>
    Type,
    Field,
    Method,
}

/// <summary>
/// One member (or bare type) a driver's compiled IL references into its required library assembly.
/// <para>
/// Matched against a candidate library by <see cref="DeclaringTypeFullName"/> + <see cref="MemberName"/>
/// + <see cref="ParameterCount"/> for a method/constructor, or by <see cref="DeclaringTypeFullName"/> +
/// <see cref="MemberName"/> alone for a field — name and arity, not full signature-level overload
/// resolution. A deliberate implementation-time simplification (phase 109j): comparing parameter types
/// across two independently-loaded metadata contexts (Cecil's own type-reference model for the driver,
/// <see cref="System.Reflection.MetadataLoadContext"/>'s <see cref="Type"/> objects for the candidate)
/// would need its own type-identity comparison with no shared runtime to lean on, for a benefit this
/// phase judges not worth the complexity: the real risk named in the phase doc is a member removed or
/// renamed outright (a <see cref="MissingMethodException"/>), which name+arity already catches, not a
/// same-named, same-arity overload whose parameter types changed incompatibly — a narrower and
/// considerably rarer form of breaking change.
/// </para>
/// </summary>
/// <param name="DeclaringTypeFullName">The type's full name, normalized to reflection's <c>+</c>
/// nested-type separator (Cecil uses <c>/</c>) so it compares directly against
/// <see cref="Type.FullName"/> on the checking side.</param>
public sealed record UsedMember(string DeclaringTypeFullName, UsedMemberKind Kind, string? MemberName, int ParameterCount)
{
    /// <summary>A one-line, human-readable identification of this member — what a "missing members"
    /// report names, and what <c>config check</c>/<c>library install</c> print.</summary>
    public string Describe() => Kind switch
    {
        UsedMemberKind.Type => DeclaringTypeFullName,
        UsedMemberKind.Field => $"{DeclaringTypeFullName}.{MemberName}",
        UsedMemberKind.Method => $"{DeclaringTypeFullName}.{MemberName}({ParameterCount} arg(s))",
        _ => DeclaringTypeFullName,
    };
}

/// <summary>
/// Phase 109j: walks a driver's own already-built DLL's metadata (a <c>MemberRef</c>/<c>TypeRef</c>
/// table read via Mono.Cecil — no execution, no JIT, nothing loaded into this process's real
/// <see cref="System.Runtime.Loader.AssemblyLoadContext"/>) and lists every member/type it references
/// into one specific target assembly (its <see cref="Drivers.Abstractions.IDriver.RequiredLibraryId"/>
/// library, e.g. <c>Microsoft.Data.SqlClient</c>).
/// <para>
/// <c>ModuleDefinition.GetMemberReferences()</c>/<c>GetTypeReferences()</c> return every entry in the
/// module's own <c>MemberRef</c>/<c>TypeRef</c> metadata tables — by definition references to something
/// defined in a *different* module, whether that's the target library, another DbDataSync assembly, or
/// the BCL. Filtering by the declaring type's resolved assembly name is what narrows that down to just
/// the library this phase cares about.
/// </para>
/// </summary>
public static class LibrarySurfaceExtractor
{
    /// <summary>
    /// Reads <paramref name="driverAssemblyPath"/>'s own metadata and returns every distinct
    /// member/type it references whose declaring type resolves to <paramref name="targetAssemblySimpleName"/>
    /// (e.g. <c>"Microsoft.Data.SqlClient"</c>, not the package id or a full path).
    /// </summary>
    public static IReadOnlyList<UsedMember> ExtractUsedSurface(string driverAssemblyPath, string targetAssemblySimpleName)
    {
        using var module = ModuleDefinition.ReadModule(
            driverAssemblyPath, new ReaderParameters { ReadingMode = ReadingMode.Deferred });

        var used = new HashSet<UsedMember>();

        foreach (var typeRef in module.GetTypeReferences())
        {
            if (!MatchesAssembly(typeRef.Scope, targetAssemblySimpleName))
                continue;

            used.Add(new UsedMember(NormalizeTypeName(typeRef.FullName), UsedMemberKind.Type, null, 0));
        }

        foreach (var memberRef in module.GetMemberReferences())
        {
            var declaringType = memberRef.DeclaringType;
            if (declaringType is null || !MatchesAssembly(declaringType.Scope, targetAssemblySimpleName))
                continue;

            var typeName = NormalizeTypeName(declaringType.FullName);
            used.Add(memberRef switch
            {
                MethodReference method => new UsedMember(typeName, UsedMemberKind.Method, method.Name, method.Parameters.Count),
                FieldReference field => new UsedMember(typeName, UsedMemberKind.Field, field.Name, 0),
                _ => new UsedMember(typeName, UsedMemberKind.Type, null, 0),
            });
        }

        return used.ToList();
    }

    /// <summary>
    /// <see cref="TypeReference.Scope"/> resolves through a nested type's declaring type chain to the
    /// enclosing module/assembly on its own (Cecil's own behaviour, not something this method has to
    /// walk itself) — so a plain <see cref="AssemblyNameReference"/> name comparison is enough here.
    /// </summary>
    private static bool MatchesAssembly(IMetadataScope scope, string targetAssemblySimpleName) =>
        scope switch
        {
            AssemblyNameReference assemblyRef => string.Equals(assemblyRef.Name, targetAssemblySimpleName, StringComparison.Ordinal),
            ModuleDefinition moduleDef => string.Equals(moduleDef.Assembly?.Name?.Name, targetAssemblySimpleName, StringComparison.Ordinal),
            _ => false,
        };

    /// <summary>Cecil separates a nested type from its declaring type with <c>/</c>
    /// (<c>Outer/Inner</c>); <see cref="Type.FullName"/> uses <c>+</c> (<c>Outer+Inner</c>) — normalized
    /// here so a lookup by name on the checking side (against real reflection <see cref="Type"/>
    /// objects) matches directly.</summary>
    private static string NormalizeTypeName(string cecilFullName) => cecilFullName.Replace('/', '+');
}
