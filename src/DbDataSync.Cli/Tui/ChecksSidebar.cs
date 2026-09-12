using System.Collections.ObjectModel;
using System.Text;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TGAttribute = Terminal.Gui.Drawing.Attribute;

namespace DbDataSync.Cli.Tui;

/// <summary>
/// The "checks update live while configuring" half of <see cref="SetupScreen"/> — a <see cref="FrameView"/>
/// wrapping a <see cref="ListView"/> bound straight to <see cref="ReadinessChecks"/>' own
/// <see cref="CheckResult"/> records, the same engine <c>config check</c> uses (not
/// <see cref="ReadinessChecks.FormatResult"/>'s flattened strings — that stays as-is for
/// <c>config check</c>'s plain-text path). <see cref="RefreshAsync"/> is called once when the screen
/// opens and again after every Save, so the sidebar reflects what is actually on disk, not unsaved
/// field edits.
/// </summary>
internal sealed class ChecksSidebar : FrameView
{
    private readonly ListView _list = new() { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
    private readonly ObservableCollection<string> _lines = [];

    // Parallel to _lines: which CheckStatus each rendered row belongs to, including every wrapped
    // continuation line — RowRender below looks this up by row index to color the row.
    private readonly List<CheckStatus> _rowStatuses = [];

    // A first render found every check line clipping mid-word (a path, a driver name) rather than
    // wrapping, because a fixed-width sidebar next to a fixed-width tab strip never fits everything a
    // real check message can say. `contentWidth` (the sidebar's own width, minus its border) drives
    // wrapping instead — see `Wrap`.
    private readonly int _contentWidth;

    public ChecksSidebar(int width)
    {
        Title = "Readiness";
        _contentWidth = Math.Max(width - 2, 20); // -2 for FrameView's own left/right border
        _list.SetSource(_lines);
        _list.RowRender += (_, e) =>
        {
            if (e.Row >= 0 && e.Row < _rowStatuses.Count)
                e.RowAttribute = ColorFor(_rowStatuses[e.Row]);
        };
        Add(_list);
    }

    public async Task RefreshAsync(string root)
    {
        var context = ReadinessChecks.BuildContext(["--repo", root]);
        var results = await ReadinessChecks.RunChecksAsync(context);

        _lines.Clear();
        _rowStatuses.Clear();
        foreach (var result in results)
        {
            foreach (var line in FormatWrapped(result))
            {
                _lines.Add(line);
                _rowStatuses.Add(result.Status);
            }
        }

        FirstAdminOutstanding = results.Any(r => r.Name == "First admin" && r.Status != CheckStatus.Ok);
    }

    /// <summary>Read by the bottom action bar — "Reissue invite" only makes sense while no admin exists
    /// yet, the same predicate <c>ReviewAsync</c>'s menu used.</summary>
    public bool FirstAdminOutstanding { get; private set; }

    private static TGAttribute ColorFor(CheckStatus status) => status switch
    {
        CheckStatus.Ok => new TGAttribute(ColorName16.Green, ColorName16.Black),
        CheckStatus.Warn => new TGAttribute(ColorName16.BrightYellow, ColorName16.Black),
        _ => new TGAttribute(ColorName16.BrightRed, ColorName16.Black),
    };

    /// <summary>One or more lines for <paramref name="result"/>, word-wrapped to <see cref="_contentWidth"/>
    /// with continuation lines indented under the text (not the marker), same glyphs
    /// <see cref="ReadinessChecks.FormatResult"/> uses for <c>config check</c>'s own report.</summary>
    private IEnumerable<string> FormatWrapped(CheckResult result)
    {
        var marker = result.Status switch
        {
            CheckStatus.Ok => "✓",
            CheckStatus.Warn => "!",
            _ => "✗",
        };

        var body = result.Fix is null
            ? $"{result.Name} — {result.Detail}"
            : $"{result.Name} — {result.Detail} (fix: {result.Fix})";

        var prefix = $"{marker} ";
        var indent = new string(' ', prefix.Length);
        var bodyWidth = Math.Max(_contentWidth - prefix.Length, 10);

        var wrapped = Wrap(body, bodyWidth);
        for (var i = 0; i < wrapped.Count; i++)
            yield return (i == 0 ? prefix : indent) + wrapped[i];
    }

    /// <summary>Greedy word-wrap at <paramref name="width"/>; a single token longer than the whole width
    /// (a long path, usually) is hard-broken rather than left to overrun the line.</summary>
    private static IReadOnlyList<string> Wrap(string text, int width)
    {
        var lines = new List<string>();
        var current = new StringBuilder();

        foreach (var word in text.Split(' '))
        {
            var remaining = word;
            if (current.Length > 0 && current.Length + 1 + remaining.Length > width)
            {
                lines.Add(current.ToString());
                current.Clear();
            }

            while (remaining.Length > width)
            {
                if (current.Length > 0)
                {
                    lines.Add(current.ToString());
                    current.Clear();
                }
                lines.Add(remaining[..width]);
                remaining = remaining[width..];
            }

            if (remaining.Length == 0)
                continue;

            if (current.Length > 0)
                current.Append(' ');
            current.Append(remaining);
        }

        if (current.Length > 0)
            lines.Add(current.ToString());

        return lines;
    }
}
