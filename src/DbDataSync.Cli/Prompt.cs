namespace DbDataSync.Cli;

/// <summary>
/// The seam between <see cref="Prompt"/> and however input/output actually happens — a real console
/// for an operator running <c>dbdatasync setup</c>, or a scripted list of answers for a test. Nothing
/// in <see cref="Prompt"/> touches <see cref="Console"/> directly, so a test drives the whole
/// interactive flow without a real terminal.
/// </summary>
public interface IPromptIo
{
    /// <summary>A line of input, or null at end of input (a scripted list that ran out — a test's own
    /// bug, treated as "no answer" rather than crashing the command under test).</summary>
    string? ReadLine();

    /// <summary>One key, without echoing it — what <see cref="Prompt.Secret"/> reads a character at a
    /// time so a password never appears on screen.</summary>
    ConsoleKeyInfo ReadKey(bool intercept);

    void Write(string s);

    void WriteLine(string s);

    /// <summary>Whether this is a real, interactive console — <c>false</c> means <c>setup</c> refuses
    /// to run at all rather than hanging on a <see cref="ReadLine"/> that will never return anything.</summary>
    bool IsInteractive { get; }
}

/// <summary>The real implementation, wrapping <see cref="Console"/>.</summary>
public sealed class ConsolePromptIo : IPromptIo
{
    public string? ReadLine() => Console.ReadLine();

    public ConsoleKeyInfo ReadKey(bool intercept) => Console.ReadKey(intercept);

    public void Write(string s) => Console.Write(s);

    public void WriteLine(string s) => Console.WriteLine(s);

    // A real console has neither stream redirected, and Console.IsInputRedirected/IsOutputRedirected
    // both answer that directly — no need to also probe Console.WindowHeight or similar, which throws
    // when there genuinely is no console at all (a Windows service, some CI runners).
    public bool IsInteractive => !Console.IsInputRedirected && !Console.IsOutputRedirected;
}

/// <summary>
/// Text-with-default, yes/no, single-choice, multi-choice, masked-secret, and a "type this exact
/// phrase" hard confirmation — the five shapes <c>dbdatasync setup</c>'s walk-through and review
/// screen need, all over <see cref="IPromptIo"/> so a test scripts an entire session.
/// </summary>
public sealed class Prompt(IPromptIo io)
{
    /// <summary>A line of text, or <paramref name="default"/> when the operator presses Enter on an
    /// empty line. Null default means the field is required — an empty answer re-prompts.</summary>
    public string Text(string label, string? @default = null)
    {
        while (true)
        {
            io.Write(@default is null ? $"{label}: " : $"{label} [{@default}]: ");
            var line = RequireLine(label);
            if (line.Length == 0)
            {
                if (@default is not null)
                    return @default;

                io.WriteLine("A value is required.");
                continue;
            }

            return line;
        }
    }

    public bool YesNo(string label, bool @default)
    {
        var hint = @default ? "Y/n" : "y/N";
        while (true)
        {
            io.Write($"{label} [{hint}]: ");
            var line = RequireLine(label).Trim();
            if (line.Length == 0)
                return @default;

            switch (line.ToLowerInvariant())
            {
                case "y" or "yes": return true;
                case "n" or "no": return false;
                default:
                    io.WriteLine("Please answer y or n.");
                    continue;
            }
        }
    }

    /// <summary>Numbered menu; an empty answer picks <paramref name="default"/>.</summary>
    public T Choice<T>(string label, IReadOnlyList<(T Value, string Label)> options, T @default)
    {
        io.WriteLine(label);
        var defaultIndex = options.ToList().FindIndex(o => Equals(o.Value, @default));
        for (var i = 0; i < options.Count; i++)
        {
            var marker = i == defaultIndex ? "*" : " ";
            io.WriteLine($"  {marker}{i + 1}) {options[i].Label}");
        }

        while (true)
        {
            io.Write($"Choose 1-{options.Count}" + (defaultIndex >= 0 ? $" [{defaultIndex + 1}]: " : ": "));
            var line = RequireLine(label).Trim();
            if (line.Length == 0)
            {
                if (defaultIndex >= 0)
                    return @default;

                io.WriteLine("A choice is required.");
                continue;
            }

            if (int.TryParse(line, out var n) && n >= 1 && n <= options.Count)
                return options[n - 1].Value;

            io.WriteLine($"Enter a number from 1 to {options.Count}.");
        }
    }

    /// <summary>Space- or comma-separated numbers; an empty answer selects nothing.</summary>
    public IReadOnlyList<T> MultiChoice<T>(string label, IReadOnlyList<(T Value, string Label)> options)
    {
        io.WriteLine(label);
        for (var i = 0; i < options.Count; i++)
            io.WriteLine($"  {i + 1}) {options[i].Label}");

        while (true)
        {
            io.Write($"Choose any of 1-{options.Count}, space- or comma-separated (blank for none): ");
            var line = RequireLine(label).Trim();
            if (line.Length == 0)
                return [];

            var tokens = line.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
            var selected = new List<T>();
            var bad = false;
            foreach (var token in tokens)
            {
                if (int.TryParse(token, out var n) && n >= 1 && n <= options.Count)
                {
                    selected.Add(options[n - 1].Value);
                }
                else
                {
                    io.WriteLine($"'{token}' is not a number from 1 to {options.Count}.");
                    bad = true;
                    break;
                }
            }

            if (!bad)
                return selected;
        }
    }

    /// <summary>Read a character at a time with no echo, so a password never appears on screen —
    /// including in a terminal's own scrollback.</summary>
    public string Secret(string label)
    {
        io.Write($"{label}: ");
        var chars = new List<char>();
        while (true)
        {
            var key = io.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                break;

            if (key.Key == ConsoleKey.Backspace)
            {
                if (chars.Count > 0)
                    chars.RemoveAt(chars.Count - 1);
                continue;
            }

            if (key.KeyChar != '\0')
                chars.Add(key.KeyChar);
        }

        io.WriteLine("");
        return new string([.. chars]);
    }

    /// <summary>Must type <paramref name="phrase"/> exactly — the guard in front of an irreversible or
    /// security-relevant choice (turning authentication off), where a stray Enter on a yes/no prompt
    /// would be too cheap a way to get there.</summary>
    public string HardConfirm(string label, string phrase)
    {
        io.WriteLine(label);
        io.Write($"Type '{phrase}' to confirm, or anything else to cancel: ");
        return io.ReadLine() ?? "";
    }

    /// <summary>
    /// A non-null answer, or an exception naming <paramref name="label"/> — <see cref="IPromptIo.ReadLine"/>
    /// returning null means input has genuinely ended (a closed stdin mid-session, or a
    /// <c>ScriptedPromptIo</c> whose answer list ran out), which every required-input loop above used to
    /// treat exactly like an empty line. That conflation was a real infinite loop: an exhausted script
    /// answering a required prompt with no default would print "a value is required" and call
    /// <see cref="IPromptIo.ReadLine"/> again forever, since null never becomes non-null on retry.
    /// </summary>
    private string RequireLine(string label)
    {
        var line = io.ReadLine();
        if (line is null)
            throw new InvalidOperationException($"No answer given for '{label}' — input ended.");
        return line;
    }
}
