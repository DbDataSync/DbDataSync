using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that reports itself skipped on any platform but Windows, rather than
/// failing there.
/// <para>
/// Every test in this project's <c>*WindowsTests</c> files calls real Windows APIs — the certificate
/// store, DPAPI-backed key storage — that simply don't exist elsewhere. <c>Skip</c> is evaluated once,
/// in the constructor, since that's the only place a `[Fact]`'s properties are computed; setting it
/// conditionally here is the standard xUnit v2 idiom for a runtime-decided skip; xUnit has no built-in
/// platform trait to do this instead.
/// </para>
/// </summary>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows-only — exercises the real certificate store/DPAPI, not available on this platform.";
    }
}
