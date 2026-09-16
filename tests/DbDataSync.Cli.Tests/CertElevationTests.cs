using System.Runtime.Versioning;

namespace DbDataSync.Cli.Tests;

/// <summary>
/// <c>config cert</c>'s elevation guard. Every subcommand that issues or imports writes to
/// <c>LocalMachine\My</c>, which cannot be opened for write without elevation — and before this guard
/// existed, a non-elevated run died with an unhandled
/// <c>CryptographicException("Access is denied")</c> out of <c>X509Store.Open</c> and exit code 127.
/// <para>
/// Worse than the message: <c>CertificateStore.Install</c> persisted the private key *before* opening
/// the store, and <c>%ProgramData%\Microsoft\Crypto\RSA\MachineKeys</c> is writable unelevated while
/// the store is not — so each attempt left an orphaned key container behind with no certificate
/// referencing it. Found by running the real CLI on a non-elevated Windows shell and then looking for
/// what it left on disk; <c>Install</c> now opens the store first, so a refusal cannot strand key
/// material.
/// </para>
/// </summary>
public sealed class CertElevationTests
{
    [WindowsOnlyFact]
    [SupportedOSPlatform("windows")]
    public void StoreWritingSubcommands_NotElevated_RefuseInsteadOfThrowing()
    {
        foreach (var sub in new[] { "new-self-signed", "enroll", "renew", "retrieve" })
        {
            var (exitCode, output) = RunCaptured(
                () => CertCommand.Run([sub, "--dns", "elevation-test.invalid"], elevatedOverride: false));

            Assert.Equal(1, exitCode);
            Assert.Contains("Needs Administrator", output);
            Assert.Contains(sub, output);
        }
    }

    /// <summary>The read-only side of the same guard: these genuinely work unelevated and must not be
    /// swept up by it. <c>status</c> against a repo that has nothing bound is the cheapest of them that
    /// touches no external state at all.</summary>
    [WindowsOnlyFact]
    [SupportedOSPlatform("windows")]
    public void ReadOnlySubcommands_AreNotRefusedWhenNotElevated()
    {
        var root = Directory.CreateTempSubdirectory("dbdatasync-cert-elevation-tests-").FullName;
        try
        {
            ServeCommand.Prepare(root);

            var (exitCode, output) = RunCaptured(
                () => CertCommand.Run(["status", "--repo", root], elevatedOverride: false));

            Assert.Equal(0, exitCode);
            Assert.DoesNotContain("Needs Administrator", output);
        }
        finally
        {
            GitTempDirectory.DeleteRecursively(root);
        }
    }

    private static (int ExitCode, string Output) RunCaptured(Func<int> action)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        using var output = new StringWriter();
        Console.SetOut(output);
        Console.SetError(output);
        try
        {
            return (action(), output.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }
}
