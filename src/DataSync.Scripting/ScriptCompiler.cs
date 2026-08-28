using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using DataSync.Core.Config;
using DataSync.Scripting.Abstractions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DataSync.Scripting;

/// <summary>
/// Compiles a script's source into an assembly and finds its entry type.
/// <para>
/// <c>CSharpCompilation</c> rather than <c>Microsoft.CodeAnalysis.CSharp.Scripting</c>: the same Roslyn
/// dependency with more control over the reference set, and full C# semantics — which matter, because
/// the slots this feeds end at "rebuild the whole query from metadata", which is real code with types
/// and helper methods rather than an expression.
/// </para>
/// </summary>
public sealed class ScriptCompiler(ScriptCacheDirectory cache)
{
    /// <summary>
    /// What a script is allowed to reference.
    /// <para>
    /// **This is a guardrail, not a security boundary, and must not be described as one.** Reflection
    /// defeats it, and .NET has had no in-process code-trust mechanism since CAS was removed. The
    /// security decision — the risk is accepted and gated by permissions — is recorded in
    /// architecture/planning/done/csharp-scripting-host.md.
    /// </para>
    /// <para>
    /// It is also **partial by construction**, which is worth knowing before relying on it. Keeping an
    /// assembly out works for <c>System.Net.Http</c> and <c>System.Diagnostics.Process</c>, which have
    /// their own; it does nothing about <c>System.IO.File</c> or <c>System.Environment</c>, which live
    /// in <c>System.Private.CoreLib</c> next to <c>string</c>. <see cref="ScriptSyntaxGuard"/> covers
    /// that gap as a lint.
    /// </para>
    /// </summary>
    private static readonly string[] AllowedAssemblies =
    [
        "System.Runtime",
        "System.Private.CoreLib",
        "System.Linq",
        "System.Collections",
        "System.Text.RegularExpressions",
        "System.Globalization",
        "netstandard",
    ];

    public ScriptCompilation Compile(ScriptDefinition script)
    {
        if (string.IsNullOrWhiteSpace(script.Code))
            return ScriptCompilation.Failed(
                [new ScriptDiagnostic(0, 0, $"Script '{script.Manifest.Name}' has no code.")]);

        // A SQL hook has no entry type to compile — ScriptHost never routes one here (see
        // HookExecution), so reaching this with Language=Sql is a caller error, not a config problem.
        if (script.Manifest.EntryType is not { } entryType)
            throw new InvalidOperationException(
                $"Script '{script.Manifest.Name}' has no EntryType and cannot be compiled — " +
                "ScriptCompiler.Compile is only for ScriptLanguage.CSharp scripts.");

        var hash = HashOf(script.Code, entryType);
        var cached = cache.TryGetAssemblyPath(hash);
        if (cached is not null)
            return Load(File.ReadAllBytes(cached), script, hash);

        var syntaxTree = CSharpSyntaxTree.ParseText(script.Code, path: $"{script.Manifest.Name}.cs");

        // Before Roslyn, because the reference set cannot express this on its own — see the guard.
        var banned = ScriptSyntaxGuard.Check(syntaxTree);
        if (banned.Count > 0)
            return ScriptCompilation.Failed(banned);

        var compilation = CSharpCompilation.Create(
            $"DataSync.Script.{script.Manifest.Name}.{hash[..8]}",
            [syntaxTree],
            ReferenceSet(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));

        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        if (!result.Success)
            return ScriptCompilation.Failed(result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(ToDiagnostic)
                .ToList());

        var bytes = stream.ToArray();
        cache.Store(hash, bytes);
        return Load(bytes, script, hash);
    }

    private static ScriptCompilation Load(byte[] assemblyBytes, ScriptDefinition script, string hash)
    {
        // Collectible, so replacing a script does not leak an assembly for the life of the process.
        // In the TaskRunner that hardly matters — the process is per run — but the API is long-lived
        // and compiles on every save.
        var context = new AssemblyLoadContext($"script:{script.Manifest.Name}:{hash[..8]}", isCollectible: true);
        Assembly assembly;
        try
        {
            assembly = context.LoadFromStream(new MemoryStream(assemblyBytes));
        }
        catch (Exception ex)
        {
            context.Unload();
            return ScriptCompilation.Failed([new ScriptDiagnostic(0, 0, $"Could not load the compiled script: {ex.Message}")]);
        }

        var entryType = assembly.GetTypes().FirstOrDefault(
            t => string.Equals(t.Name, script.Manifest.EntryType!, StringComparison.Ordinal));

        if (entryType is null)
        {
            var available = string.Join(", ", assembly.GetTypes().Where(t => t.IsPublic).Select(t => t.Name));
            context.Unload();
            return ScriptCompilation.Failed([new ScriptDiagnostic(0, 0,
                $"Entry type '{script.Manifest.EntryType}' was not found in script '{script.Manifest.Name}' " +
                $"(public types: {(available.Length == 0 ? "none" : available)}).")]);
        }

        return ScriptCompilation.Succeeded(context, entryType, hash);
    }

