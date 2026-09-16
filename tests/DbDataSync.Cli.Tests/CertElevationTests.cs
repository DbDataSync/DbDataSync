using System.Runtime.Versioning;
using DbDataSync.Certificates;
using DbDataSync.Core.Config;

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


    /// <summary>
    /// Phase 130's tier 2, reached from Windows — the gap
    /// <c>planning/todo/follow-up-phase-140-windows-cannot-reach-the-managed-self-signed-certificate-from-the-cli.md</c>
    /// records. Before <c>--file</c>, <c>CertCommand</c> dispatched on the OS before the subcommand, so
    /// a Windows operator could not produce a managed PFX from the CLI at all even though
    /// <c>DbDataSyncHost</c> would run its renewal service there.
    /// <para>
    /// Deliberately **not** <c>elevatedOverride: false</c>-driven: the point is that this route needs no
    /// elevation, so it is run exactly as an operator would and must succeed on its own.
    /// </para>
    /// </summary>
    [WindowsOnlyFact]
    [SupportedOSPlatform("windows")]
    public void NewSelfSigned_WithFile_TakesTheManagedFileRouteOnWindowsWithoutElevation()
    {
        var root = Directory.CreateTempSubdirectory("dbdatasync-cert-file-route-").FullName;
        try
        {
            ServeCommand.Prepare(root);

            var (exitCode, output) = RunCaptured(
                () => CertCommand.Run(["new-self-signed", "--file", "--days", "30", "--repo", root]));

            Assert.True(
                exitCode == 0,
                $"`new-self-signed --file` exited {exitCode} on Windows. Its output was: {output}");

            var pfx = ManagedSelfSignedCertificate.PfxPath(root);
            Assert.True(File.Exists(pfx), $"Expected a managed PFX at '{pfx}'. The command said: {output}");

            var config = DbDataSyncConfigFile.Read(root);
            Assert.Equal(pfx, config[$"{CertificateBinding.Section}:Path"]);
        }
        finally
        {
            GitTempDirectory.DeleteRecursively(root);
        }
    }

    /// <summary>The default is untouched: no flag on Windows still means the certificate store, which
    /// still needs elevation. Guards against `--file` quietly becoming the default for everyone.</summary>
    [WindowsOnlyFact]
    [SupportedOSPlatform("windows")]
    public void NewSelfSigned_WithNoTargetFlag_StillTakesTheStoreRouteOnWindows()
    {
        var (exitCode, output) = RunCaptured(
            () => CertCommand.Run(["new-self-signed", "--dns", "unused.invalid"], elevatedOverride: false));

        Assert.Equal(1, exitCode);
        Assert.Contains("machine certificate store", output);
    }

    [Fact]
    public void NewSelfSigned_WithBothTargetFlags_RefusesRatherThanPickingOne()
    {
        var (exitCode, output) = RunCaptured(() => CertCommand.Run(["new-self-signed", "--file", "--store"]));

        Assert.Equal(1, exitCode);
        Assert.Contains("mutually exclusive", output);
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
