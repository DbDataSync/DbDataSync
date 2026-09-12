using Terminal.Gui.App;
using Terminal.Gui.Time;

namespace DbDataSync.Cli.Tui;

/// <summary>
/// Owns the one <see cref="IApplication"/> instance for a <c>dbdatasync setup</c> session — one
/// init/dispose bracket for the whole command, not one per screen or dialog. Built on Terminal.Gui's
/// instance-based <c>IApplication</c> (<see cref="Application.Create"/>), not the static
/// <c>Application.Init/Run/Shutdown/RaiseKeyDownEvent</c> facade — the shipped 2.5.0 package marks that
/// facade <c>[Obsolete]</c> ("The legacy static Application object is going away"), confirmed against
/// the actual package during phase 128's spike rather than assumed from older Terminal.Gui docs. A
/// future TUI screen reuses this the same way <see cref="SetupScreen"/> does: one <see cref="Start"/>,
/// one <see cref="IDisposable.Dispose"/>.
/// </summary>
internal sealed class TuiSession : IDisposable
{
    public IApplication App { get; }

    private TuiSession(IApplication app) => App = app;

    /// <param name="driverName">Null lets Terminal.Gui pick the right driver for the current host — the
    /// real path. Tests pass <c>"ansi"</c> explicitly, the driver phase 128's headless spike proved
    /// runs with no real terminal at all (an off-screen buffer, exactly what `dotnet test` gives it).</param>
    public static TuiSession Start(string? driverName = null)
    {
        var app = Application.Create(new SystemTimeProvider());
        app.Init(driverName!);
        return new TuiSession(app);
    }

    public void Dispose() => (App as IDisposable)?.Dispose();
}
