namespace DbDataSync.Updates.Tests;

public class ReleaseVersionTests
{
    [Theory]
    [InlineData("2026.9.18.1918")]
    [InlineData("2026.9.12.721-beta")]
    [InlineData("2026.9.19.1432-snapshot.g65615e7")]
    [InlineData("2026.09.19.1432-alpha.12")]
    [InlineData("1.0")]
    public void TryParse_AcceptsTheVersionsThisProductProduces(string text)
    {
        Assert.True(ReleaseVersion.TryParse(text, out var version));
        Assert.Equal(text, version.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1")]
    [InlineData("1.2.3.4.5")]
    [InlineData("2026.9.18.1918-")]
    [InlineData("2026.9.18.1918-beta.")]
    [InlineData("2026.9.18.a")]
    [InlineData("2026..18.1918")]
    [InlineData("../../etc/passwd")]
    [InlineData("2026.9.18.1918/../x")]
    [InlineData("2026.9.18.1918-be ta")]
    [InlineData("2026.9.18.1918-beta\\x")]
    [InlineData("99999999999.1")]
    public void TryParse_RejectsAnythingElse_SoAVersionFromTheNetworkCanNeverNameAPath(string text)
    {
        Assert.False(ReleaseVersion.TryParse(text, out _));
    }

    [Fact]
    public void TryParse_DropsBuildMetadata_SoTheReportedVersionOfARunningToolParses()
    {
        Assert.True(ReleaseVersion.TryParse("2026.9.18.1918+3462587066f4d3f3d49b675694f58a1c348bccfd", out var version));
        Assert.Equal("2026.9.18.1918", version.Text);
    }

    [Theory]
    // Numeric parts compare as numbers: 506 is below 1005 even though "1005" sorts first as text.
    [InlineData("2026.9.16.506", "2026.9.16.1005")]
    [InlineData("2026.9.18.1918", "2026.9.19.100")]
    [InlineData("2026.9.30.1", "2026.10.1.1")]
    // A prerelease sorts below the same core's stable version.
    [InlineData("2026.9.12.721-beta", "2026.9.12.721")]
    [InlineData("2026.9.19.1432-snapshot.g65615e7", "2026.9.19.1432")]
    // ...but a later clock time beats an earlier one whatever the labels.
    [InlineData("2026.9.19.1432", "2026.9.19.1615-snapshot.gabc")]
    [InlineData("2026.9.19.1432-snapshot.g65615e7", "2026.9.19.1615-snapshot.g1")]
    // SemVer 2 label precedence.
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-alpha.2", "1.0.0-alpha.11")]
    [InlineData("1.0.0-beta.11", "1.0.0-rc.1")]
    public void CompareTo_OrdersAsNuGetDoes(string lower, string higher)
    {
        var low = ReleaseVersion.Parse(lower);
        var high = ReleaseVersion.Parse(higher);

        Assert.True(low < high);
        Assert.True(high > low);
        Assert.True(low.CompareTo(high) < 0);
        Assert.True(high.CompareTo(low) > 0);
    }

    [Fact]
    public void Equality_IgnoresLeadingZerosAndBuildMetadata()
    {
        var a = ReleaseVersion.Parse("2026.9.1.5");
        var b = ReleaseVersion.Parse("2026.09.01.0005");
        var c = ReleaseVersion.Parse("2026.9.1.5+abcdef");

        Assert.Equal(a, b);
        Assert.Equal(a, c);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.Equal(a.GetHashCode(), c.GetHashCode());
        Assert.NotEqual(a, ReleaseVersion.Parse("2026.9.1.6"));
    }

    [Fact]
    public void Equality_TreatsATrailingZeroPartAsAbsent_MatchingTheOrdering()
    {
        var three = ReleaseVersion.Parse("1.2.3");
        var four = ReleaseVersion.Parse("1.2.3.0");

        Assert.Equal(three, four);
        Assert.Equal(three.GetHashCode(), four.GetHashCode());
    }

    [Theory]
    [InlineData("2026.9.18.1918", ReleaseChannel.Stable)]
    [InlineData("2026.9.12.721-beta", ReleaseChannel.Beta)]
    [InlineData("2026.9.19.1432-snapshot.g65615e7", ReleaseChannel.Snapshot)]
    [InlineData("2026.9.19.1432-snapshot", ReleaseChannel.Snapshot)]
    public void Channel_ReadsTheLabel(string text, ReleaseChannel expected)
    {
        Assert.Equal(expected, ReleaseVersion.Parse(text).Channel);
    }

    [Theory]
    [InlineData("2026.09.19.1432-alpha.1789")]
    [InlineData("2026.9.19.1432-betamax")]
    [InlineData("2026.9.19.1432-snapshots.1")]
    public void Channel_IsNullForALabelThatIsNotOneOfTheProductsChannels(string text)
    {
        Assert.Null(ReleaseVersion.Parse(text).Channel);
    }

    [Fact]
    public void BuiltUtc_IsReadBackOutOfTheVersion()
    {
        Assert.Equal(
            new DateTimeOffset(2026, 9, 19, 14, 32, 0, TimeSpan.Zero),
            ReleaseVersion.Parse("2026.9.19.1432-snapshot.g65615e7").BuiltUtc);
        Assert.Equal(
            new DateTimeOffset(2026, 9, 11, 5, 32, 0, TimeSpan.Zero),
            ReleaseVersion.Parse("2026.9.11.532").BuiltUtc);
    }

    [Theory]
    [InlineData("2026.13.1.100")]
    [InlineData("2026.9.40.100")]
    [InlineData("2026.9.19.2560")]
    [InlineData("1.2.3")]
    public void BuiltUtc_IsNullWhenThePartsAreNotADate(string text)
    {
        Assert.Null(ReleaseVersion.Parse(text).BuiltUtc);
    }
}
