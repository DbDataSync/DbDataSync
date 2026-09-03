using ClrKernel.Core.Secrets;

namespace DbDataSync.Cli.Tests;

/// <summary>
/// <c>dbdatasync secret set|list|remove</c> — thin wrappers over <see cref="SecretStore"/>, reachable
/// without going through a connection's own save flow (phase 79). <c>SecretCommand</c> constructs its
/// own <c>new SecretStore("DbDataSync", true)</c> per call rather than taking one as a parameter, the
/// same shape every other CLI command in this file uses for its own dependencies — so these tests
/// exercise it through the real store (env-var-backed in this environment; see
/// <c>EnvironmentSecretProvider</c>), cleaning up what they write rather than injecting a fake.
/// </summary>
public sealed class SecretCommandTests : IDisposable
{
    private readonly string _ref = $"dbdatasync:test:{Guid.NewGuid():N}";

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

    /// <summary>
    /// Phase 93: <c>SecretCommand</c> now constructs <c>new SecretStore("DbDataSync", true)</c>, not the
    /// old unprefixed <c>new SecretStore(true)</c> — proven here by resolving what it wrote through a
    /// freshly-constructed, differently-prefixed store of each: a "DbDataSync"-prefixed store (matching
    /// production) finds it, the package's own unconfigured default ("ClrKernel") does not, since they
    /// are different provider namespaces (different Windows Credential Manager target names / env var
    /// names) even for the exact same secret ref string.
    /// </summary>
    [Fact]
    public void Set_IsResolvableUnderTheDbDataSyncPrefix_ButNotUnderThePackagesUnconfiguredDefault()
    {
        Run(["set", _ref, "sup3r-secret"]);

        var dbDataSyncPrefixed = new SecretStore("DbDataSync", true);
        Assert.Equal("sup3r-secret", dbDataSyncPrefixed.Resolve(_ref));

        var packageDefault = new SecretStore(true);
        Assert.False(packageDefault.TryResolve(_ref, out _));
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
