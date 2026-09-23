using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using Xunit.Abstractions;

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
/// test for the first time against a never-before-registered source fails here with a
/// <c>SecurityException</c> out of <c>EventLog.SourceExists</c> — the same elevation limit that used to
/// crash <c>service install</c> itself, until <see cref="ServiceCommand"/> grew a guard for it.
/// </para>
/// <para>
/// **These tests report what they saw, not just whether they were happy.** They previously asserted a
/// bare boolean with a fixed sentence — "the entry just written was not found in the real Application
/// log" — which is unusable twice over: a failure said nothing about what *was* in the log (was the
/// source wrong? was nothing written at all? did something else eat it?), and a pass proved only that
/// some entry matched a GUID, never showing the text it matched. That is what left this repo unable to
/// state, after a green CI run, that anyone had ever read the Event Log output these tests exist to
/// produce. Every assertion below now carries the observed state in its own failure message, and the
/// entries scanned go to <see cref="ITestOutputHelper"/> — which VSTest includes in the console output
/// of a *failing* test at ordinary verbosity, and of a passing one under
/// <c>--logger "console;verbosity=detailed"</c>.
/// </para>
/// </summary>
public sealed class WindowsServiceEventLogTests(ITestOutputHelper output)
{
    /// <summary>How far back to scan. The real Application log on a long-lived machine holds many
    /// thousands of entries and this only ever needs the newest few; wide enough that a parallel test
    /// writing its own entry in between cannot push ours out of range.</summary>
    private const int ScanDepth = 50;

    [WindowsOnlyFact]
    [SupportedOSPlatform("windows")]
    public void WriteError_ARealEntryIsReadableBackFromTheApplicationLog()
    {
        EnsureSourceRegisteredOrExplain();

        var marker = $"dbdatasync-test-{Guid.NewGuid():N}";
        WriteOrExplain(() => WindowsServiceEventLog.WriteError(new InvalidOperationException("boom"), marker));

        AssertWrittenBack(marker, EventLogEntryType.Error);
    }

    [WindowsOnlyFact]
    [SupportedOSPlatform("windows")]
    public void WriteInformation_ARealEntryIsReadableBackFromTheApplicationLog()
    {
        EnsureSourceRegisteredOrExplain();

        var marker = $"dbdatasync-test-{Guid.NewGuid():N}";
        WriteOrExplain(() => WindowsServiceEventLog.WriteInformation(marker));

        AssertWrittenBack(marker, EventLogEntryType.Information);
    }

    /// <summary>
    /// Registering an already-registered source must not throw — <c>service install</c> has to tolerate
    /// being re-run (upgrade, repair) without erroring on this.
    /// <para>
    /// It also has to leave the source actually registered, which "did not throw" never checked: a
    /// silently-no-op'd registration would have passed this test and then failed every write for real.
    /// The resolved log name is asserted too, since a source bound to the wrong log is exactly as
    /// broken as one that does not exist, and reported either way.
    /// </para>
    /// </summary>
    [WindowsOnlyFact]
    [SupportedOSPlatform("windows")]
    public void EnsureSourceRegistered_CalledTwice_LeavesTheSourceBoundToTheApplicationLog()
    {
        EnsureSourceRegisteredOrExplain();
        EnsureSourceRegisteredOrExplain();

        var source = WindowsServiceEventLog.SourceName;
        var exists = EventLog.SourceExists(source);
        var logName = exists ? EventLog.LogNameFromSourceName(source, ".") : "(source does not exist)";

        output.WriteLine($"After two EnsureSourceRegistered() calls: source '{source}' exists={exists}, log='{logName}'.");

        Assert.True(
            exists,
            $"EventLog.SourceExists(\"{source}\") is false after two EnsureSourceRegistered() calls — " +
            "registration silently did nothing rather than throwing.");
        Assert.Equal("Application", logName);
    }

    /// <summary>
    /// <see cref="WindowsServiceEventLog.EnsureSourceRegistered"/>, with the one failure that is not a
    /// product bug explained where it happens. <c>EventLog.SourceExists</c> has to search every log,
    /// including <c>Security</c>, so it throws <see cref="SecurityException"/> outright when this
    /// process is not elevated — and a raw stack trace out of <c>FindSourceRegistration</c> says
    /// nothing about elevation being the reason. CI's <c>windows-latest</c> runners are elevated, so
    /// this path is only ever taken on a developer's own machine, which is exactly where the
    /// explanation is worth having.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void EnsureSourceRegisteredOrExplain()
    {
        try
        {
            WindowsServiceEventLog.EnsureSourceRegistered();
        }
        catch (SecurityException ex)
        {
            Assert.Fail(
                $"Could not register or check the Event Log source '{WindowsServiceEventLog.SourceName}'. "
                + $"This process is {(WindowsElevation.IsAdministrator() ? "elevated" : "NOT elevated")}, and "
                + "EventLog.SourceExists must search every log (including Security) to answer, which requires "
                + "Administrator. Run this suite from an elevated prompt to exercise these tests. "
                + $"The underlying error was: {ex.Message}");
        }
    }

