using DbDataSync.Api.Configuration;
using Microsoft.Extensions.Configuration;

namespace DbDataSync.Cli.Tests;

/// <summary>
/// <see cref="ServeCommand.BuildHostArgs"/> — what actually reaches <c>DbDataSyncHost.Build</c>.
/// <para>
/// Two real bugs surfaced while extracting this for phase 164's own regrouping: (1) it still passed
/// the pre-reorg flat <c>--DbDataSync:RepoRoot</c>/<c>--DbDataSync:StateDbPath</c> names after every
/// other key had moved, and (2) stripping a CLI-only flag (<c>--repo</c>/<c>--state-db</c>/<c>--url</c>)
/// without also stripping its separate value token left an orphan that, for an *odd* count of such
/// orphans, silently desyncs .NET's command-line parser for every <c>--DbDataSync:*</c> pair appended
/// after it — dropping App:RepoRoot/State:DbPath entirely for something as ordinary as
/// <c>dbdatasync serve --repo X</c> with no <c>--url</c>. Neither was ever exercised anywhere else:
/// <c>ServeCommand.RunAsync</c> actually starts listening, so nothing calls it in a test, and the
/// Playwright suite launches <c>DbDataSync.Api.dll</c> directly rather than through
/// <c>dbdatasync serve</c>.
/// </para>
/// </summary>
public sealed class ServeCommandHostArgsTests
{
    [Fact]
    public void ResolvesToTheRightApiOptions()
    {
        var hostArgs = ServeCommand.BuildHostArgs(
            args: ["--repo", "/ignored", "--state-db", "/ignored", "--url", "/ignored"],
            root: "/srv/dbdatasync", stateDb: "/srv/dbdatasync/state.db", url: "https://console.example:5080");

        var configuration = new ConfigurationBuilder().AddCommandLine(hostArgs).Build();
        var options = ApiOptions.FromConfiguration(configuration);

        Assert.Equal("/srv/dbdatasync", options.RepoRoot);
        Assert.Equal("/srv/dbdatasync/state.db", options.StateDbPath);
        Assert.Equal("https://console.example:5080", configuration["urls"]);
    }

    [Fact]
    public void StripsTheCliOnlyFlagsAndTheirValues()
    {
        var hostArgs = ServeCommand.BuildHostArgs(
            args: ["--repo", "/ignored", "--state-db", "/ignored", "--url", "/ignored", "--DbDataSync:App:AlternateUrls", "http://alt/"],
            root: "/srv/dbdatasync", stateDb: "/srv/dbdatasync/state.db", url: "http://localhost:5080");

        Assert.DoesNotContain("--repo", hostArgs);
        Assert.DoesNotContain("/ignored", hostArgs);
        Assert.DoesNotContain("--state-db", hostArgs);
        // The literal --url flag is stripped; the resolved --urls (Kestrel's own flag) still appears.
        Assert.DoesNotContain("--url", hostArgs);
        Assert.Contains("--urls", hostArgs);
        Assert.Contains("--DbDataSync:App:AlternateUrls", hostArgs);
    }

    /// <summary>
    /// The exact regression: `--repo X` alone (no `--url`) leaves one CLI-only flag with no partner to
    /// balance the parity that broke <see cref="ResolvesToTheRightApiOptions"/>'s counterpart before the
    /// value-token fix. Every real, ordinary invocation shape is covered, not just the all-three-flags
    /// case above.
    /// </summary>
    [Theory]
    [MemberData(nameof(RealisticInvocations))]
    public void EveryRealisticFlagCombination_StillResolvesTheRepoRootAndStateDbPath(string[] args)
    {
        var hostArgs = ServeCommand.BuildHostArgs(args, root: "/srv/dbdatasync", stateDb: "/srv/dbdatasync/state.db", url: "http://localhost:5080");

        var options = ApiOptions.FromConfiguration(new ConfigurationBuilder().AddCommandLine(hostArgs).Build());

        Assert.Equal("/srv/dbdatasync", options.RepoRoot);
        Assert.Equal("/srv/dbdatasync/state.db", options.StateDbPath);
    }

    public static IEnumerable<object[]> RealisticInvocations()
    {
        yield return [Array.Empty<string>()];
        yield return [new[] { "--repo", "/ignored" }];
        yield return [new[] { "--state-db", "/ignored" }];
        yield return [new[] { "--url", "http://ignored/" }];
        yield return [new[] { "--repo", "/ignored", "--url", "http://ignored/" }];
        yield return [new[] { "--repo", "/ignored", "--state-db", "/ignored" }];
        yield return [new[] { "--repo", "/ignored", "--state-db", "/ignored", "--url", "http://ignored/" }];
    }
}
