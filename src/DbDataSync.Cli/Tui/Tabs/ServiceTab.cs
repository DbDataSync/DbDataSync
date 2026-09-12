using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DbDataSync.Cli.Tui.Tabs;

/// <summary>
/// Old step 6 — Windows (<c>sc.exe</c>) or Linux (systemd, phase 111) only; <see cref="Tui.SetupScreen"/>
/// skips adding this tab at all on macOS, same as the console flow's platform branch. Neither
/// registration is orchestrated here — both need real elevation this process cannot assume it has — so
/// <see cref="GetInstructions"/> only computes the exact command to run.
/// </summary>
internal sealed class ServiceTab : View
{
    private readonly bool _isWindows = OperatingSystem.IsWindows();
    private readonly CheckBox _register;
    private readonly Label _accountLabel;
    private readonly TextField _account;

    public ServiceTab()
    {
        Title = "Service";
        CanFocus = true;
        _register = new CheckBox
        {
            X = 1, Y = 0,
            Text = _isWindows ? "Register dbdatasync as a Windows service?" : "Register dbdatasync as a systemd service?",
        };
        _accountLabel = new Label { X = 1, Y = 2, Text = _isWindows ? "Service account:" : "Service user:" };
        _account = new TextField { X = 18, Y = 2, Width = 30, Text = _isWindows ? "LocalSystem" : "dbdatasync" };

        Add(_register, _accountLabel, _account);
        _register.ValueChanged += (_, _) => UpdateVisibility();
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        var registering = _register.Value == CheckState.Checked;
        _accountLabel.Visible = registering;
        _account.Visible = registering;
    }

    /// <returns>Null when the checkbox isn't checked — nothing to show.</returns>
    public IReadOnlyList<string>? GetInstructions(string root, string url) =>
        _register.Value != CheckState.Checked
            ? null
            : _isWindows
                ? SetupSteps.WindowsServiceInstallInstructions(root, url, _account.Text)
                : SetupSteps.SystemdServiceInstallInstructions(root, url, _account.Text);
}
