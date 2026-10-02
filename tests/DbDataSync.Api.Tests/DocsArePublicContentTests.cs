using System.Text.RegularExpressions;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// <c>docs/</c> is public, embedded documentation; phase docs and planning records are the project's own
/// process. Nothing in the first may point at the second — a reader of an installed copy has no
/// <c>architecture/</c> folder, and a phase number means nothing to them. If a reader needs the detail,
/// the detail goes in <c>docs/</c>. See <c>architecture/implementation/README.md</c>, "docs/ is public
/// documentation, not planning".
/// </summary>
public sealed class DocsArePublicContentTests
{
    private static readonly Regex PhaseReference = new(@"\bphase[- ]?\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PlanningLink = new(@"architecture/(implementation|planning)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Fact]
    public void NoDocsPageReferencesAPhaseOrPlanningDocument()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(AdminConfigServiceTests.RepoDocsDirectory(), "*.md", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (PhaseReference.IsMatch(lines[i]) || PlanningLink.IsMatch(lines[i]))
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "docs/ is public documentation and must not mention phases or point into architecture/. Say what the " +
            "reader needs to know instead:\n" + string.Join("\n", offenders));
    }
}
