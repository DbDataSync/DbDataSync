using Xunit;

namespace DbDataSync.Cli.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that reports itself skipped on any platform but Linux, rather than
/// failing there — the inverse of <see cref="WindowsOnlyFactAttribute"/>, same idiom. Narrower than
/// <see cref="NonWindowsFactAttribute"/>: this is for what genuinely needs *Linux*, not merely "not
/// Windows", so macOS skips too.
/// <para>
/// Two kinds of use, both in <see cref="SystemdServiceTests"/>. The original: the pair of tests driven
/// against the real <c>id</c>/<c>systemctl</c> binaries — <c>RealSystemdEnvironment</c> is the Linux
/// systemd path (phase 111) and neither binary exists on Windows or macOS. Phase 140 added a third that
/// needs no binary at all — <c>Install_ExecutableUnderHomeWithTheHardenedDefaultRoot_RefusesRatherThanRegisteringABrokenUnit</c>,
/// whose refusal fires only when <see cref="CliOptions.DefaultRoot"/> happens to equal systemd's managed
/// state directory, which is true on Linux and on no other platform this tool targets.
/// </para>
/// <para>
/// Added when a Windows CI runner started actually executing this project's suite for real rather than
/// only ever running on Linux; nothing had forced any of these gaps into the open before.
/// </para>
/// </summary>
public sealed class LinuxOnlyFactAttribute : FactAttribute
{
    public LinuxOnlyFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "Linux-only — depends on Linux systemd binaries or on Linux's own default state directory.";
    }
}
