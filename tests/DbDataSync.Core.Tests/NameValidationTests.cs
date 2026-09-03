using DbDataSync.Core.Config;

namespace DbDataSync.Core.Tests;

/// <summary>
/// A name becomes a file or directory name, so what it may contain is a safety question rather than a
/// style one.
/// <para>
/// <c>.</c> is allowed since phase 45, which infers a mapping's name from its source as
/// <c>schema.table</c>. The plan for that phase assumed nothing rejected a dot already; this
/// validation did, and allowing it is what these cover.
/// </para>
/// </summary>
public sealed class NameValidationTests
{
    private static Exception? Validate(string name) =>
        Record.Exception(() => ConfigValidation.ValidateName(name, "Name"));

    [Theory]
    [InlineData("orders")]
    [InlineData("dbo.Orders")]
    [InlineData("sales_2026")]
    [InlineData("cross-instance")]
    [InlineData("Sales.dbo.Orders")]
    public void ANameMadeOfLettersDigitsDashUnderscoreAndDots_IsAccepted(string name) =>
        Assert.Null(Validate(name));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyName_IsRefused(string name) => Assert.NotNull(Validate(name));

    [Theory]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a b")]
    [InlineData("a:b")]
    [InlineData("a*b")]
    public void ANameCarryingAnythingElse_IsRefused(string name) => Assert.NotNull(Validate(name));

    /// <summary>
    /// The reason allowing '.' is not free. A path built from a name that *is* <c>..</c> resolves to
    /// the parent directory, and there is no reason to find out which caller composes paths carelessly.
    /// </summary>
    [Theory]
    [InlineData("..")]
    [InlineData("a..b")]
    [InlineData("....")]
    public void ANameContainingADoubleDot_IsRefused(string name)
    {
        var ex = Validate(name);
        Assert.NotNull(ex);
        Assert.Contains("..", ex!.Message);
    }

    /// <summary>A leading dot is a hidden file on Unix and a trailing one is invalid on Windows.</summary>
    [Theory]
    [InlineData(".orders")]
    [InlineData("orders.")]
    public void ANameStartingOrEndingWithADot_IsRefused(string name)
    {
        var ex = Validate(name);
        Assert.NotNull(ex);
        Assert.Contains("start or end", ex!.Message);
    }
}
