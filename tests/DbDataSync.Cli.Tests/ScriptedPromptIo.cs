namespace DbDataSync.Cli.Tests;

/// <summary>
/// Drives a <see cref="Prompt"/> from a fixed answer list, the same way a real console drives one from
/// an operator's typing — <see cref="PromptTests"/>' and <see cref="SetupCommandTests"/>'s stand-in for
/// an interactive session.
/// </summary>
internal sealed class ScriptedPromptIo : IPromptIo
{
    private readonly Queue<string?> _lines;
    private readonly Queue<ConsoleKeyInfo> _keys;

    public ScriptedPromptIo(IEnumerable<string?> lines, IEnumerable<ConsoleKeyInfo>? keys = null)
    {
        _lines = new Queue<string?>(lines);
        _keys = new Queue<ConsoleKeyInfo>(keys ?? []);
    }

    public List<string> Written { get; } = [];

    /// <summary>Null once the script runs out — exactly what a real closed stdin returns, and what
    /// exercises <see cref="Prompt"/>'s own end-of-input handling.</summary>
    public string? ReadLine() => _lines.Count > 0 ? _lines.Dequeue() : null;

    public ConsoleKeyInfo ReadKey(bool intercept) => _keys.Dequeue();

    public void Write(string s) => Written.Add(s);

    public void WriteLine(string s) => Written.Add(s);

    public bool IsInteractive => true;

    public static ConsoleKeyInfo Char(char c) => new(c, ConsoleKey.NoName, false, false, false);

    public static ConsoleKeyInfo Enter => new('\r', ConsoleKey.Enter, false, false, false);

    public static ConsoleKeyInfo Backspace => new('\0', ConsoleKey.Backspace, false, false, false);
}

/// <summary>A non-interactive stand-in — <c>setup</c>'s "refuse to run" gate is the one thing this
/// exists for.</summary>
internal sealed class NonInteractivePromptIo : IPromptIo
{
    public List<string> Written { get; } = [];
    public string? ReadLine() => null;
    public ConsoleKeyInfo ReadKey(bool intercept) => throw new NotSupportedException();
    public void Write(string s) => Written.Add(s);
    public void WriteLine(string s) => Written.Add(s);
    public bool IsInteractive => false;
}
