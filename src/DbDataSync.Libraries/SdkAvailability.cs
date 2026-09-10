using System.Runtime.InteropServices;

namespace DbDataSync.Libraries;

/// <summary>
/// Whether this process can shell out to <c>dotnet publish</c> to restore a package — true on every
/// normal deployment (the CLI is itself a <c>dotnet tool</c>, which needs the SDK to install), false on
/// phase 121's runtime-only container image, which ships only the ASP.NET Core shared runtime.
/// <para>
/// The same walk-up phase 123's <c>SystemdService.ResolveDotnetRoot</c> already uses and verified
/// empirically: <c>RuntimeEnvironment.GetRuntimeDirectory()</c> is
/// <c>&lt;dotnetRoot&gt;/shared/Microsoft.NETCore.App/&lt;version&gt;/</c>, so three <c>Parent</c>
/// steps up is the dotnet root — an <c>sdk/</c> directory there, non-empty, means the SDK is present.
/// </para>
/// </summary>
public static class SdkAvailability
{
    private static readonly Lazy<bool> Cached = new(() => HasSdk(dotnetRootOverride: null));

    /// <summary>The real answer for this process, computed once and cached — the SDK's presence
    /// cannot change during a process's lifetime.</summary>
    public static bool HasSdk() => Cached.Value;

    /// <summary>The pure check, taking the dotnet root directly rather than deriving it — what a test
    /// drives against a real temporary directory shaped like a runtime-only or an SDK install, without
    /// needing two different real dotnet installations to run against.</summary>
    public static bool HasSdk(string? dotnetRootOverride)
    {
        var dotnetRoot = dotnetRootOverride ?? ResolveDotnetRoot();
        if (dotnetRoot is null)
            return false;

        var sdkDir = Path.Combine(dotnetRoot, "sdk");
        return Directory.Exists(sdkDir) && Directory.EnumerateDirectories(sdkDir).Any();
    }

    private static string? ResolveDotnetRoot()
    {
        var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar, '/');
        return Directory.GetParent(runtimeDir)?.Parent?.Parent?.FullName;
    }
}
