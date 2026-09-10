using Xunit;

namespace DbDataSync.Libraries.Tests;

/// <summary>Phase 121's "is there an SDK here" check — driven with a real, hand-built directory shaped
/// like a dotnet root, rather than needing two actual dotnet installations (one with the SDK, one
/// without) to prove both branches.</summary>
public sealed class SdkAvailabilityTests
{
    [Fact]
    public void HasSdk_TrueForTheRealSandbox()
    {
        // This sandbox has the SDK (dotnet build/test/publish already depend on it working) — the
        // no-argument overload derives the real dotnet root and should agree.
        Assert.True(SdkAvailability.HasSdk());
    }

    [Fact]
    public void HasSdk_WithAnSdkDirectoryPresentAndNonEmpty_IsTrue()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dbdatasync-dotnetroot-sdk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "sdk", "10.0.100"));
        try
        {
            Assert.True(SdkAvailability.HasSdk(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void HasSdk_WithNoSdkDirectory_IsFalse()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dbdatasync-dotnetroot-runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "shared", "Microsoft.AspNetCore.App", "10.0.0"));
        try
        {
            Assert.False(SdkAvailability.HasSdk(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void HasSdk_WithAnEmptySdkDirectory_IsFalse()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dbdatasync-dotnetroot-emptysdk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "sdk"));
        try
        {
            Assert.False(SdkAvailability.HasSdk(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void HasSdk_ANonexistentRoot_IsFalse()
    {
        Assert.False(SdkAvailability.HasSdk(Path.Combine(Path.GetTempPath(), $"dbdatasync-does-not-exist-{Guid.NewGuid():N}")));
    }
}
