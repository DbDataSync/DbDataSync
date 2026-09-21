using DbDataSync.Cli.Tui;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Terminal.Gui.Time;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DbDataSync.Cli.Tests.Tui;

/// <summary>
/// Renders the real <see cref="SetupScreen"/> headlessly and checks what actually drew, not what the
/// code intends to draw — phase 128's rework shipped once already with four real layout bugs
/// (truncated tabs, clipped sidebar text, off-screen buttons, no status color) that every other test in
/// this project was green through, because they only ever asserted on values, never on the render. Also
/// the tool for capturing a fresh screenshot on demand: re-run this test, then open the file
/// <see cref="TerminalCapture.SaveHtmlAsync"/> writes.
/// </summary>
public sealed class SetupScreenCaptureTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-capture-tests-").FullName;

    public void Dispose() => GitTempDirectory.DeleteRecursively(_root);

    [Fact]
    public async Task FreshSetupScreen_At80x25_RendersEveryTabAndActionButtonOnScreen()
    {
        ServeCommand.Prepare(_root);

        using var app = Application.Create(new SystemTimeProvider());
        app.Init("ansi");

        string? plainText = null;
        var iterations = 0;
        app.Iteration += (_, _) =>
        {
            iterations++;
            if (iterations < 2)
                return; // let one full layout+draw pass happen first

            plainText = TerminalCapture.ToPlainText(app);
            app.RequestStop();
        };

        await SetupScreen.RunAsync(_root, app, FailingInstallLibrary);
        await TerminalCapture.SaveHtmlAsync(app, "dbdatasync setup — fresh repo, 80x25", "dbdatasync-setup-screen.html");

        Assert.NotNull(plainText);

        // Every tab's title must actually appear — not truncated into an ellipsis or scrolled off, the
        // exact failure this test caught the first time (Authentication cut to "Au", Service and
        // Certificate never drawn at all).
        foreach (var tabTitle in new[] { "General", "State DB", "Drivers", "Auth", "Service", "Cert" })
            Assert.Contains(tabTitle, plainText);

        // Every action-bar button must be reachable, not laid out past column 80 — the other failure
        // this test caught: "Reissue invite" and "Exit" rendered off the right edge entirely.
        foreach (var buttonLabel in new[] { "Save", "Start", "Print config", "Reissue invite", "Exit" })
            Assert.Contains(buttonLabel, plainText);

        Assert.DoesNotContain("Found '/etc/dotnet/install_", plainText); // a check line clipped mid-word
    }

    /// <summary>
    /// Regression test for a real bug reported from manual testing on Windows: keyboard/mouse navigation
    /// never reached the action bar (Save/Start/Print config/Reissue invite/Exit) at all, so Exit
    /// couldn't be triggered from the keyboard or (per the report) reliably by mouse either. Root cause —
    /// <c>actionBar</c> in <c>SetupScreen.cs</c> is a plain container <see cref="View"/>, which
    /// Terminal.Gui defaults to <c>CanFocus = false</c>. Every tab already has its own copy of this exact
    /// trap (see <c>GeneralTab.cs</c>'s "container Views default to non-focusable" comment) — this
    /// container was just the one missed. Setting <see cref="View.HasFocus"/> on a view recursively
    /// requires every SuperView up the chain to also be focusable, so a non-focusable container makes
    /// every button inside it unreachable regardless of driver — nothing Windows-specific about it, just
    /// never exercised by <see cref="TerminalGuiHeadlessSpikeTests"/>-style input injection before now.
    /// </summary>
    [Fact]
    public async Task ExitButton_CanBeFocusedAndActivatedFromTheKeyboard()
    {
        ServeCommand.Prepare(_root);

        using var app = Application.Create(new SystemTimeProvider());
        app.Init("ansi");
        var injector = app.GetInputInjector();

        var iterations = 0;
        Exception? caught = null;
        bool? focused = null;
        app.Iteration += (_, _) =>
        {
            iterations++;
            try
            {
                if (iterations == 2)
                {
                    var exitButton = Descendants(app.TopRunnableView!).OfType<Button>().Single(b => b.Text == "Exit");
                    focused = exitButton.SetFocus();
                    injector.InjectKey(Key.Enter);
                    injector.ProcessQueue();
                }
                else if (iterations >= 5)
                {
                    app.RequestStop(); // safety net — never let a failure here hang the run loop forever
                }
            }
            catch (Exception ex)
            {
                caught = ex;
                app.RequestStop();
            }
        };

        var exitCode = await SetupScreen.RunAsync(_root, app, FailingInstallLibrary);

        Assert.Null(caught);
        Assert.True(focused, "Exit could not be focused — did actionBar lose its CanFocus = true?");
        Assert.Equal(0, exitCode); // Exit must not start the service
    }

    private static IEnumerable<View> Descendants(View view)
    {
        foreach (var sub in view.SubViews)
        {
            yield return sub;
            foreach (var descendant in Descendants(sub))
                yield return descendant;
        }
    }

    private static Task<DbDataSync.Libraries.LibraryManifest> FailingInstallLibrary(
        string repoRoot, string id, IReadOnlyList<DbDataSync.Libraries.PackageRef> packages, string factoryType,
        string? source, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Not exercised by this capture.");
}
