using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>The counterpart of <see cref="WindowsOnlyFactAttribute"/>: skipped *on* Windows, for a test
/// of behaviour that only exists where something Windows-only is missing.</summary>
public sealed class NonWindowsFactAttribute : FactAttribute
{
    public NonWindowsFactAttribute()
    {
        if (OperatingSystem.IsWindows())
            Skip = "Non-Windows only — asserts what happens where Windows-only features are unavailable.";
    }
}
