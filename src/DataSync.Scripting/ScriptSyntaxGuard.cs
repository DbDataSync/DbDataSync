using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DataSync.Scripting;

/// <summary>
/// Rejects a script that reaches for a namespace scripts have no business in.
/// <para>
/// **This is a lint, not a security boundary.** It is trivially bypassed — by reflection, by an alias,
/// by anything that does not spell the namespace out — and it is not trying to stop someone who wants
/// to. The security decision is recorded in architecture/planning/done/csharp-scripting-host.md: the
/// risk is accepted and gated by permissions.
/// </para>
/// <para>
/// It exists because the reference set alone cannot do this job. Restricting references keeps out
/// <c>System.Net.Http</c> and <c>System.Diagnostics.Process</c>, which live in their own assemblies —
/// but <c>System.IO.File</c> and <c>System.Environment</c> live in <c>System.Private.CoreLib</c>
/// alongside <c>string</c> and <c>int</c>, so there is no reference set that admits one and not the
/// other. Compiling against reference assemblies rather than implementation ones would fix that
/// properly and is a larger change than it is worth for a boundary we have already decided not to
/// enforce.
/// </para>
/// <para>
/// What it buys is that the most likely accident — a transform quietly reading a file or an
/// environment variable — fails at save time with a message saying why, instead of at 3am in a run.
/// </para>
/// </summary>
internal static class ScriptSyntaxGuard
{
    private static readonly string[] BannedNamespaces =
    [
        "System.IO",
        "System.Net",
        "System.Diagnostics",
        "System.Reflection",
        "System.Runtime.InteropServices",
        "System.Threading",
    ];

    public static IReadOnlyList<ScriptDiagnostic> Check(SyntaxTree tree)
    {
        var diagnostics = new List<ScriptDiagnostic>();
        var root = tree.GetRoot();

        foreach (var directive in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
        {
            var name = directive.Name?.ToString();
            if (name is not null && IsBanned(name))
                diagnostics.Add(At(directive, $"Scripts may not use '{name}'. {Why}"));
        }

        // A qualified name spelled out in code — System.IO.File.ReadAllText(…) — which a `using` check
        // alone would miss.
        foreach (var qualified in root.DescendantNodes().OfType<QualifiedNameSyntax>())
        {
            // A using directive contains a qualified name; it has already been reported above, and
            // reporting the same line twice makes the operator look for two problems.
            if (qualified.FirstAncestorOrSelf<UsingDirectiveSyntax>() is not null)
                continue;

            var name = qualified.ToString();
            if (IsBanned(name))
            {
                diagnostics.Add(At(qualified, $"Scripts may not use '{name}'. {Why}"));
                break;
            }
        }

        foreach (var member in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            var name = member.ToString();
            if (IsBanned(name))
            {
                diagnostics.Add(At(member, $"Scripts may not use '{Prefix(name)}'. {Why}"));
                break;
            }
        }

        return diagnostics;
    }

    private const string Why =
        "A script transforms data and generates SQL; it does not read files, open sockets, start " +
        "processes or touch the host. If you need something from outside, put it in the mapping's " +
        "parameters instead.";

    private static bool IsBanned(string name) =>
        BannedNamespaces.Any(ns => name.StartsWith(ns + ".", StringComparison.Ordinal) || name == ns);

    private static string Prefix(string name) =>
        BannedNamespaces.FirstOrDefault(ns => name.StartsWith(ns + ".", StringComparison.Ordinal)) ?? name;

    private static ScriptDiagnostic At(SyntaxNode node, string message)
    {
        var span = node.GetLocation().GetLineSpan();
        return new ScriptDiagnostic(span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1, message);
    }
}
