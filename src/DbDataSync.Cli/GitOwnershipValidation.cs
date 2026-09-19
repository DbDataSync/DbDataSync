namespace DbDataSync.Cli;

/// <summary>
/// Phase 157: a Windows service install (phase 135) transfers ownership of the data directory to the
/// service account, and libgit2 refuses to open a repository it doesn't own — that now also blocks an
/// interactive command run against the same directory afterward, not just the service itself.
/// <para>
/// Disabling libgit2's ownership check is process-wide (it wraps libgit2's own
/// <c>git_libgit2_opts_set_owner_validation</c>, not a per-path allowlist), which is exactly why this
/// only runs in the CLI's own short-lived interactive process and never in the long-running Windows
/// Service host — <see cref="Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.
/// IsWindowsService"/> tells the two apart at runtime, since the service is registered with the
/// identical <c>serve --repo ... --url ...</c> command line a terminal would type
/// (<see cref="ServiceCommand"/>'s own <c>binPath</c>) — there is no command-line flag to key off.
/// </para>
/// </summary>
internal static class GitOwnershipValidation
{
    internal const string WarningMessage =
        "Warning: disabling libgit2's repository-ownership check for this command. Installing " +
        "DbDataSync as a Windows service transfers ownership of the data directory to the service " +
        "account, which otherwise blocks every other command run against the same directory.";

    /// <summary>
    /// No-op outside Windows (the Linux service path already gets ownership right unconditionally via
    /// <c>chown -R</c> — phase 135) and no-op when this process *is* the Windows Service, which keeps
    /// the check on and keeps failing the way phase 135 already made it fail if it somehow isn't the
    /// owner. Every other invocation — <c>config check</c>, <c>setup</c>, a foreground <c>serve</c>, or
    /// anything else run from a terminal — warns and disables the check for this process's lifetime.
    /// </summary>
    public static void DisableIfInteractive(TextWriter? warningWriter = null)
    {
        if (!OperatingSystem.IsWindows())
            return;

        if (Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService())
            return;

        (warningWriter ?? Console.Error).WriteLine(WarningMessage);
        LibGit2Sharp.GlobalSettings.SetOwnerValidation(false);
    }
}
