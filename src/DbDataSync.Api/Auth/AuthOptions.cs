namespace DbDataSync.Api.Auth;

/// <summary>A recurring two-state config value — never a bare boolean in the file (phase 164): a mode
/// name self-documents in <c>config get</c>/the Admin screen the way <c>true</c>/<c>false</c> never did,
/// and leaves room for a feature to grow a third state without another schema change.</summary>
public enum FeatureMode { Disabled, Enabled }

/// <summary>Who an unauthenticated request from loopback is trusted as, if anyone — phase 164's
/// replacement for the old blanket <c>Auth:Disabled</c>. Deliberately has no "trust from anywhere" value:
/// unlike <see cref="ViewerNetworkTrust"/>, granting Admin for free is never offered wider than
/// loopback.</summary>
public enum AdminNetworkTrust { Disabled, Loopback }

/// <summary>Who an unauthenticated request is trusted as for read-only access — wider than
/// <see cref="AdminNetworkTrust"/> on purpose, since a Viewer can only look.</summary>
public enum ViewerNetworkTrust { Disabled, Loopback, Remote }

/// <summary>
/// How this deployment decides who may use it, read from the <c>DbDataSync:Auth</c> configuration
/// section.
/// </summary>
public sealed class AuthOptions
{
    public const string SessionCookie = "dbdatasync.session";

    /// <summary>
    /// Windows groups whose members are admins, and whose members are viewers.
    /// <para>
    /// **Two groups rather than one group and a per-user role**, because a Windows shop already
    /// manages membership in a directory and asking them to manage DbDataSync's roles inside DbDataSync
    /// is asking them to keep two lists in step. Somebody in both is an admin — the more permissive
    /// answer, which is the one that matches what "you put me in the admin group" means.
    /// </para>
    /// </summary>
    public string? AdminGroup { get; init; }

    public string? ViewerGroup { get; init; }

    /// <summary>Explicit — lets an operator configure a group name and still turn Windows auth off
    /// without clearing it. Defaults to <see cref="FeatureMode.Enabled"/>, so an existing deployment
    /// that only ever configured group names keeps working exactly as before.</summary>
    public FeatureMode WindowsMode { get; init; } = FeatureMode.Enabled;

    /// <summary>Whether Windows authentication is actually live: the mode allows it, and at least one
    /// group is configured. Neither means this deployment does not use it.</summary>
    public bool WindowsEnabled =>
        WindowsMode == FeatureMode.Enabled && (!string.IsNullOrWhiteSpace(AdminGroup) || !string.IsNullOrWhiteSpace(ViewerGroup));

    /// <summary>
    /// Trusts an unauthenticated request from loopback as Admin — phase 164's narrower replacement for
    /// the old <c>Auth:Disabled</c>, which trusted every request, from anywhere, as Admin. There is no
    /// "from anywhere" option for Admin at all; only <see cref="NetworkViewer"/> ever widens past
    /// loopback.
    /// </summary>
    public AdminNetworkTrust NetworkAdmin { get; init; } = AdminNetworkTrust.Disabled;

    /// <summary>Trusts an unauthenticated request as Viewer — from loopback, or (widest) from
    /// anywhere.</summary>
    public ViewerNetworkTrust NetworkViewer { get; init; } = ViewerNetworkTrust.Disabled;

    public static AuthOptions FromConfiguration(IConfiguration configuration)
    {
        var windows = configuration.GetSection("DbDataSync:Auth:Windows");
        var network = configuration.GetSection("DbDataSync:Auth:Network");

        return new AuthOptions
        {
            AdminGroup = windows["AdminGroup"],
            ViewerGroup = windows["ViewerGroup"],
            WindowsMode = ConfigEnum.Parse(windows["Mode"], FeatureMode.Enabled),
            NetworkAdmin = ConfigEnum.Parse(network["Admin"], AdminNetworkTrust.Disabled),
            NetworkViewer = ConfigEnum.Parse(network["Viewer"], ViewerNetworkTrust.Disabled),
        };
    }
}

/// <summary>Shared by every mode-string-backed setting (phase 164 replaced every bare boolean with one
/// of these), so <c>enabled</c>/<c>disabled</c>/etc. parse identically everywhere rather than each
/// options class rolling its own.</summary>
internal static class ConfigEnum
{
    public static TEnum Parse<TEnum>(string? value, TEnum fallback) where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) ? parsed : fallback;
}
