namespace DbDataSync.Cli.Tests;

/// <summary>
/// <c>dbdatasync setup</c> is a Terminal.Gui TUI now (phase 128) — everything section-shaped
/// (config/secret/git writes) is covered UI-free by <c>SetupStepsTests</c>, and the real widget wiring
/// by <c>Tui.TabWiringTests</c>. This covers the one thing left in <see cref="SetupCommand"/> itself:
/// the no-terminal gate, which needs no fake at all — a test host's own stdio is already redirected, so
/// calling the real method exercises the real gate.
/// </summary>
public sealed class SetupCommandTests
{
    [Fact]
    public async Task NonInteractive_RefusesAndPointsAtConfigCheck()
    {
        var originalError = Console.Error;
        var error = new StringWriter();
        Console.SetError(error);
        int exitCode;
        try
        {
            exitCode = await SetupCommand.RunAsync(["--repo", "unused"], FailingInstallLibrary);
        }
        finally
        {
            Console.SetError(originalError);
        }

        Assert.Equal(1, exitCode);
        Assert.Contains("dbdatasync config check", error.ToString());
    }

    private static Task<DbDataSync.Libraries.LibraryManifest> FailingInstallLibrary(
        string repoRoot, string id, IReadOnlyList<DbDataSync.Libraries.PackageRef> packages, string factoryType,
        string? source, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("This test's gate should return before ever installing a library.");
}