    /// <summary>
    /// The reference set, resolved from the assemblies already loaded in this process rather than from
    /// a reference pack on disk — the host and the script run on the same runtime by construction, so
    /// there is no version to reconcile.
    /// </summary>
    private static IReadOnlyList<MetadataReference> ReferenceSet()
    {
        var references = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location))
                continue;

            var name = assembly.GetName().Name;
            if (name is null || !AllowedAssemblies.Contains(name, StringComparer.OrdinalIgnoreCase))
                continue;
            if (seen.Add(assembly.Location))
                references.Add(MetadataReference.CreateFromFile(assembly.Location));
        }

        // The contracts a script implements, always — without them nothing it writes can compile.
        var abstractions = typeof(ISqlColumnExpression).Assembly;
        if (seen.Add(abstractions.Location))
            references.Add(MetadataReference.CreateFromFile(abstractions.Location));

        var drivers = typeof(DataSync.Drivers.Abstractions.ColumnMetadata).Assembly;
        if (seen.Add(drivers.Location))
            references.Add(MetadataReference.CreateFromFile(drivers.Location));

        // And the config types the contracts hand a script: ColumnMapping on every transform context,
        // SourceTableRef and TableRef on a lifecycle hook's. Without this a script could implement the
        // interfaces but not read half of what it was given — phase 27's own motivating example, "for
        // each mapped column with no target column, emit ALTER TABLE", touches Target and so never
        // compiled. Found by phase 41, which was the first thing to run one.
        var config = typeof(DataSync.Core.Config.ColumnMapping).Assembly;
        if (seen.Add(config.Location))
            references.Add(MetadataReference.CreateFromFile(config.Location));

        return references;
    }

    private static ScriptDiagnostic ToDiagnostic(Diagnostic diagnostic)
    {
        var span = diagnostic.Location.GetLineSpan();
        return new ScriptDiagnostic(
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1,
            diagnostic.GetMessage());
    }

    /// <summary>Keyed on the entry type as well as the code, because changing which type is the entry
    /// point changes what compiles to — without changing a byte of source.</summary>
    internal static string HashOf(string code, string entryType) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{entryType}\n{code}"))).ToLowerInvariant();
}

public sealed record ScriptDiagnostic(int Line, int Column, string Message)
{
    public override string ToString() => Line > 0 ? $"({Line},{Column}): {Message}" : Message;
}

public sealed class ScriptCompilation
{
    private ScriptCompilation(AssemblyLoadContext? context, Type? entryType, string? hash, IReadOnlyList<ScriptDiagnostic> diagnostics)
    {
        LoadContext = context;
        EntryType = entryType;
        Hash = hash;
        Diagnostics = diagnostics;
    }

    public AssemblyLoadContext? LoadContext { get; }
    public Type? EntryType { get; }
    public string? Hash { get; }
    public IReadOnlyList<ScriptDiagnostic> Diagnostics { get; }

    public bool Success => EntryType is not null;

    public static ScriptCompilation Succeeded(AssemblyLoadContext context, Type entryType, string hash) =>
        new(context, entryType, hash, []);

    public static ScriptCompilation Failed(IReadOnlyList<ScriptDiagnostic> diagnostics) =>
        new(null, null, null, diagnostics);

    /// <summary>Creates the script's type, cast to the contract its slot requires.</summary>
    public T CreateInstance<T>(string scriptName) where T : class
    {
        if (EntryType is null)
            throw new ScriptExecutionException($"Script '{scriptName}' did not compile: {string.Join("; ", Diagnostics)}");

        if (!typeof(T).IsAssignableFrom(EntryType))
            throw new ScriptExecutionException(
                $"Script '{scriptName}' type '{EntryType.Name}' does not implement {typeof(T).Name}.");

        return Activator.CreateInstance(EntryType) as T
            ?? throw new ScriptExecutionException($"Script '{scriptName}' type '{EntryType.Name}' could not be constructed.");
    }
}
