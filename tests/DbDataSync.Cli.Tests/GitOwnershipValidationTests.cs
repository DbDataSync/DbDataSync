using LibGit2Sharp;
using Xunit;

namespace DbDataSync.Cli.Tests;

/// <summary>
/// Phase 157. <see cref="GlobalSettings.SetOwnerValidation"/> is genuine process-wide libgit2 state,
/// shared with every other test in this same test process — every case here restores whatever value was
/// in effect before it ran, in a <c>finally</c>, so this can never leave a changed global behind for
/// whichever test the runner schedules next.
/// </summary>
public class GitOwnershipValidationTests
{
    [WindowsOnlyFact]
    public void DisableIfInteractive_NotRunningAsWindowsService_WarnsAndDisablesOwnershipValidation()
    {
        // The real, un-mocked case: an ordinary test process is not started by the Service Control
        // Manager, so WindowsServiceHelpers.IsWindowsService() is really false here — this is not a
        // fake standing in for the interactive branch, it *is* the interactive branch.
        var original = GlobalSettings.GetOwnerValidation();
        try
        {
            var writer = new StringWriter();

            GitOwnershipValidation.DisableIfInteractive(writer);

            Assert.Equal(GitOwnershipValidation.WarningMessage, writer.ToString().TrimEnd());
            Assert.False(GlobalSettings.GetOwnerValidation());
        }
        finally
        {
            GlobalSettings.SetOwnerValidation(original);
        }
    }

    [NonWindowsFact]
    public void DisableIfInteractive_OffWindows_DoesNothing()
    {
        var original = GlobalSettings.GetOwnerValidation();
        try
        {
            var writer = new StringWriter();

            GitOwnershipValidation.DisableIfInteractive(writer);

            Assert.Equal("", writer.ToString());
            Assert.Equal(original, GlobalSettings.GetOwnerValidation());
        }
        finally
        {
            GlobalSettings.SetOwnerValidation(original);
        }
    }
}
