using Xunit;

namespace DbDataSync.Cli.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that reports itself skipped on any platform but Windows, rather than
/// failing there — same idiom as <c>DbDataSync.Api.Tests</c>' own copy of this attribute. This project's
/// use is <see cref="ServiceCommandTests"/>' real <c>icacls</c> execution (phase 135), which does not
/// exist off Windows.
/// </summary>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows-only — exercises the real icacls.exe, not available on this platform.";
    }
}
