using DbDataSync.Libraries;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DbDataSync.Cli.Tui.Tabs;

/// <summary>Old step 4. The three built-ins (SQL Server, PostgreSQL, DuckDB) need no action and are
/// listed only so the operator sees them acknowledged; MySQL/MariaDB is the one with a starter template
/// to drive end to end (phase 109d); "another engine" just points at the manual command, same as the
/// console flow's <c>"other"</c> branch.</summary>
internal sealed class DriversTab : View
{
    private readonly CheckBox _mssql = new() { X = 1, Y = 0, Text = "SQL Server (built in — nothing to do)" };
    private readonly CheckBox _postgres = new() { X = 1, Y = 1, Text = "PostgreSQL (built in — nothing to do)" };
    private readonly CheckBox _duckdb = new() { X = 1, Y = 2, Text = "DuckDB (built in — nothing to do)" };
    private readonly CheckBox _mysql = new() { X = 1, Y = 3, Text = "MySQL / MariaDB" };
    private readonly Label _versionLabel = new() { X = 3, Y = 4, Text = "MySqlConnector version:" };
    private readonly TextField _version = new() { X = 28, Y = 4, Width = 20 };
    private readonly Label _other = new()
    {
        X = 1, Y = 6, Width = Dim.Fill(1),
        Text = "For any other engine, run `dbdatasync config driver install <id> --library <name> " +
               "--version <v> [--from mysql]` once this finishes.",
    };

    public DriversTab()
    {
        Title = "Drivers";
        CanFocus = true;
        Add(_mssql, _postgres, _duckdb, _mysql, _versionLabel, _version, _other);
        _mysql.ValueChanged += (_, _) => UpdateVisibility();
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        var wantsMysql = _mysql.Value == CheckState.Checked;
        _versionLabel.Visible = wantsMysql;
        _version.Visible = wantsMysql;
    }

    /// <returns>Null when MySQL isn't checked — nothing to report, the built-ins need no action and
    /// "other" is purely informational.</returns>
    public Task<SetupSteps.StepResult?> SaveAsync(
        string root,
        Func<string, string, IReadOnlyList<PackageRef>, string, string?, CancellationToken, Task<LibraryManifest>> installLibrary)
    {
        if (_mysql.Value != CheckState.Checked || _version.Text.Length == 0)
            return Task.FromResult<SetupSteps.StepResult?>(null);

        return InstallAsync();

        async Task<SetupSteps.StepResult?> InstallAsync() =>
            await SetupSteps.InstallMySqlDriverAsync(root, _version.Text, installLibrary);
    }
}
