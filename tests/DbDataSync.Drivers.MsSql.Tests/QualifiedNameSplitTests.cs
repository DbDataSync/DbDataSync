using DbDataSync.Drivers.MsSql;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.MsSql.Tests;

/// <summary>
/// A hook parameter's value is operator-typed free text, so there is no structured schema-and-table
/// pair to carry instead of a string — which means the quoting rule has to be stated and honoured
/// rather than worked around. Splitting on every dot silently mangled any name containing one.
/// </summary>
public sealed class QualifiedNameSplitTests
{
    private static IReadOnlyList<string> Split(string value) =>
        MsSqlDialect.Instance.SplitQualifiedName(value);

    [Fact]
    public void ABareName_IsOnePart() => Assert.Equal(["LoadControl"], Split("LoadControl"));

    [Fact]
    public void AQualifiedName_IsTwo() => Assert.Equal(["dbo", "LoadControl"], Split("dbo.LoadControl"));

    [Fact]
    public void BracketsAreUnwrapped() => Assert.Equal(["dbo", "LoadControl"], Split("[dbo].[LoadControl]"));

    /// <summary>The case the old split got wrong: a dot inside the quoting belongs to the name.</summary>
    [Fact]
    public void ADotInsideBrackets_StaysPartOfTheName() =>
        Assert.Equal(["dbo", "My.Table"], Split("[dbo].[My.Table]"));

    [Fact]
    public void AQuotedNameWithNoSchema_IsStillOnePart() =>
        Assert.Equal(["My.Table"], Split("[My.Table]"));

    /// <summary>A doubled closing bracket is an escaped one, as it is everywhere else in T-SQL.</summary>
    [Fact]
    public void ADoubledClosingBracket_IsAnEscapedOne() =>
        Assert.Equal(["a]b"], Split("[a]]b]"));

    /// <summary>
    /// Unquoted, this is ambiguous, and it keeps the reading it has always had. There is no cleverness
    /// available that tells it from a table called <c>My.Table</c> — quoting is how the operator says
    /// which one they meant.
    /// </summary>
    [Fact]
    public void AnUnquotedDot_StillMeansSchemaAndTable() =>
        Assert.Equal(["My", "Table"], Split("My.Table"));
}
