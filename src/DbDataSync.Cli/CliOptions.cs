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
