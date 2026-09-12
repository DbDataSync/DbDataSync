using System.Text;
using Terminal.Gui.App;
using TGAttribute = Terminal.Gui.Drawing.Attribute;

namespace DbDataSync.Cli.Tests.Tui;

/// <summary>
/// Renders a running <see cref="IApplication"/>'s actual off-screen buffer (Terminal.Gui's own
/// <c>IDriver.Contents</c> cell grid — already-resolved characters and RGB, not a re-parsed ANSI escape
/// stream) so a test can capture and inspect real layout, not a guess at it. Grew out of phase 128's
/// TUI rework, where a captured render found four real layout bugs a plain green test run would have
/// missed entirely — kept as a permanent utility for the same reason on every future TUI screen.
/// </summary>
internal static class TerminalCapture
{
    /// <summary>The grid as plain text, one line per row — cheap to assert against in a test
    /// (<c>Assert.Contains("Reachable at a hostname", ...)</c>) without touching HTML at all.</summary>
    /// <param name="asciiSafe">See <see cref="AsciiSafe"/> — default <c>true</c> for anything asserted
    /// against in a test; pass <c>false</c> only to look at Terminal.Gui's real drawing characters.</param>
    internal static string ToPlainText(IApplication app, bool asciiSafe = true)
    {
        var contents = app.Driver!.Contents!;
        var rows = contents.GetLength(0);
        var cols = contents.GetLength(1);
        var sb = new StringBuilder();

        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
                sb.Append(asciiSafe ? AsciiSafe(contents[r, c].Grapheme) : Blank(contents[r, c].Grapheme));
            sb.Append('\n');
        }

        return sb.ToString();
    }

    private static string Blank(string grapheme) => grapheme.Length == 0 ? " " : grapheme;

    /// <summary>
    /// Reduces Terminal.Gui's own drawing characters to plain ASCII — the second attempt at rendering
    /// this correctly relied on a font with full Unicode box-drawing/symbol coverage, which does not
    /// exist reliably across viewers (desktop got there with Menlo; a phone's browser still rendered
    /// borders, checkboxes and button decorations as blank or badly-fallback-sized). Font hunting is a
    /// dead end for characters this obscure (⟦⟧▝▖▘ are not in most fonts at all); ASCII is the one
    /// character set every device actually agrees on. Only 1:1 single-character substitutions — swapping
    /// in a multi-character string would shift every following cell on the row out of alignment.
    /// </summary>
    private static string AsciiSafe(string grapheme) => grapheme switch
    {
        { Length: 0 } => " ",

        // Box drawing — single, double, heavy, and rounded corners/junctions all collapse to '+'; a web
        // capture doesn't need to distinguish the line weight or corner style Terminal.Gui chose.
        "┌" or "┐" or "└" or "┘" or "├" or "┤" or "┬" or "┴" or "┼"
            or "╔" or "╗" or "╚" or "╝" or "╠" or "╣" or "╦" or "╩" or "╬"
            or "┏" or "┓" or "┗" or "┛" or "┣" or "┫" or "┳" or "┻" or "╋"
            or "╭" or "╮" or "╰" or "╯" => "+",
        "─" or "━" or "╌" or "╍" => "-",
        "│" or "┃" or "╎" or "╏" => "|",
        "═" => "=",
        "║" => "|",

        // Block/quadrant elements (shadows, shading) — the cell's own background color already carries
        // the visual weight, so the glyph itself can safely go blank rather than risk another tofu box.
        "▀" or "▄" or "█" or "▌" or "▐" or "░" or "▒" or "▓"
            or "▘" or "▝" or "▖" or "▗" or "▚" or "▞" => " ",

        // Button and selection chrome
        "⟦" => "[",
        "⟧" => "]",
        "►" => ">",
        "◄" => "<",
        "▲" => "^",
        "▼" => "v",

        // Checkbox / radio states
        "☐" => "_",
        "☑" or "☒" => "X",
        "○" => "(",
        "●" => "*",

        // Everything else — plain ASCII, and the handful of dingbats (✓ ✗ —) confirmed to render
        // correctly everywhere this has actually been viewed — passes through unchanged.
        var g => g,
    };

    /// <summary>A self-contained HTML page — one <c>&lt;span&gt;</c> per cell, colored from its actual
    /// <see cref="TGAttribute"/> — styled as a terminal window so it's readable dropped anywhere with no
    /// further wrapping needed.</summary>
    /// <param name="asciiSafe">See <see cref="AsciiSafe"/> — default <c>true</c>, the proven-reliable
    /// path. <c>false</c> renders Terminal.Gui's real box-drawing/symbol characters as-is: worth trying
    /// again once the font-family and width-clamp defenses below are in place, but not guaranteed on
    /// every viewer the way the substituted characters are.</param>
    internal static string ToHtml(IApplication app, string caption, bool asciiSafe = true)
    {
        var driver = app.Driver!;
        var contents = driver.Contents!;
        var rows = contents.GetLength(0);
        var cols = contents.GetLength(1);

        var grid = new StringBuilder();
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                var cell = contents[r, c];
                var raw = asciiSafe ? AsciiSafe(cell.Grapheme) : Blank(cell.Grapheme);
                var text = System.Net.WebUtility.HtmlEncode(raw);
                var fg = cell.Attribute?.Foreground;
                var bg = cell.Attribute?.Background;
                var fgCss = fg is { } f ? $"rgb({f.R},{f.G},{f.B})" : "#ccc";
                var bgCss = bg is { } b ? $"rgb({b.R},{b.G},{b.B})" : "#000";
                grid.Append($"<span class='tg-cell' style='color:{fgCss};background:{bgCss}'>{text}</span>");
            }
            grid.Append('\n');
        }

        // Not 'JetBrains Mono' first, and every span clamped to exactly one character's width: a real
        // capture is mostly box-drawing runes (─│┌┐└┘├┤╭╮╰╯☐), and a hosted/partial build of a font
        // missing those glyphs makes the browser fall back per-glyph to something with a different
        // advance width — which blows the monospace grid apart cell by cell rather than failing loudly.
        // Menlo/Consolas/DejaVu Sans Mono are common system fonts with full box-drawing coverage; the
        // width clamp is the second line of defense for whatever fallback chain a viewer actually has.
        return $$"""
            <!doctype html><meta charset=utf-8><title>{{System.Net.WebUtility.HtmlEncode(caption)}}</title>
            <style>.tg-cell{display:inline-block;width:1ch;overflow:hidden;vertical-align:top}</style>
            <body style="background:#111;margin:0;padding:16px;font-family:system-ui,sans-serif;color:#999">
            <p style="font:12px/1.4 monospace;margin:0 0 8px">{{System.Net.WebUtility.HtmlEncode(caption)}} — {{cols}}&times;{{rows}}</p>
            <pre style="font-family:Menlo,Consolas,'DejaVu Sans Mono',monospace;font-size:14px;line-height:1.25;
                        color:#ccc;background:#000;padding:10px;border-radius:6px;width:fit-content;margin:0">
            {{grid}}</pre>
            </body>
            """;
    }

    /// <summary>Writes <see cref="ToHtml"/> to a fixed, predictable path outside the repo — re-running
    /// the test that calls this is how a future capture happens; no path to remember or invent.</summary>
    internal static async Task SaveHtmlAsync(IApplication app, string caption, string fileName, bool asciiSafe = true)
    {
        var path = Path.Combine(Path.GetTempPath(), fileName);
        await File.WriteAllTextAsync(path, ToHtml(app, caption, asciiSafe));
    }
}
