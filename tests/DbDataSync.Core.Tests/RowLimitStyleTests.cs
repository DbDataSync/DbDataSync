using DbDataSync.Core.Sql;

namespace DbDataSync.Core.Tests;

/// <summary>
/// <see cref="SqlDialect.RowLimitStyle"/>/<see cref="SqlDialect.SupportsTieSafeRowLimit"/> are purely
/// declarative for a compiled dialect — they describe what <see cref="SqlDialect.RenderRowLimit"/>/
/// <see cref="SqlDialect.RenderTieSafeRowLimit"/> already render, rather than driving it. These tests
/// pin that the declaration agrees with the rendering, for every dialect that overrides either.
/// </summary>
public sealed class RowLimitStyleTests
{
    [Fact]
    public void BaseDefault_IsOffsetFetch_AndTieSafe() =>
        Assert.Equal((RowLimitStyle.OffsetFetch, true), (PostgresDialect.Instance.RowLimitStyle, PostgresDialect.Instance.SupportsTieSafeRowLimit));

    [Fact]
    public void MsSql_DeclaresTopN_AndStaysTieSafe()
    {
        Assert.Equal(RowLimitStyle.TopN, MsSqlDialect.Instance.RowLimitStyle);
        Assert.True(MsSqlDialect.Instance.SupportsTieSafeRowLimit);

        var (prefix, suffix) = MsSqlDialect.Instance.RenderTieSafeRowLimit("batchSize");
        Assert.Contains("TOP (", prefix);
        Assert.Contains("WITH TIES", prefix);
        Assert.Equal("", suffix);
    }

    [Fact]
    public void MySql_DeclaresLimitOffset_AndNotTieSafe()
    {
        Assert.Equal(RowLimitStyle.LimitOffset, MySqlDialect.Instance.RowLimitStyle);
        Assert.False(MySqlDialect.Instance.SupportsTieSafeRowLimit);

        var (prefix, suffix) = MySqlDialect.Instance.RenderTieSafeRowLimit("batchSize");
        Assert.Equal("", prefix);
        Assert.DoesNotContain("WITH TIES", suffix);
        Assert.Contains("LIMIT", suffix);
    }
}
