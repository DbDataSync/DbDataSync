using DbDataSync.Api.Services;
using DbDataSync.Core.Config;
using Microsoft.Extensions.Configuration;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DbDataSync.Cli.Tui.Tabs;

/// <summary>The console URL — old step 2. "Reachable at a hostname other than localhost?" toggles
/// between a plain URL field and a hostname+port pair that composes an <c>https://</c> URL, exactly the
/// two paths <c>SetupCommand.WalkThroughAsync</c>'s old step 2 offered.</summary>
internal sealed class GeneralTab : View
{
    // internal, not private: TabWiringTests focuses these directly to drive real keystrokes through
    // them — SetFocus() on the containing tab view does not reliably delegate to a specific descendant.
    internal readonly CheckBox _reachable = new() { X = 1, Y = 0, Text = "Reachable at a hostname other than localhost?" };
    private readonly Label _hostLabel = new() { X = 1, Y = 2, Text = "Hostname:" };
    private readonly TextField _host = new() { X = 14, Y = 2, Width = 30 };
    private readonly Label _portLabel = new() { X = 1, Y = 3, Text = "Port:" };
    private readonly TextField _port = new() { X = 14, Y = 3, Width = 10, Text = "5080" };
    private readonly Label _urlLabel = new() { X = 1, Y = 2, Text = "Console URL:" };
    private readonly TextField _url = new() { X = 14, Y = 2, Width = 40, Text = "http://localhost:5080" };

    /// <summary>Phase 161: whether Notes render with the full Markdown renderer. The warning under it is the catalog's own text
    /// (the Admin screen shows the same words), shown whether the box is ticked or not — it is what someone deciding needs to
    /// read, not a confirmation after the fact.</summary>
    internal readonly CheckBox _notesRich = new() { X = 1, Y = 5, Text = "Render Notes with the full Markdown renderer" };
    private readonly Label _notesWarning = new() { X = 4, Y = 6, Text = NotesWarningText() };

    public GeneralTab()
    {
        Title = "General";
        CanFocus = true; // container Views default to non-focusable; without this the focus chain never reaches these fields
        Add(_reachable, _hostLabel, _host, _portLabel, _port, _urlLabel, _url, _notesRich, _notesWarning);
        _reachable.ValueChanged += (_, _) => UpdateVisibility();
        UpdateVisibility();
    }

    private static string NotesWarningText()
    {
        var caution = AdminConfigService.Writable("NotesRichMarkdown")?.Caution ?? "";
        return string.Join('\n', Wrap(caution, 46));
    }

    /// <summary>Greedy word wrap — Label does not wrap, and a warning cut off at the tab's edge is worse than none.</summary>
    internal static IEnumerable<string> Wrap(string text, int width)
    {
        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                yield return line;
                line = word;
            }
            else
                line = line.Length == 0 ? word : $"{line} {word}";
        }
        if (line.Length > 0)
            yield return line;
    }

    /// <summary>Whether the box is ticked, for <see cref="SetupSteps.ApplyNotesRichMarkdown"/>.</summary>
    public bool NotesRichMarkdown => _notesRich.Value == CheckState.Checked;

    private void UpdateVisibility()
    {
        var reachable = _reachable.Value == CheckState.Checked;
        _hostLabel.Visible = reachable;
        _host.Visible = reachable;
        _portLabel.Visible = reachable;
        _port.Visible = reachable;
        _urlLabel.Visible = !reachable;
        _url.Visible = !reachable;
    }

    /// <summary><c>Host</c> is null when the operator typed a plain URL directly — the same "no
    /// hostname to derive a relying-party id or a certificate DNS name from" case the console flow
    /// left <c>host</c> null for.</summary>
    public (string Url, string? Host) GetValues() =>
        _reachable.Value == CheckState.Checked
            ? ($"https://{_host.Text}:{_port.Text}", _host.Text)
            : (_url.Text, null);

    /// <summary>Reviewing an existing install always shows the direct-URL field, even if it was
    /// originally entered as hostname+port — reversing that composition isn't worth the complexity
    /// for a value the operator can just retype.</summary>
    public void Populate(IConfiguration configuration)
    {
        var url = configuration["DbDataSync:Url"];
        if (!string.IsNullOrEmpty(url))
            _url.Text = url;

        _notesRich.Value = bool.TryParse(configuration["DbDataSync:NotesRichMarkdown"], out var rich) && rich
            ? CheckState.Checked
            : CheckState.UnChecked;
    }
}