    /// <summary>
    /// <see cref="WindowsServiceEventLog.WriteError"/>/<see cref="WindowsServiceEventLog.WriteInformation"/>,
    /// with the write's own access denial explained where it happens — the counterpart to
    /// <see cref="EnsureSourceRegisteredOrExplain"/>, which only ever guarded registration. The observed
    /// failure ("Cannot open log for source 'DbDataSync'.  — Access is denied") is not a
    /// <see cref="SecurityException"/>, so it used to sail past that guard and surface as a bare assertion
    /// failure with none of the elevation context the guard exists to supply. See
    /// architecture/planning/todo/follow-up-event-log-tests-guard-registration-but-not-the-write.md.
    /// <para>
    /// **Retries on that same access denial, briefly — the real cause, confirmed, not guessed.** CI run
    /// `35812820281` printed this method's own "This process is elevated" from a live failure: `runneradmin`
    /// stayed elevated the whole time, which rules out the follow-up doc's second candidate (a genuine
    /// privilege gap between the registration and write paths) and leaves its first — registering a
    /// brand-new source and being able to open it for writing are not the same instant, and .NET's
    /// <see cref="EventLog"/> has no synchronous "wait until the write path is ready" call to block on
    /// instead (<see cref="EventLog.SourceExists"/>, already checked by
    /// <see cref="EnsureSourceRegisteredOrExplain"/>, only confirms the registry side — it was already
    /// true in the run that still failed here). Retrying the one thing that's genuinely still uncertain
    /// (does *this* write succeed yet) is a bounded wait on a real condition, the same shape this repo's
    /// own <c>UntilAsync</c>/<c>ScanUntilPastAsync</c> helpers already use elsewhere for a real service's
    /// own eventual consistency — not a fixed sleep guessing a duration. A failure that outlasts the
    /// retry window is left to report exactly as before: elevation context and all, because at that point
    /// it is no longer a propagation delay, it is a real problem.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void WriteOrExplain(Action write)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            try
            {
                write();
                return;
            }
            catch (Exception ex) when (IsAccessDenied(ex))
            {
                if (DateTime.UtcNow >= deadline)
                {
                    Assert.Fail(
                        $"Could not write to the Event Log under source '{WindowsServiceEventLog.SourceName}', " +
                        "even after retrying for 5s. "
                        + $"This process is {(WindowsElevation.IsAdministrator() ? "elevated" : "NOT elevated")} — a "
                        + "source that was just registered can still deny the first write until the Event Log service "
                        + "has picked up the registration, and the write path is not guaranteed to run with the same "
                        + "privileges the registration path had either way. Run this suite from an elevated prompt to "
                        + $"exercise these tests. The underlying error was: {ex.Message}");
                }
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>Whether an exception out of the real Event Log write path is a permissions denial rather
    /// than a genuine defect in what was written — registration fails this way with a
    /// <see cref="SecurityException"/>, but the write path denies access as a bare message instead, so
    /// both are checked.</summary>
    private static bool IsAccessDenied(Exception ex) =>
        ex is SecurityException
        || ex.Message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("Cannot open log for source", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Finds the entry just written and asserts its type, reporting everything it looked at either way.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private void AssertWrittenBack(string marker, EventLogEntryType expectedType)
    {
        var source = WindowsServiceEventLog.SourceName;
        var scanned = RecentEntries();
        var ours = scanned.Where(e => e.Source == source).ToList();
        var match = ours.FirstOrDefault(e => e.Message.Contains(marker, StringComparison.Ordinal));

        output.WriteLine(
            $"Scanned the newest {scanned.Count} 'Application' entries for source '{source}' / marker '{marker}'. " +
            $"{ours.Count} were from this source:");
        foreach (var entry in ours)
            output.WriteLine($"  [{entry.TimeWritten:O}] {entry.EntryType}: {FirstLine(entry.Message)}");

        Assert.True(
            match is not null,
            $"No entry from source '{source}' containing marker '{marker}' in the newest {scanned.Count} " +
            $"'Application' entries. From that source there were {ours.Count} entries: " +
            (ours.Count == 0
                ? "none at all — the write did not reach the log, or landed under a different source."
                : string.Join(" | ", ours.Select(e => $"[{e.TimeWritten:O}] {e.EntryType}: {FirstLine(e.Message)}"))));

        output.WriteLine("Matched entry, verbatim:");
        output.WriteLine(match!.Message);

        Assert.True(
            match.EntryType == expectedType,
            $"The entry for marker '{marker}' was written as {match.EntryType}, expected {expectedType}. " +
            $"Its message was: {match.Message}");
    }

    /// <summary>
    /// Scans backward from the newest entry via the collection's own indexer, not
    /// <c>Cast&lt;EventLogEntry&gt;().Reverse()</c> — the real <c>Application</c> log on a machine that
    /// has been running a while can hold many thousands of entries, and enumerating the whole thing to
    /// find one near the end would be needlessly slow for what this only ever needs the last few dozen
    /// of. Returns a snapshot rather than live <c>EventLogEntry</c> objects so the values survive the
    /// <c>EventLog</c> being disposed, and can be quoted in an assertion message.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static List<LoggedEntry> RecentEntries()
    {
        using var log = new EventLog("Application");
        var count = log.Entries.Count;
        var entries = new List<LoggedEntry>();

        for (var i = count - 1; i >= 0 && i >= count - ScanDepth; i--)
        {
            var entry = log.Entries[i];
            entries.Add(new LoggedEntry(entry.Source, entry.EntryType, entry.TimeWritten, entry.Message));
        }

        return entries;
    }

    /// <summary>One line, capped — a real Event Log message can run to many lines, and a failure
    /// message listing fifty of them in full is its own kind of unreadable.</summary>
    private static string FirstLine(string message)
    {
        var line = message.Split('\n', 2)[0].TrimEnd('\r');
        return line.Length <= 160 ? line : line[..160] + "…";
    }

    private sealed record LoggedEntry(string Source, EventLogEntryType EntryType, DateTime TimeWritten, string Message);
}
