using System.Diagnostics;
using System.Runtime.Versioning;

namespace DbDataSync.Cli.Tests;

/// <summary>
/// Phase 136's own "real, not mocked" verification (checkpoint 5): <see cref="WindowsServiceEventLog"/>
/// actually writes an entry a subsequent read-back can see, the same precedent
/// <c>ServiceCommandTests</c>' own real <c>icacls.exe</c> test and <c>AdminCertificateServiceWindowsTests</c>
/// already set for this repo's OS-interaction tests.
/// <para>
/// Calls <see cref="WindowsServiceEventLog.EnsureSourceRegistered"/> directly rather than assuming a
/// real <c>service install</c> already ran — the first call needs to create the event source, which
/// needs elevated rights (see that method's own doc comment). Windows CI runners (GitHub Actions'
/// <c>windows-latest</c> included) run elevated by default; a non-elevated real Windows box running this
/// test for the first time against a never-before-registered source could fail here, which is the same
/// elevation caveat the phase doc's own design section already names for install-time registration.
/// </para>
/// </summary>
public sealed class WindowsServiceEventLogTests
{
    [WindowsOnlyFact]
    [SupportedOSPlatform("windows")]
    public void WriteError_ARealEntryIsReadableBackFromTheApplicationLog()
    {
        WindowsServiceEventLog.EnsureSourceRegistered();

        var marker = $"dbdatasync-test-{Guid.NewGuid():N}";
        WindowsServiceEventLog.WriteError(new InvalidOperationException("boom"), marker);

        Assert.True(
            RecentEntryExists(marker), "the entry just written was not found in the real Application log");
    }

    [WindowsOnlyFact]
    [SupportedOSPlatform("windows")]
    public void WriteInformation_ARealEntryIsReadableBackFromTheApplicationLog()
    {
        WindowsServiceEventLog.EnsureSourceRegistered();

        var marker = $"dbdatasync-test-{Guid.NewGuid():N}";
        WindowsServiceEventLog.WriteInformation(marker);

        Assert.True(
            RecentEntryExists(marker), "the entry just written was not found in the real Application log");
    }

    /// <summary>Registering an already-registered source must not throw — <c>service install</c> has to
    /// tolerate being re-run (upgrade, repair) without erroring on this.</summary>
    [WindowsOnlyFact]
    [SupportedOSPlatform("windows")]
    public void EnsureSourceRegistered_CalledTwice_DoesNotThrow()
    {
        WindowsServiceEventLog.EnsureSourceRegistered();
        WindowsServiceEventLog.EnsureSourceRegistered();
    }

    /// <summary>
    /// Scans backward from the newest entry via the collection's own indexer, not
    /// <c>Cast&lt;EventLogEntry&gt;().Reverse()</c> — the real <c>Application</c> log on a machine that
    /// has been running a while can hold many thousands of entries, and enumerating the whole thing to
    /// find one near the end would be needlessly slow for what this only ever needs the last few dozen
    /// of.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool RecentEntryExists(string marker)
    {
        using var log = new EventLog("Application");
        var count = log.Entries.Count;
        for (var i = count - 1; i >= 0 && i >= count - 50; i--)
        {
            var entry = log.Entries[i];
            if (entry.Source == WindowsServiceEventLog.SourceName && entry.Message.Contains(marker))
                return true;
        }

        return false;
    }
}
