using DbDataSync.Core.Config;
using Xunit;

namespace DbDataSync.Core.Tests;

public sealed class HookValidationTests
{
    [Fact]
    public void ABodyReferencingOnlyBuiltInTokensAndParameters_ValidatesCleanly()
    {
        var errors = HookValidation.ValidateBody(
            "INSERT INTO dbo.LoadControl (Mapping, RunId, RowsWritten) VALUES (@mapping, @runId, @rowsWritten);", []);

        Assert.Empty(errors);
    }

    [Fact]
    public void AnUnknownToken_IsAnError()
    {
        var errors = HookValidation.ValidateBody("SELECT * FROM {{nope}};", []);

        Assert.Contains(errors, e => e.Contains("{{nope}}"));
    }

    [Fact]
    public void ADeclaredParameterUsedAsAToken_Validates()
    {
        var errors = HookValidation.ValidateBody("ALTER INDEX ALL ON {{controlTable}} REBUILD;", ["controlTable"]);

        Assert.Empty(errors);
    }

    [Fact]
    public void AnUnknownParameter_IsAnError()
    {
        var errors = HookValidation.ValidateBody("SELECT @nope;", []);

        Assert.Contains(errors, e => e.Contains("@nope"));
    }

    [Theory]
    [InlineData(HookPoints.BeforeStage, "{{staging}}")]
    [InlineData(HookPoints.BeforeStage, "@rowsStaged")]
    [InlineData(HookPoints.BeforeStage, "@rowsWritten")]
    [InlineData(HookPoints.AfterStage, "@rowsWritten")]
    [InlineData(HookPoints.BeforeLoad, "@rowsWritten")]
    public void AReferenceUnavailableAtThatPoint_IsAnError_NamingThePoint(string point, string reference)
    {
        var errors = HookValidation.ValidateAtPoint(point, $"SELECT {reference};", []);

        Assert.Contains(errors, e => e.Contains(reference) && e.Contains(point));
    }

    [Fact]
    public void RowsWritten_IsAvailableOnlyAtAfterLoad()
    {
        Assert.Empty(HookValidation.ValidateAtPoint(HookPoints.AfterLoad, "SELECT @rowsWritten;", []));
    }

    [Fact]
    public void Staging_IsAvailableAtAfterStageBeforeLoadAndAfterLoad()
    {
        Assert.Empty(HookValidation.ValidateAtPoint(HookPoints.AfterStage, "SELECT * FROM {{staging}};", []));
        Assert.Empty(HookValidation.ValidateAtPoint(HookPoints.BeforeLoad, "SELECT * FROM {{staging}};", []));
        Assert.Empty(HookValidation.ValidateAtPoint(HookPoints.AfterLoad, "SELECT * FROM {{staging}};", []));
    }

    [Fact]
    public void FindTokenReferences_ReturnsEveryDistinctName()
    {
        var found = HookValidation.FindTokenReferences("SELECT {{target}}, {{staging}}, {{target}};");

        Assert.Equal(2, found.Count);
        Assert.Contains("target", found);
        Assert.Contains("staging", found);
    }

    [Fact]
    public void FindParameterReferences_ReturnsEveryDistinctName()
    {
        var found = HookValidation.FindParameterReferences("VALUES (@runId, @mapping, @runId);");

        Assert.Equal(2, found.Count);
        Assert.Contains("runId", found);
        Assert.Contains("mapping", found);
    }
}
