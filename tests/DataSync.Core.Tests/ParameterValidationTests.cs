using DataSync.Core.Config;

namespace DataSync.Core.Tests;

/// <summary>
/// What an author declared, checked against what an operator supplied. Server-side because the form's
/// own checks are a courtesy — config also arrives through the API directly and through a git commit
/// somebody made by hand, and "the form would not have let you" is not a property of the data.
/// </summary>
public sealed class ParameterValidationTests
{
    private static ParameterDescriptor Param(
        string name, ParameterType type = ParameterType.Text, bool required = false,
        ParameterCardinality? cardinality = null, List<string>? options = null) =>
        new() { Name = name, Type = type, Required = required, Cardinality = cardinality, DropdownOptions = options };

    private static IReadOnlyList<string> Validate(
        IReadOnlyList<ParameterDescriptor> declared, Dictionary<string, string> values) =>
        ParameterValidation.Validate(declared, values, "Reader 'X'");

    [Fact]
    public void ADeclarationEverybodyHonoured_ReportsNothing()
    {
        var problems = Validate(
            [Param("watermarkColumn", required: true), Param("batchSize", ParameterType.Number)],
            new() { ["watermarkColumn"] = "RowVersion", ["batchSize"] = "500" });

        Assert.Empty(problems);
    }

    [Fact]
    public void ARequiredParameterLeftBlank_IsReported()
    {
        var problems = Validate([Param("watermarkColumn", required: true)], []);

        Assert.Contains(problems, p => p.Contains("watermarkColumn") && p.Contains("required"));
    }

    /// <summary>Whitespace is not a value. A required setting filled with a space is the same mistake
    /// as leaving it empty, and reporting only the second would be a rule that can be walked past.</summary>
    [Fact]
    public void ARequiredParameterFilledWithWhitespace_IsReported()
    {
        var problems = Validate([Param("watermarkColumn", required: true)], new() { ["watermarkColumn"] = "  " });

        Assert.Contains(problems, p => p.Contains("required"));
    }

    [Theory]
    [InlineData(ParameterType.Number, "not-a-number")]
    [InlineData(ParameterType.Bool, "yes")]
    [InlineData(ParameterType.Date, "the 3rd")]
    [InlineData(ParameterType.DateTime, "soon")]
    public void AValueThatIsNotItsDeclaredType_IsReported(ParameterType type, string value)
    {
        var problems = Validate([Param("setting", type)], new() { ["setting"] = value });

        Assert.Contains(problems, p => p.Contains("setting") && p.Contains(value));
    }

    [Fact]
    public void ADropdownValueOutsideItsOptions_IsReportedWithTheOptions()
    {
        var problems = Validate(
            [Param("mode", ParameterType.Dropdown, options: ["fast", "careful"])],
            new() { ["mode"] = "reckless" });

        var problem = Assert.Single(problems);
        Assert.Contains("reckless", problem);
        Assert.Contains("fast, careful", problem);
    }

    /// <summary>
    /// Reported, not rejected as a type error: a driver may read a setting it has not got round to
    /// declaring, and silently dropping something an operator typed would be worse than saying it is
    /// not recognised.
    /// </summary>
    [Fact]
    public void AValueNobodyDeclared_IsReportedAsUnrecognised()
    {
        var problems = Validate([Param("watermarkColumn")], new() { ["waterMarkColum"] = "RowVersion" });

        Assert.Contains(problems, p => p.Contains("waterMarkColum") && p.Contains("not a setting"));
    }

    [Fact]
    public void EveryProblemIsReportedAtOnce_NotJustTheFirst()
    {
        var problems = Validate(
            [Param("a", required: true), Param("b", ParameterType.Number)],
            new() { ["b"] = "no", ["c"] = "1" });

        Assert.Equal(3, problems.Count);
    }

    // ---- Varargs ----

    [Fact]
    public void AVarargsValues_AreFoundByTheirPrefixedKeys()
    {
        var properties = Param("properties", ParameterType.Property, cardinality: ParameterCardinality.Any);

        var problems = Validate([properties], new()
        {
            ["properties.Encrypt"] = "true",
            ["properties.ApplicationName"] = "DataSync",
        });

        Assert.Empty(problems);
    }

    [Fact]
    public void TooFewOrTooManyValues_AreReported()
    {
        var segments = Param("segments", cardinality: new ParameterCardinality(2, 3));

        Assert.Contains(
            Validate([segments], new() { ["segments.0"] = "a" }),
            p => p.Contains("at least 2"));

        Assert.Contains(
            Validate([segments], new()
            {
                ["segments.0"] = "a", ["segments.1"] = "b", ["segments.2"] = "c", ["segments.3"] = "d",
            }),
            p => p.Contains("at most 3"));
    }

    /// <summary>
    /// The guardrail is a ceiling on what a *declaration* can ask for, independent of what it asked
    /// for. A manifest saying it takes a million values is a form that stops responding.
    /// </summary>
    [Fact]
    public void AnAbsurdDeclaredMaximum_IsCappedByTheSystemCeiling()
    {
        var greedy = Param("many", cardinality: new ParameterCardinality(0, 1_000_000));
        var values = Enumerable.Range(0, ParameterCardinality.Ceiling + 1)
            .ToDictionary(i => $"many.{i}", i => i.ToString());

        Assert.Contains(
            Validate([greedy], values),
            p => p.Contains($"at most {ParameterCardinality.Ceiling}"));
    }

    [Fact]
    public void EachOfAVarargsValues_IsTypeCheckedIndividually()
    {
        var ports = Param("ports", ParameterType.Number, cardinality: ParameterCardinality.Any);

        var problems = Validate([ports], new() { ["ports.0"] = "1433", ["ports.1"] = "eleven" });

        // Only the bad one: the good value beside it is not made a problem by its neighbour.
        var problem = Assert.Single(problems);
        Assert.Contains("ports.1", problem);
        Assert.Contains("eleven", problem);
    }

    // ---- Defaults ----

    /// <summary>
    /// A declaration that says only what it is called still renders and still validates: single, text,
    /// optional. That is what makes adopting this additive — every parameter written before it existed
    /// reads back as exactly what it already meant.
    /// </summary>
    [Fact]
    public void ADeclarationThatStatesOnlyItsName_MeansASingleOptionalTextValue()
    {
        var bare = new ParameterDescriptor { Name = "thing" };

        Assert.Equal(ParameterType.Text, bare.Type);
        Assert.False(bare.Required);
        Assert.False(bare.Occurrences.IsVararg);
        Assert.Equal("thing", bare.DisplayLabel);
        Assert.Empty(Validate([bare], []));
        Assert.Empty(Validate([bare], new() { ["thing"] = "anything at all" }));
    }
}
