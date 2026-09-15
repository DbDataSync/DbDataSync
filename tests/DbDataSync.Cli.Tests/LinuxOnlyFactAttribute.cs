using Xunit;

namespace DbDataSync.Cli.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that reports itself skipped on any platform but Linux, rather than
/// failing there — the inverse of <see cref="WindowsOnlyFactAttribute"/>, same idiom. This project's
/// use is <see cref="SystemdServiceTests"/>' two tests against the real <c>id</c>/<c>systemctl</c>
/// binaries — <c>RealSystemdEnvironment</c> is the Linux systemd path (phase 111), and neither binary
/// exists on Windows or macOS. Added when a Windows CI runner started actually executing this project's
/// suite for real rather than only ever running on Linux — those two tests were previously never
/// exercised anywhere but Linux, so nothing had forced this gap into the open before.
/// </summary>
public sealed class LinuxOnlyFactAttribute : FactAttribute
{
    public LinuxOnlyFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "Linux-only — exercises the real id/systemctl binaries, not available on this platform.";
    }
}
