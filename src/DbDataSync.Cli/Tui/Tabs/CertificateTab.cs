using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DbDataSync.Cli.Tui.Tabs;

/// <summary>
/// Old step 7 — the Windows certificate store, or (phase 113, every other platform) a PEM file. Shown
/// on every platform, unlike <see cref="ServiceTab"/> — Kestrel needs a certificate story everywhere,
/// not just Windows/Linux. Purely informational, same as <see cref="ServiceTab"/>: it computes the
/// exact command to run rather than orchestrating anything.
/// </summary>
internal sealed class CertificateTab : View
{
    private static readonly string[] WindowsChoices = ["self-signed", "enroll", "bind"];

    private readonly bool _isWindows = OperatingSystem.IsWindows();
    private readonly CheckBox _enable;
    private readonly OptionSelector? _windowsChoice;
    private readonly Label? _certPathLabel;
    private readonly TextField? _certPath;
    private readonly Label? _keyPathLabel;
    private readonly TextField? _keyPath;

    public CertificateTab()
    {
        Title = "Cert"; // short — six tabs need to fit an 80-column tab strip, "Certificate" alone ate 11 of it
        CanFocus = true;
        _enable = new CheckBox
        {
            X = 1, Y = 0,
            Text = _isWindows
                ? "Set up the TLS certificate?"
                : "Point Kestrel at a certificate file (PEM), e.g. from certbot?",
        };
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
            _certPathLabel = new Label { X = 1, Y = 2, Text = "Certificate file (PEM):" };
            _certPath = new TextField { X = 1, Y = 3, Width = Dim.Fill(1) };
            _keyPathLabel = new Label { X = 1, Y = 4, Text = "Private key file (PEM, unencrypted):" };
            _keyPath = new TextField { X = 1, Y = 5, Width = Dim.Fill(1) };
            Add(_certPathLabel, _certPath, _keyPathLabel, _keyPath);
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
        }
        else
        {
            _certPathLabel!.Visible = enabled;
            _certPath!.Visible = enabled;
            _keyPathLabel!.Visible = enabled;
            _keyPath!.Visible = enabled;
        }
    }

    /// <returns>Null when the checkbox isn't checked — nothing to show.</returns>
    public IReadOnlyList<string>? GetInstructions(string root, string? host)
    {
        if (_enable.Value != CheckState.Checked)
            return null;

        return _isWindows
            ? SetupSteps.WindowsCertificateInstructions(WindowsChoices[_windowsChoice!.Value ?? 0], host)
            : SetupSteps.PemCertificateInstructions(root, _certPath!.Text, _keyPath!.Text);
    }
}
