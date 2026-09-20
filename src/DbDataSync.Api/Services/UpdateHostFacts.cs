using DbDataSync.Updates;

namespace DbDataSync.Api.Services;

/// <summary>
/// What the running process knows about how it was installed and started — gathered in one place so tests can
/// describe a different host without being on one.
/// </summary>
/// <param name="RunningVersion">This build's informational version.</param>
/// <param name="RunsUnderSelfUpdateUnit">True when a systemd unit written by this version or later started this
/// process: it sets <c>DBDATASYNC_SELF_UPDATE=1</c> and systemd sets <c>INVOCATION_ID</c>. That is what makes
/// exiting to be updated safe — without the unit's apply step, the service would restart on the same version
/// and the update would never happen.</param>
public sealed record UpdateHostFacts(
    string? RunningVersion,
    InstallLocation Location,
    bool IsWindows,
    bool IsLinux,
    bool RunsUnderSelfUpdateUnit)
{
    public static UpdateHostFacts Current()
    {
        var inContainer = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";
        var globalTools = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools");

        // The entry assembly is the tool itself (DbDataSync.Cli), whose version is what `dbdatasync version`
        // reports; the API assembly's own is the same only when the whole graph was built with one -p:Version.
        var assembly = System.Reflection.Assembly.GetEntryAssembly() ?? typeof(UpdateHostFacts).Assembly;

        return new UpdateHostFacts(
            System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(assembly)?.InformationalVersion,
            InstallLocator.Locate(AppContext.BaseDirectory, globalTools, inContainer),
            OperatingSystem.IsWindows(),
            OperatingSystem.IsLinux(),
            Environment.GetEnvironmentVariable("DBDATASYNC_SELF_UPDATE") == "1"
                && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("INVOCATION_ID")));
    }
}
