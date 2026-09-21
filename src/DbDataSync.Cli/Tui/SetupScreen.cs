using DbDataSync.Cli.Tui.Tabs;
using DbDataSync.Libraries;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TGAttribute = Terminal.Gui.Drawing.Attribute;

namespace DbDataSync.Cli.Tui;

/// <summary>
/// The one screen <c>dbdatasync setup</c> shows, for a fresh root or an existing one alike — a tabbed
/// form (one tab per config section, mirroring the old console walk-through's steps 2–7) with a
/// <see cref="ChecksSidebar"/> beside it, and a persistent action bar below both. There is no separate
/// review screen any more: an existing install just opens with its tabs pre-filled.
/// </summary>
internal static class SetupScreen
{
    internal static async Task<int> RunAsync(
        string root, IApplication app,
        Func<string, string, IReadOnlyList<PackageRef>, string, string?, CancellationToken, Task<LibraryManifest>> installLibrary)
    {
        var window = new Window { Title = $"DbDataSync setup — {root}" };

        // Terminal.Gui's default "Base" scheme deliberately leaves Normal's colors as `None` — "inherit
        // whatever the real terminal's own default colors are" — which is fine on a real terminal but
        // resolves inconsistently with no terminal to query (a first capture found the whole screen
        // rendering black-on-white except ChecksSidebar, the one place already setting real colors
        // explicitly). Setting one explicit Scheme here — Terminal.Gui derives sensible Focus/Disabled
        // variants from it automatically — makes every descendant consistent instead of ambient.
        window.SetScheme(new Scheme(new TGAttribute(new Color(ColorName16.White), new Color(ColorName16.Black))));

        var general = new GeneralTab();
        var stateDatabase = new StateDatabaseTab();
        var drivers = new DriversTab();
        var authentication = new AuthenticationTab();
        var service = OperatingSystem.IsWindows() || OperatingSystem.IsLinux() ? new ServiceTab() : null;
        var certificate = new CertificateTab();

        if (ExistingSetup.DetectedAt(root))
        {
            var configuration = ReadinessChecks.BuildContext(["--repo", root]).Configuration;
            general.Populate(configuration);
            stateDatabase.Populate(configuration);
            authentication.Populate(configuration);
        }

        const int sidebarWidth = 40;
        var sidebar = new ChecksSidebar(sidebarWidth) { X = Pos.AnchorEnd(sidebarWidth), Y = 0, Width = sidebarWidth, Height = Dim.Fill(4) };
        // Terminal.Gui.Views.Tabs, fully qualified — DbDataSync.Cli.Tui.Tabs (this project's own tab
        // classes) is a namespace of the same name in scope from the `using` above.
        var tabs = new Terminal.Gui.Views.Tabs { X = 0, Y = 0, Width = Dim.Fill(sidebarWidth), Height = Dim.Fill(4) };
        tabs.InsertTab(0, general);
        tabs.InsertTab(1, stateDatabase);
        tabs.InsertTab(2, drivers);
        tabs.InsertTab(3, authentication);
        var nextIndex = 4;
        if (service is not null)
            tabs.InsertTab(nextIndex++, service);
        tabs.InsertTab(nextIndex, certificate);

        var savedUrl = "http://localhost:5080";
        var startRequested = false;
        var exitCode = 0;

        // Two rows, not one growing chain — a first render showed a single Pos.Right(prev)+2 chain of
        // all five buttons running well past column 80: "Reissue invite" and "Exit" were laid out, just
        // never visible or reachable. Labels are shortened too (full sentences were most of the width
        // problem), and the two rows plus their shadows fit inside actionBar's existing 4-row height.
        var saveButton = new Button { X = 0, Y = 0, Text = "Save", IsDefault = true };
        var startButton = new Button { X = Pos.Right(saveButton) + 2, Y = 0, Text = "Start" };
        var printButton = new Button { X = Pos.Right(startButton) + 2, Y = 0, Text = "Print config" };
        var reissueButton = new Button { X = 0, Y = 2, Text = "Reissue invite" };
        var exitButton = new Button { X = Pos.Right(reissueButton) + 2, Y = 2, Text = "Exit" };

        saveButton.Accepted += async (_, _) => await SaveAsync();
        printButton.Accepted += (_, _) => PrintConfiguration();
        reissueButton.Accepted += (_, _) =>
            Dialogs.Message(app, "Invite", CaptureConsole(() => InviteCommand.Run(["--repo", root])));
        startButton.Accepted += (_, _) =>
        {
            startRequested = true;
            app.RequestStop();
        };
        exitButton.Accepted += (_, _) => app.RequestStop();

        var actionBar = new View
        {
            X = 0, Y = Pos.AnchorEnd(4), Width = Dim.Fill(), Height = 4,
            CanFocus = true, // container Views default to non-focusable; without this the focus chain never reaches these buttons — same fix every tab already needed (see GeneralTab's own copy of this comment)
        };
        actionBar.Add(saveButton, startButton, printButton, reissueButton, exitButton);

        window.Add(tabs, sidebar, actionBar);

        await sidebar.RefreshAsync(root);
        reissueButton.Visible = sidebar.FirstAdminOutstanding;

        app.Run(window);

        return startRequested ? await ServeCommand.RunAsync(["--repo", root, "--url", savedUrl]) : exitCode;

        async Task SaveAsync()
        {
            var (url, host) = general.GetValues();
            savedUrl = url;
            SetupSteps.ApplyConsoleUrl(root, url);
            SetupSteps.ApplyNotesRenderer(root, general.NotesRichMarkdown);

            var messages = new List<string> { (await stateDatabase.SaveAsync(root, installLibrary)).Message };

            var driverResult = await drivers.SaveAsync(root, installLibrary);
            if (driverResult is { } result)
                messages.Add(result.Message);

            messages.Add(authentication.Save(app, root, url, host).Message);

            var serviceLines = service?.GetInstructions(root, url);
            var certificateLines = certificate.GetInstructions(root, host);

            SetupSteps.CommitChanges(root);
            await sidebar.RefreshAsync(root);
            reissueButton.Visible = sidebar.FirstAdminOutstanding;

            if (serviceLines is not null)
                messages.AddRange(["", .. serviceLines]);
            if (certificateLines is not null)
                messages.AddRange(["", .. certificateLines]);

            Dialogs.Message(app, "Saved", string.Join('\n', messages));
        }

        void PrintConfiguration()
        {
            var configuration = ReadinessChecks.BuildContext(["--repo", root]).Configuration;
            var lines = SetupSteps.EffectiveConfigurationLines(configuration);
            Dialogs.Message(app, "Effective configuration", string.Join('\n', lines));
        }
    }

    /// <summary>Redirects a command's <c>Console</c> writes into the dialog they belong in, rather than
    /// letting them land on the real console while a Terminal.Gui driver owns the screen —
    /// <c>InviteCommand.Run</c> is an ordinary CLI command, not TUI-aware.</summary>
    private static string CaptureConsole(Func<int> action)
    {
        var original = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            action();
        }
        finally
        {
            Console.SetOut(original);
        }

        return writer.ToString();
    }
}
