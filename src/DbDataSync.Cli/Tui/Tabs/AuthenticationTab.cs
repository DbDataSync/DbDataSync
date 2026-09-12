using Microsoft.Extensions.Configuration;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DbDataSync.Cli.Tui.Tabs;

/// <summary>Old step 5: passkeys (default), Windows groups, or none — guarded by the same
/// type-the-phrase hard confirm before disabling authentication (<see cref="Tui.Dialogs.HardConfirm"/>,
/// shown from <see cref="Save"/> rather than kept as a persistent field, since it only matters at the
/// moment of saving "none").</summary>
internal sealed class AuthenticationTab : View
{
    private static readonly string[] Choices = ["passkeys", "windows", "none"];

    private readonly OptionSelector _method = new()
    {
        X = 1, Y = 0,
        Labels = ["Passkeys (recommended)", "Windows groups", "None — trusted network only"],
        Value = 0,
    };

    private readonly Label _relyingPartyLabel = new() { X = 1, Y = 4, Text = "Relying party id (bare domain):" };
    private readonly TextField _relyingParty = new() { X = 1, Y = 5, Width = Dim.Fill(1) };
    private readonly Label _adminGroupLabel = new() { X = 1, Y = 4, Text = "Admin group:" };
    private readonly TextField _adminGroup = new() { X = 1, Y = 5, Width = Dim.Fill(1) };
    private readonly Label _viewerGroupLabel = new() { X = 1, Y = 6, Text = "Viewer group (blank for none):" };
    private readonly TextField _viewerGroup = new() { X = 1, Y = 7, Width = Dim.Fill(1) };

    public AuthenticationTab()
    {
        Title = "Auth"; // short — six tabs need to fit an 80-column tab strip
        CanFocus = true;
        Add(_method, _relyingPartyLabel, _relyingParty, _adminGroupLabel, _adminGroup, _viewerGroupLabel, _viewerGroup);
        _method.ValueChanged += (_, _) => UpdateVisibility();
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        var choice = Choices[_method.Value ?? 0];
        _relyingPartyLabel.Visible = choice == "passkeys";
        _relyingParty.Visible = choice == "passkeys";
        _adminGroupLabel.Visible = choice == "windows";
        _adminGroup.Visible = choice == "windows";
        _viewerGroupLabel.Visible = choice == "windows";
        _viewerGroup.Visible = choice == "windows";
    }

    /// <param name="host">The General tab's hostname, if any — the relying-party id's default, same as
    /// the console flow's <c>host ?? "localhost"</c>.</param>
    public SetupSteps.StepResult Save(IApplication app, string root, string url, string? host)
    {
        var choice = Choices[_method.Value ?? 0];
        var relyingPartyId = _relyingParty.Text.Length > 0 ? _relyingParty.Text : host ?? "localhost";

        var noAuthConfirmed = choice == "none"
            && Dialogs.HardConfirm(app, "DbDataSync will accept every request with no sign-in.", "ALLOW");

        return SetupSteps.ApplyAuthentication(root, choice, relyingPartyId, url, _adminGroup.Text, _viewerGroup.Text, noAuthConfirmed);
    }

    public void Populate(IConfiguration configuration)
    {
        if (string.Equals(configuration["DbDataSync:Auth:Disabled"], "true", StringComparison.OrdinalIgnoreCase))
        {
            _method.Value = 2;
        }
        else if (!string.IsNullOrEmpty(configuration["DbDataSync:Auth:AdminGroup"]))
        {
            _method.Value = 1;
            _adminGroup.Text = configuration["DbDataSync:Auth:AdminGroup"] ?? "";
            _viewerGroup.Text = configuration["DbDataSync:Auth:ViewerGroup"] ?? "";
        }
        else
        {
            _method.Value = 0;
            _relyingParty.Text = configuration["DbDataSync:Auth:Passkeys:RelyingPartyId"] ?? "";
        }

        UpdateVisibility();
    }
}
