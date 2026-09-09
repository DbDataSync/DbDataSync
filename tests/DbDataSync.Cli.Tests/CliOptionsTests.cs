namespace DbDataSync.Cli.Tests;

/// <summary>
/// <see cref="CliOptions.DefaultRoot"/> — machine-wide per platform since phase 112, so an
/// interactive <c>serve</c>/<c>setup</c> and a registered service agree on one repo with no
/// <c>--repo</c> needed on either side.
/// </summary>
public sealed class CliOptionsTests
{
    [Fact]
    public void DefaultRoot_MatchesThePlatformsMachineWideLocation()
    {
        var expected =
            OperatingSystem.IsWindows()
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DbDataSync")
            : OperatingSystem.IsMacOS() ? "/Library/Application Support/DbDataSync"
            : OperatingSystem.IsFreeBSD() ? "/var/db/dbdatasync"
            : "/var/lib/dbdatasync";

        Assert.Equal(expected, CliOptions.DefaultRoot);
    }

    [Fact]
    public void DefaultRoot_IsNotUnderTheUserProfile()
    {
        var perUser = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.False(
            !string.IsNullOrEmpty(perUser)
            && CliOptions.DefaultRoot.StartsWith(perUser, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LegacyDefaultRoot_IsStillThePreviousPerUserLocation()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
            "DbDataSync");

        Assert.Equal(expected, CliOptions.LegacyDefaultRoot);
    }
}
