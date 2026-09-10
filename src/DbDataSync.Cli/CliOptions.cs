namespace DbDataSync.Cli;

/// <summary>
/// Where a tool that was just installed keeps its things.
/// <para>
/// Not the current directory, which is what the API's own defaults use: `dbdatasync serve` run from
/// wherever a shell happens to be would scatter a config repo and a state database across a user's
/// filesystem, and the second run would find neither.
/// </para>
/// <para>
/// **Machine-wide, not per-user** (phase 112) — the interactive tool and a registered service must
/// agree on one repo with no <c>--repo</c> needed on either side. Before this phase the default was
/// <see cref="Environment.SpecialFolder.LocalApplicationData"/>, which put the Windows service
/// (running as <c>LocalSystem</c>, whose <c>%LOCALAPPDATA%</c> is a profile nobody else ever sees) on
/// a different repo than an interactive <c>serve</c> run by a person. <see cref="LegacyDefaultRoot"/>
/// is that old per-user path, kept only so an upgrade can detect and point at it rather than silently
/// stranding it.
/// </para>
/// </summary>
public static class CliOptions
{
    /// <summary>
    /// One documented, machine-wide directory per platform: <c>%ProgramData%\DbDataSync</c> on
    /// Windows (<c>CommonApplicationData</c>, not hard-coded, so a redirected <c>ProgramData</c> is
    /// honoured — the all-users store, <c>LocalSystem</c>'s analog of <c>/var/lib</c>); macOS's
    /// <c>/Library/Application Support</c> (hard-coded — <c>CommonApplicationData</c> maps to
    /// <c>/usr/share</c> on macOS, which is wrong for this); FreeBSD's <c>/var/db</c> per
    /// <c>hier(7)</c> (<c>/var/lib</c> does not exist on a stock FreeBSD); Linux's FHS
    /// <c>/var/lib/&lt;pkg&gt;</c>, which every other Unix-like OS (Solaris/illumos, NetBSD, OpenBSD —
    /// none of which .NET has an <c>OperatingSystem.IsX()</c> for) also falls through to as the
    /// least-surprising generic answer; an operator on one of those sets
    /// <c>DbDataSync__RepoRoot</c> instead if <c>/var/lib</c> is not idiomatic there.
    /// </summary>
    public static string DefaultRoot =>
        OperatingSystem.IsWindows()
            ? Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolderOption.Create),
                "DbDataSync")
            : OperatingSystem.IsMacOS() ? "/Library/Application Support/DbDataSync"
            : OperatingSystem.IsFreeBSD() ? "/var/db/dbdatasync"
            : "/var/lib/dbdatasync";

    /// <summary>The per-user default this tool used before phase 112 — still checked so an upgrade
    /// does not silently strand a working install; see <see cref="LegacyRootMigration"/>.</summary>
    public static string LegacyDefaultRoot =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.Create),
            "DbDataSync");

    /// <summary>
    /// One documented, machine-wide directory per platform for the *binary* — <see cref="DefaultRoot"/>
    /// is this phase's analog for the *data* (phase 112). `/opt`/`%ProgramFiles%` for a binary,
    /// `/var/lib`/`%ProgramData%` for data is the idiomatic split on each platform this project targets.
    /// Not read by <c>serve</c>/<c>service</c> — those still resolve the running executable from
    /// <see cref="Environment.ProcessPath"/>; this is only where <c>tool install</c> (phase 123) expects
    /// to find it by default, and what the docs point a machine-wide install at.
    /// </summary>
    public static string DefaultToolDir =>
        OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DbDataSync")
            : OperatingSystem.IsMacOS() ? "/usr/local/dbdatasync"
            : "/opt/dbdatasync";

    /// <summary>
    /// Whether <paramref name="path"/> sits under the current user's own profile — the wrong place
    /// for a service or a machine-wide install to point at, because it stops working the moment that
    /// profile is cleaned up or the tool is re-registered from a different account. Checked against
    /// <see cref="Environment.SpecialFolder.UserProfile"/> on Windows and <c>$HOME</c> elsewhere, plus
    /// a literal <c>/home/</c>/<c>/root</c> fallback for the case a service account's own <c>$HOME</c>
    /// differs from the profile the tool actually lives under.
    /// </summary>
    public static bool IsUnderUserProfile(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var profile = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : Environment.GetEnvironmentVariable("HOME");

        if (!string.IsNullOrEmpty(profile))
        {
            var profileFull = Path.GetFullPath(profile).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (full.StartsWith(profileFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(full, profileFull, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (OperatingSystem.IsWindows())
            return false;

        return full.StartsWith("/home/", StringComparison.Ordinal) || full.StartsWith("/root/", StringComparison.Ordinal)
            || string.Equals(full, "/root", StringComparison.Ordinal);
    }

    /// <summary>Reads <c>--name value</c> from an argument list, or null.</summary>
    public static string? Read(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }

    public static bool Has(string[] args, string name) =>
        args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
}
