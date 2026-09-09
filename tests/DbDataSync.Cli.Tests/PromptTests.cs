namespace DbDataSync.Cli.Tests;

/// <summary>
/// The five prompt shapes <c>dbdatasync setup</c>'s walk-through and review screen need, each driven
/// by a <see cref="ScriptedPromptIo"/> rather than a real console.
/// </summary>
public sealed class PromptTests
{
    [Fact]
    public void Text_BlankAnswer_ReturnsTheDefault()
    {
        var io = new ScriptedPromptIo([""]);
        Assert.Equal("fallback", new Prompt(io).Text("Label", "fallback"));
    }

    [Fact]
    public void Text_NoDefault_RepromptsOnBlankThenAcceptsAValue()
    {
        var io = new ScriptedPromptIo(["", "", "hello"]);
        Assert.Equal("hello", new Prompt(io).Text("Label"));
    }

    [Fact]
    public void Text_NoDefault_ExhaustedInput_ThrowsRatherThanLoopingForever()
    {
        var io = new ScriptedPromptIo([]);
        Assert.Throws<InvalidOperationException>(() => new Prompt(io).Text("Label"));
    }

    [Theory]
    [InlineData("y")]
    [InlineData("Y")]
    [InlineData("yes")]
    public void YesNo_AffirmativeAnswers_ReturnTrue(string answer)
    {
        var io = new ScriptedPromptIo([answer]);
        Assert.True(new Prompt(io).YesNo("Label", @default: false));
    }

    [Fact]
    public void YesNo_BlankAnswer_ReturnsTheDefault()
    {
        var io = new ScriptedPromptIo([""]);
        Assert.True(new Prompt(io).YesNo("Label", @default: true));
    }

    [Fact]
    public void YesNo_InvalidAnswer_RepromptsThenAccepts()
    {
        var io = new ScriptedPromptIo(["maybe", "n"]);
        Assert.False(new Prompt(io).YesNo("Label", @default: true));
    }

    [Fact]
    public void Choice_ByNumber_ReturnsTheMatchingValue()
    {
        var io = new ScriptedPromptIo(["2"]);
        var result = new Prompt(io).Choice(
            "Label", [("a", "Option A"), ("b", "Option B")], "a");
        Assert.Equal("b", result);
    }

    [Fact]
    public void Choice_BlankAnswer_ReturnsTheDefault()
    {
        var io = new ScriptedPromptIo([""]);
        var result = new Prompt(io).Choice(
            "Label", [("a", "Option A"), ("b", "Option B")], "b");
        Assert.Equal("b", result);
    }

    [Fact]
    public void Choice_OutOfRangeThenValid_Reprompts()
    {
        var io = new ScriptedPromptIo(["9", "1"]);
        var result = new Prompt(io).Choice(
            "Label", [("a", "Option A"), ("b", "Option B")], "b");
        Assert.Equal("a", result);
    }

    [Fact]
    public void MultiChoice_SeveralNumbers_ReturnsEachMatchingValue()
    {
        var io = new ScriptedPromptIo(["1, 3"]);
        var result = new Prompt(io).MultiChoice(
            "Label", [("a", "A"), ("b", "B"), ("c", "C")]);
        Assert.Equal(["a", "c"], result);
    }

    [Fact]
    public void MultiChoice_BlankAnswer_SelectsNothing()
    {
        var io = new ScriptedPromptIo([""]);
        var result = new Prompt(io).MultiChoice("Label", [("a", "A"), ("b", "B")]);
        Assert.Empty(result);
    }

    [Fact]
    public void Secret_ReadsCharByCharAndHonoursBackspace()
    {
        var keys = new[]
        {
            ScriptedPromptIo.Char('h'), ScriptedPromptIo.Char('i'), ScriptedPromptIo.Backspace,
            ScriptedPromptIo.Char('e'), ScriptedPromptIo.Char('y'), ScriptedPromptIo.Enter,
        };
        var io = new ScriptedPromptIo([], keys);
        Assert.Equal("hey", new Prompt(io).Secret("Password"));
    }

    [Fact]
    public void HardConfirm_ExactPhrase_ReturnsIt()
    {
        var io = new ScriptedPromptIo(["ALLOW"]);
        Assert.Equal("ALLOW", new Prompt(io).HardConfirm("Label", "ALLOW"));
    }

    [Fact]
    public void HardConfirm_WrongPhrase_ReturnsWhatWasTypedRatherThanTheExpectedOne()
    {
        var io = new ScriptedPromptIo(["nope"]);
        Assert.Equal("nope", new Prompt(io).HardConfirm("Label", "ALLOW"));
    }
}
