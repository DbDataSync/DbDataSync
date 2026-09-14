using DbDataSync.Libraries;
using DbDataSync.State;
using Microsoft.Extensions.Configuration;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DbDataSync.Cli.Tui.Tabs;

/// <summary>Old step 3. SQLite needs nothing; SQL Server/PostgreSQL reveal the connection string and
/// password fields. The password field is never pre-filled (there is nothing to show — it lives only
/// in <c>SecretStore</c>) and stays blank on Save unless the operator types a new one, matching
/// <see cref="Tui.SetupSteps.ApplyStateDatabase"/>'s "null means keep the current secret" contract.</summary>
internal sealed class StateDatabaseTab : View
{
    private static readonly string[] Engines = [StateEngineIds.Sqlite, StateEngineIds.MsSql, StateEngineIds.Postgres];

    // internal, not private: TabWiringTests focuses this directly to drive real keystrokes through it —
    // SetFocus() on the containing tab view does not reliably delegate to a specific descendant.
    internal readonly OptionSelector _engine = new()
    {
        X = 1, Y = 0,
        Labels = ["SQLite (default — no separate server)", "SQL Server", "PostgreSQL"],
        Value = 0,
    };

    private readonly Label _connectionLabel = new() { X = 1, Y = 4, Text = "Connection string (no password):" };
    // internal, not private: TabWiringTests focuses these directly rather than pressing Tab once per
    // OptionSelector item (it has one tab stop per radio item, standard radio-group navigation) to
    // reach them — simpler than counting presses, not a workaround for anything broken.
    internal readonly TextField _connection = new() { X = 1, Y = 5, Width = Dim.Fill(1) };
    private readonly Label _passwordLabel = new() { X = 1, Y = 6, Text = "Password (blank = keep current):" };
    internal readonly TextField _password = new() { X = 1, Y = 7, Width = Dim.Fill(1), Secret = true };

    public StateDatabaseTab()
    {
        Title = "State DB"; // short — six tabs need to fit an 80-column tab strip
        CanFocus = true;
        Add(_engine, _connectionLabel, _connection, _passwordLabel, _password);
        _engine.ValueChanged += (_, _) => UpdateVisibility();
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        var needsServer = Engines[_engine.Value ?? 0] != StateEngineIds.Sqlite;
        _connectionLabel.Visible = needsServer;
        _connection.Visible = needsServer;
        _passwordLabel.Visible = needsServer;
        _password.Visible = needsServer;
    }

    /// <summary>Phase 109h: async now, the same "installLibrary threaded down from SetupScreen" shape
    /// <see cref="DriversTab.SaveAsync"/> already uses — <see cref="SetupSteps.ApplyStateDatabaseAsync"/>
    /// installs the matching library first when MsSql/Postgres is picked and it isn't installed yet.</summary>
    public Task<SetupSteps.StepResult> SaveAsync(
        string root,
        Func<string, string, IReadOnlyList<PackageRef>, string, string?, CancellationToken, Task<LibraryManifest>> installLibrary)
    {
        var engine = Engines[_engine.Value ?? 0];
        var connectionString = engine == StateEngineIds.Sqlite ? null : _connection.Text;
        var password = engine == StateEngineIds.Sqlite || _password.Text.Length == 0 ? null : _password.Text;
        return SetupSteps.ApplyStateDatabaseAsync(root, engine, connectionString, password, installLibrary);
    }

    public void Populate(IConfiguration configuration)
    {
        var engine = configuration["DbDataSync:StateEngine"];
        var index = Array.IndexOf(Engines, engine);
        if (index >= 0)
            _engine.Value = index;

        var connectionString = configuration["DbDataSync:StateConnectionString"];
        if (!string.IsNullOrEmpty(connectionString))
            _connection.Text = connectionString;

        UpdateVisibility();
    }
}
