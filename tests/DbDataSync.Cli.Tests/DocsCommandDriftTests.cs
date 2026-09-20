using System.Text.RegularExpressions;

namespace DbDataSync.Cli.Tests;

/// <summary>
/// The docs tell operators what to type. A command that no longer exists there — <c>dbdatasync secret</c> and
/// <c>dbdatasync cert</c> were still documented after phase 115 moved them under <c>config</c>, so anyone copy-pasting got
/// "Unknown command" — is the kind of drift only a reader finds (phase 162). The CLI's own help is what says which commands exist.
/// </summary>
public sealed class DocsCommandDriftTests
{
    private static string DocsDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs");
            if (File.Exists(Path.Combine(candidate, "configuration.md")))
                return candidate;
        }

        throw new DirectoryNotFoundException("docs/configuration.md was not found above " + AppContext.BaseDirectory);
    }

    private static string HelpText()
    {
        var original = Console.Out;
        using var output = new StringWriter();
        Console.SetOut(output);
        try
        {
            Help.Print();
            return output.ToString();
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    /// <summary>The first word after <c>dbdatasync</c> in each command a text shows: inside inline code and code fences only,
    /// so prose like "dbdatasync itself" is not mistaken for a command.</summary>
    private static IEnumerable<string> CommandWords(string markdown, bool wholeTextIsCode)
    {
        var code = new List<string>();
        if (wholeTextIsCode)
            code.Add(markdown);
        else
        {
            var inFence = false;
            foreach (var line in markdown.Split('\n'))
            {
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) { inFence = !inFence; continue; }
                if (inFence) code.Add(line);
                else code.AddRange(Regex.Matches(line, "`([^`]+)`").Select(m => m.Groups[1].Value));
            }
        }

        return code.SelectMany(text => Regex.Matches(text, @"(?:^|[\s$])dbdatasync\s+([a-z][a-z-]*)")
            .Select(m => m.Groups[1].Value));
    }

    [Fact]
    public void EveryCommandTheDocsShow_IsOneTheCliHas()
    {
        var real = CommandWords(HelpText(), wholeTextIsCode: true).ToHashSet();
        Assert.Contains("serve", real); // the extraction found the help's commands at all

        // Machinery a document may name without it being something to type: install.md says systemd runs
        // `dbdatasync internal apply-update` before a start, and the command is deliberately absent from the help.
        real.Add("internal");

        var unknown = new List<string>();
        foreach (var file in Directory.GetFiles(DocsDirectory(), "*.md"))
        foreach (var word in CommandWords(File.ReadAllText(file), wholeTextIsCode: false).Distinct())
            if (!real.Contains(word))
                unknown.Add($"{Path.GetFileName(file)}: dbdatasync {word}");

        Assert.True(unknown.Count == 0,
            "The docs show commands the CLI does not have (see `dbdatasync` with no arguments for what exists): " + string.Join("; ", unknown));
    }
}
