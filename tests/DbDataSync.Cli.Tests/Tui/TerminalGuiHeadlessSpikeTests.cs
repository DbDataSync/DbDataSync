using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Terminal.Gui.Time;
using Terminal.Gui.Views;

namespace DbDataSync.Cli.Tests.Tui;

/// <summary>
/// The plan's Pass-1 spike, kept as a real test: proves Terminal.Gui 2.5.0 can be driven headlessly —
/// no real terminal, redirected stdio, exactly `dotnet test` under CI's `ubuntu-latest` runner — before
/// any of <c>SetupScreen</c>'s actual tabs are built against the approach.
///
/// The package ships a first-class, non-obsolete testing surface for this
/// (<c>Terminal.Gui.Testing.IInputInjector</c>, reached via <c>IApplication.GetInputInjector()</c>) —
/// better than the "replicate their internal FakeDriver" fallback the plan expected, so this is what
/// <c>TuiSession</c> and every future TUI test build on. One correction the spike surfaced: the static
/// <c>Application.Init/Run/Shutdown/RaiseKeyDownEvent</c> façade is marked <c>[Obsolete]</c> in 2.5.0
/// ("The legacy static Application object is going away") — this uses, and the real implementation
/// must use, the instance-based <see cref="Terminal.Gui.App.Application.Create"/> /
/// <see cref="IApplication"/> API instead.
/// </summary>
public sealed class TerminalGuiHeadlessSpikeTests
{
    [Fact]
    public void InjectedKeystrokes_ReachAFocusedTextField()
    {
        using var app = Application.Create(new VirtualTimeProvider());
        app.Init("ansi");

        var field = new TextField { X = 1, Y = 1, Width = 20, Text = "" };
        var window = new Window { Title = "spike" };
        window.Add(field);

        var injector = app.GetInputInjector();
        var iterations = 0;
        app.Iteration += (_, _) =>
        {
            iterations++;
            if (iterations == 1)
            {
                field.SetFocus();
                foreach (var ch in "hi")
                    injector.InjectKey(new Key(ch));
                injector.ProcessQueue();
            }
            else
            {
                app.RequestStop();
            }
        };

        app.Run(window);

        Assert.Equal("hi", field.Text);
        Assert.Equal(2, iterations);
    }
}
