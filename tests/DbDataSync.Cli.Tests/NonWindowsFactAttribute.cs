using Xunit;

namespace DbDataSync.Cli.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that reports itself skipped on Windows rather than failing there —
/// for a test whose subject is a code path <c>OperatingSystem.IsWindows()</c> genuinely branches
/// *away* from, so there is nothing on Windows for it to be right or wrong about.
/// <para>
/// Distinct from <see cref="LinuxOnlyFactAttribute"/>, which is narrower: that one is for tests that
/// need real Linux *binaries* (<c>id</c>, <c>systemctl</c>) and so cannot run on macOS either. The
/// paths this one guards — <c>ToolCommand</c>'s POSIX install and <c>CertCommand</c>'s file-based
/// <c>new-self-signed</c> — are the non-Windows branch of a two-branch switch, and macOS takes them
/// exactly as Linux does. Gating those with <c>[LinuxOnlyFact]</c> would skip them on a platform that
/// really does run them.
/// </para>
/// <para>
/// Added in phase 140, when a real <c>windows-latest</c> CI job first executed this project's suite and
/// every such test failed on a branch it was never on.
/// </para>
/// </summary>
public sealed class NonWindowsFactAttribute : FactAttribute
{
    public NonWindowsFactAttribute()
    {
        if (OperatingSystem.IsWindows())
            Skip = "Non-Windows only — exercises the branch OperatingSystem.IsWindows() selects away from.";
    }
}
