namespace DbDataSync.DevHarness;

/// <summary>Console output for a tool a person watches while it runs. Colour-coded and prefixed so
/// the harness's own progress stays legible against the API and Vite output it tails.</summary>
public static class Log
{
    private static readonly Lock Gate = new();

    public static void Step(string message) => Write(ConsoleColor.Cyan, "==>", message);

    public static void Info(string message) => Write(ConsoleColor.Gray, "   ", message);

    public static void Ok(string message) => Write(ConsoleColor.Green, " ok", message);

    public static void Warn(string message) => Write(ConsoleColor.Yellow, "  !", message);

    public static void Error(string message) => Write(ConsoleColor.Red, "!!!", message);

    /// <summary>A line from a child process, tagged with which one it came from.</summary>
    public static void Child(string tag, string message) => Write(ConsoleColor.DarkGray, $"[{tag}]", message);

    private static void Write(ConsoleColor color, string prefix, string message)
    {
        lock (Gate)
        {
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.Write($"{prefix} ");
            Console.ForegroundColor = previous;
            Console.WriteLine(message);
        }
    }
}
