using System.Runtime.Versioning;
using System.Security.Principal;

namespace DbDataSync.Cli;

/// <summary>
/// Whether this process is running as an Administrator.
/// <para>
/// Extracted from <c>RealToolPathEnvironment.IsWindowsAdministrator</c>, which had the only copy, when
/// <c>service install</c> turned out to need the same answer — see <see cref="ServiceCommand"/>'s own
/// guard. Two copies of "am I elevated" is one more than a machine should need, and the tool command's
/// own copy lives behind an interface it fakes in tests, so it could not simply be called from here.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsElevation
{
    internal static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
