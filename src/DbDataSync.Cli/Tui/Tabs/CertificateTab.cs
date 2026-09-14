using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DbDataSync.Cli.Tui.Tabs;

/// <summary>
/// Old step 7 — the Windows certificate store, or (phase 113, every other platform) a PEM file, or
/// (phase 130, every other platform too) a self-signed certificate this codebase generates and renews
/// itself. Shown on every platform, unlike <see cref="ServiceTab"/> — Kestrel needs a certificate story
/// everywhere, not just Windows/Linux. Purely informational, same as <see cref="ServiceTab"/>: it
/// computes the exact command to run rather than orchestrating anything.
/// </summary>
internal sealed class CertificateTab : View
{
    private static readonly string[] WindowsChoices = ["self-signed", "enroll", "bind"];
    private static readonly string[] NonWindowsChoices = ["pem", "self-signed"];

    private readonly bool _isWindows = OperatingSystem.IsWindows();
    private readonly CheckBox _enable;
    private readonly OptionSelector? _windowsChoice;
    private readonly OptionSelector? _nonWindowsChoice;
    private readonly Label? _certPathLabel;
    private readonly TextField? _certPath;
    private readonly Label? _keyPathLabel;
    private readonly TextField? _keyPath;

    public CertificateTab()
    {
        Title = "Cert"; // short — six tabs need to fit an 80-column tab strip, "Certificate" alone ate 11 of it
        CanFocus = true;
        _enable = new CheckBox { X = 1, Y = 0, Text = "Set up the TLS certificate?" };
        Add(_enable);

        if (_isWindows)
        {
            _windowsChoice = new OptionSelector
            {
                X = 1, Y = 2,
                Labels = ["Self-signed", "Enroll from an AD CS template", "Bind an existing certificate by thumbprint"],
                Value = 0,
            };
            Add(_windowsChoice);
        }
        else
        {
            _nonWindowsChoice = new OptionSelector
            {
                X = 1, Y = 2,
                Labels = ["Point at a certificate file (PEM), e.g. from certbot", "Generate a self-signed certificate now"],
                Value = 0,
            };
            _certPathLabel = new Label { X = 1, Y = 4, Text = "Certificate file (PEM):" };
            _certPath = new TextField { X = 1, Y = 5, Width = Dim.Fill(1) };
            _keyPathLabel = new Label { X = 1, Y = 6, Text = "Private key file (PEM, unencrypted):" };
            _keyPath = new TextField { X = 1, Y = 7, Width = Dim.Fill(1) };
            Add(_nonWindowsChoice, _certPathLabel, _certPath, _keyPathLabel, _keyPath);

            _nonWindowsChoice.ValueChanged += (_, _) => UpdateVisibility();
        }

        _enable.ValueChanged += (_, _) => UpdateVisibility();
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        var enabled = _enable.Value == CheckState.Checked;
        if (_windowsChoice is not null)
        {
            _windowsChoice.Visible = enabled;
            return;
        }

        _nonWindowsChoice!.Visible = enabled;
        var pemChosen = enabled && (_nonWindowsChoice.Value ?? 0) == 0;
        _certPathLabel!.Visible = pemChosen;
        _certPath!.Visible = pemChosen;
        _keyPathLabel!.Visible = pemChosen;
        _keyPath!.Visible = pemChosen;
    }

    /// <returns>Null when the checkbox isn't checked — nothing to show.</returns>
    public IReadOnlyList<string>? GetInstructions(string root, string? host)
    {
        if (_enable.Value != CheckState.Checked)
            return null;

        if (_isWindows)
            return SetupSteps.WindowsCertificateInstructions(WindowsChoices[_windowsChoice!.Value ?? 0], host);

        return NonWindowsChoices[_nonWindowsChoice!.Value ?? 0] == "self-signed"
            ? SetupSteps.SelfSignedCertificateInstructions(root)
            : SetupSteps.PemCertificateInstructions(root, _certPath!.Text, _keyPath!.Text);
    }
}
