namespace DbDataSync.Api.Auth;

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

    /// <summary>Whether Windows authentication is configured at all. Two groups or one; neither means
    /// this deployment does not use it.</summary>
    public bool WindowsEnabled => !string.IsNullOrWhiteSpace(AdminGroup) || !string.IsNullOrWhiteSpace(ViewerGroup);

    /// <summary>
    /// Runs with **no authentication at all**, for a trusted-network deployment that has deliberately
    /// chosen that.
    /// <para>
    /// It has to be said out loud in configuration. The alternative — starting open when nothing is
    /// configured — is how products get breached, and refusing to start when nothing is configured
    /// would make a freshly installed tool unusable. Phase 53's first-run invite is what removes the
    /// need for this; until then it is the escape hatch, and it names itself.
    /// </para>
    /// </summary>
    public bool Disabled { get; init; }

    public static AuthOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("DbDataSync:Auth");
        return new AuthOptions
        {
            AdminGroup = section["AdminGroup"],
            ViewerGroup = section["ViewerGroup"],
            Disabled = bool.TryParse(section["Disabled"], out var disabled) && disabled,
        };
    }
}
