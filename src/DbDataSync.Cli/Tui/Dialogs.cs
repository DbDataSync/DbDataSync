using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DbDataSync.Cli.Tui;

/// <summary>
/// The small set of one-off modal dialogs <c>dbdatasync setup</c> still needs outside the tabbed form —
/// the "which folder?" prompt before <see cref="SetupScreen"/> even has a root to key off of, and the
/// "type ALLOW" hard-confirm before disabling authentication. This, not a modal-per-field wizard, is
/// the actual reusable piece a future TUI screen wants: a couple of shared dialog builders over one
/// <see cref="TuiSession"/>, not a shared top-level layout.
/// </summary>
internal static class Dialogs
{
    /// <summary>A single text field with a pre-filled default — Enter/OK accepts it, Esc/Cancel keeps
    /// the default, exactly like <c>Prompt.Text</c>'s "press Enter to accept" console behaviour.</summary>
    internal static string PromptText(IApplication app, string title, string label, string @default)
    {
        var field = new TextField { X = 1, Y = 1, Width = Dim.Fill(1), Text = @default };
        var accepted = @default;

        var dialog = new Dialog { Title = title, Width = 60, Height = 5 };
        dialog.Add(new Label { X = 1, Y = 0, Text = label });

        var ok = new Button { Text = "OK", IsDefault = true };
        ok.Accepted += (_, _) => { accepted = field.Text; app.RequestStop(); };
        var cancel = new Button { Text = "Cancel" };
        cancel.Accepted += (_, _) => app.RequestStop();

        dialog.Add(field);
        dialog.AddButton(ok);
        dialog.AddButton(cancel);
        field.SetFocus();

        app.Run(dialog);
        return accepted;
    }

    /// <returns>True for Yes, false for No or Esc.</returns>
    internal static bool Confirm(IApplication app, string title, string message, bool @default)
    {
        var result = @default;
        var dialog = new Dialog { Title = title, Width = 60, Height = 5 };
        dialog.Add(new Label { X = 1, Y = 0, Width = Dim.Fill(1), Text = message });

        var yes = new Button { Text = "Yes", IsDefault = @default };
        yes.Accepted += (_, _) => { result = true; app.RequestStop(); };
        var no = new Button { Text = "No", IsDefault = !@default };
        no.Accepted += (_, _) => { result = false; app.RequestStop(); };

        dialog.AddButton(yes);
        dialog.AddButton(no);
        (@default ? yes : (View)no).SetFocus();

        app.Run(dialog);
        return result;
    }

    /// <summary>The guard in front of an irreversible or security-relevant choice (turning
    /// authentication off) — must type <paramref name="phrase"/> exactly, the same
    /// <c>Prompt.HardConfirm</c> semantics: a stray Enter is not enough to get here.</summary>
    internal static bool HardConfirm(IApplication app, string message, string phrase)
    {
        var field = new TextField { X = 1, Y = 2, Width = Dim.Fill(1), Text = "" };
        var typed = "";

        var dialog = new Dialog { Title = "Confirm", Width = 70, Height = 7 };
        dialog.Add(new Label { X = 1, Y = 0, Width = Dim.Fill(1), Text = message });
        dialog.Add(new Label { X = 1, Y = 1, Text = $"Type '{phrase}' to confirm, or Cancel:" });
        dialog.Add(field);

        var ok = new Button { Text = "OK", IsDefault = true };
        ok.Accepted += (_, _) => { typed = field.Text; app.RequestStop(); };
        var cancel = new Button { Text = "Cancel" };
        cancel.Accepted += (_, _) => app.RequestStop();

        dialog.AddButton(ok);
        dialog.AddButton(cancel);
        field.SetFocus();

        app.Run(dialog);
        return string.Equals(typed, phrase, StringComparison.Ordinal);
    }

    /// <summary>A read-only message, one OK button — today's <c>io.WriteLine</c> narration lines that
    /// need the operator's attention before moving on (a warning, a connection result).</summary>
    internal static void Message(IApplication app, string title, string text)
    {
        var dialog = new Dialog { Title = title, Width = 70, Height = 8 };
        // TextView is obsolete in favor of tui-cs/Editor's EditorView (a separate package, not added
        // here) — still the simplest built-in scrollable read-only view, and all this needs: showing
        // a config dump or a connection result, not editing.
#pragma warning disable CS0618
        dialog.Add(new TextView { X = 1, Y = 0, Width = Dim.Fill(1), Height = Dim.Fill(1), ReadOnly = true, Text = text });
#pragma warning restore CS0618

        var ok = new Button { Text = "OK", IsDefault = true };
        ok.Accepted += (_, _) => app.RequestStop();
        dialog.AddButton(ok);
        ok.SetFocus();

        app.Run(dialog);
    }
}
