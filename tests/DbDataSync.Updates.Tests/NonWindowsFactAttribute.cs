using Xunit;

namespace DbDataSync.Updates.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that reports itself skipped on Windows rather than failing there — for a
/// test about POSIX behaviour (symbolic links made without a privilege, file modes, rename over a
/// read-only file) that Windows either cannot express or answers differently. A skip says so in the
/// results; an early <c>return</c> inside the body would report a pass for a test that never ran.
/// </summary>
public sealed class NonWindowsFactAttribute : FactAttribute
{
    public NonWindowsFactAttribute()
    {
        if (OperatingSystem.IsWindows())
            Skip = "Non-Windows only — the behaviour under test is POSIX filesystem semantics.";
    }
}
