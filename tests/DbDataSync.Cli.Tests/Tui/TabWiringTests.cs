using Microsoft.Extensions.Configuration;
using DbDataSync.Cli.Tui;
using DbDataSync.Cli.Tui.Tabs;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;
using DbDataSync.Libraries;
using ClrKernel.Core.Secrets;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Terminal.Gui.Time;
using Terminal.Gui.Views;

namespace DbDataSync.Cli.Tests.Tui;

/// <summary>
/// The plan's "small number of genuine screen-behaviour tests" — real keystrokes through the real
/// Terminal.Gui widgets (<see cref="CheckBox"/>, <see cref="OptionSelector"/>, <see cref="TextField"/>),
/// headless via <c>Terminal.Gui.Testing.IInputInjector</c> (see
/// <see cref="TerminalGuiHeadlessSpikeTests"/> for how that was proven out). Every tab reuses just these
/// three control types, so covering them here — rather than repeating the same wiring proof six times,
/// once per tab — is what actually needed the TUI; the config/secret/git assertions themselves are
/// already covered at the UI-free layer by <see cref="SetupStepsTests"/>.
/// </summary>
public sealed class TabWiringTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-tabwiring-tests-").FullName;

    public void Dispose() => GitTempDirectory.DeleteRecursively(_root);

    [Fact]
    public void GeneralTab_TogglingReachable_SwitchesFromDirectUrlToHostAndPort()
    {
        using var app = Application.Create(new VirtualTimeProvider());
        app.Init("ansi");
        var injector = app.GetInputInjector();

        var tab = new GeneralTab();
        var window = new Window();
        window.Add(tab);

        RunOneIteration(app, window, () =>
        {
            tab._reachable.SetFocus();
            injector.InjectKey(Key.Space); // check it
            injector.ProcessQueue();
            injector.InjectKey(Key.Tab); // move to the hostname field
            injector.ProcessQueue();
            TypeText(injector, "example.com");
            injector.InjectKey(Key.Tab); // move to the port field
            injector.ProcessQueue();
            for (var i = 0; i < 4; i++) // clear the "5080" default, one Backspace at a time
                injector.InjectKey(Key.Backspace);
            injector.ProcessQueue();
            TypeText(injector, "5443");
        });

        var (url, host) = tab.GetValues();
        Assert.Equal("https://example.com:5443", url);
        Assert.Equal("example.com", host);
    }

    /// <summary>Phase 161: the Notes renderer choice is a checkbox on General, off unless the configuration says otherwise, and
    /// a real Space keystroke ticks it.</summary>
    [Fact]
    public void GeneralTab_NotesRichMarkdown_IsOffByDefault_TickedFromTheKeyboard_AndPopulatedFromTheConfiguration()
    {
        using var app = Application.Create(new VirtualTimeProvider());
        app.Init("ansi");
        var injector = app.GetInputInjector();

        var tab = new GeneralTab();
        var window = new Window();
        window.Add(tab);
        Assert.False(tab.NotesRichMarkdown);

        RunOneIteration(app, window, () =>
        {
            tab._notesRich.SetFocus();
            injector.InjectKey(Key.Space);
            injector.ProcessQueue();
        });
        Assert.True(tab.NotesRichMarkdown);

        var populated = new GeneralTab();
        populated.Populate(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DbDataSync:Notes:MarkdownRenderer"] = "rich" }).Build());
        Assert.True(populated.NotesRichMarkdown);
    }

    [Fact]
    public void GeneralTab_Wrap_KeepsEveryLineWithinTheWidth_AndLosesNoWords()
    {
        var text = DbDataSync.Api.Services.AdminConfigService.Writable("Notes:MarkdownRenderer")!.Caution!;

        var lines = GeneralTab.Wrap(text, 46).ToList();

        Assert.All(lines, line => Assert.True(line.Length <= 46, line));
        Assert.Equal(text.Split(' ', StringSplitOptions.RemoveEmptyEntries), string.Join(' ', lines).Split(' '));
    }

    /// <summary>Phase 109h: <see cref="StateDatabaseTab.SaveAsync"/> installs
    /// <c>microsoft-data-sqlclient</c> for real first (see <c>SetupStepsTests</c>' identical choice of a
    /// real install over a fake, for the same "must actually reach a real DbProviderFactory" reason).</summary>
    [Fact]
    public async Task StateDatabaseTab_SelectingSqlServerAndTyping_SavesTheTypedConnectionAndPassword()
    {
        using var app = Application.Create(new VirtualTimeProvider());
        app.Init("ansi");
        var injector = app.GetInputInjector();

        var tab = new StateDatabaseTab();
        var window = new Window();
        window.Add(tab);

        const string connectionString = "Server=127.0.0.1,1;Database=DbDataSyncState;Connect Timeout=1;";

        RunOneIteration(app, window, () =>
        {
            tab._engine.SetFocus();
            injector.InjectKey(Key.CursorDown); // move focus from SQLite to SQL Server
            injector.ProcessQueue();
            injector.InjectKey(Key.Space); // commit SQL Server as the selected engine
            injector.ProcessQueue();

            // OptionSelector has one tab stop per radio item (three, here) before Tab advances past it
            // — focusing the target fields directly is simpler than pressing Tab three times to escape.
            tab._connection.SetFocus();
            TypeText(injector, connectionString);
            tab._password.SetFocus();
            TypeText(injector, "hunter2");
        });

        var result = await tab.SaveAsync(_root, LibraryInstaller.InstallAsync);
        Assert.Contains("Could not connect yet", result.Message);

        var config = DbDataSyncConfigFile.Read(_root);
        Assert.Equal("MsSql", config["DbDataSync:State:Engine"]);
        Assert.Equal(connectionString, config["DbDataSync:State:ConnectionString"]);

        var secrets = new SecretStore("DbDataSync", true);
        Assert.True(secrets.TryResolve(SecretRefs.ForAppSetting("stateConnectionString"), out var password));
        Assert.Equal("hunter2", password);
        secrets.Delete(SecretRefs.ForAppSetting("stateConnectionString"));
    }

    private static void TypeText(Terminal.Gui.Testing.IInputInjector injector, string text)
    {
        foreach (var ch in text)
        {
            injector.InjectKey(new Key(ch));
            injector.ProcessQueue();
        }
    }

    /// <summary>Runs <paramref name="setup"/> on the first iteration (once <paramref name="window"/> has
    /// a driver and focus chain to act on) and stops the loop on the second — the same
    /// one-iteration-of-work, one-iteration-to-stop shape <see cref="TerminalGuiHeadlessSpikeTests"/>
    /// proved out.</summary>
    private static void RunOneIteration(IApplication app, IRunnable window, Action setup)
    {
        var iterations = 0;
        app.Iteration += (_, _) =>
        {
            iterations++;
            if (iterations == 1)
                setup();
            else
                app.RequestStop();
        };
        app.Run(window);
    }
}
