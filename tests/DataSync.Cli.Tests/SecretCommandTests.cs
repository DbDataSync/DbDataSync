using ClrKernel.Core.Secrets;

namespace DataSync.Cli.Tests;

/// <summary>
/// <c>datasync secret set|list|remove</c> — thin wrappers over <see cref="SecretStore"/>, reachable
/// without going through a connection's own save flow (phase 79). <c>SecretCommand</c> constructs its
/// own <c>new SecretStore(true)</c> per call rather than taking one as a parameter, the same shape
/// every other CLI command in this file uses for its own dependencies — so these tests exercise it
/// through the real store (env-var-backed in this environment; see <c>EnvironmentSecretProvider</c>),
/// cleaning up what they write rather than injecting a fake.
/// </summary>
public sealed class SecretCommandTests : IDisposable
{
    private readonly string _ref = $"datasync:test:{Guid.NewGuid():N}";

    public void Dispose() => SecretCommand.Run(["remove", _ref]);

    [Fact]
    public void SetThenList_ReportsItAsSet_WithoutPrintingTheValue()
    {
        var (setCode, _) = Run(["set", _ref, "sup3r-secret"]);
        Assert.Equal(0, setCode);

        var (listCode, output) = Run(["list", _ref]);

        Assert.Equal(0, listCode);
        Assert.Contains(_ref, output);
        Assert.Contains("set", output);
        Assert.DoesNotContain("sup3r-secret", output);
    }

    [Fact]
    public void List_ARefNeverSet_ReportsNotSet()
    {
        var (code, output) = Run(["list", _ref]);

        Assert.Equal(0, code);
        Assert.Contains("not set", output);
    }

    [Fact]
    public void SetThenRemove_ThenList_ReportsNotSet()
    {
        Run(["set", _ref, "sup3r-secret"]);
        var (removeCode, _) = Run(["remove", _ref]);
        Assert.Equal(0, removeCode);

        var (_, output) = Run(["list", _ref]);
        Assert.Contains("not set", output);
    }

    [Fact]
    public void Set_WrongArgumentCount_FailsWithoutStoringAnything()
    {
        var (code, _) = Run(["set", _ref]);

        Assert.NotEqual(0, code);
        var (_, output) = Run(["list", _ref]);
        Assert.Contains("not set", output);
    }

    private static (int ExitCode, string Output) Run(string[] args)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        using var output = new StringWriter();
        Console.SetOut(output);
        Console.SetError(output);
        try
        {
            var exitCode = SecretCommand.Run(args);
            return (exitCode, output.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }
}
