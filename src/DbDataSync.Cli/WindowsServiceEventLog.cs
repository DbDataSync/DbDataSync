using System.Diagnostics;
using System.Runtime.Versioning;

namespace DbDataSync.Cli;

/// <summary>
/// Phase 136: a startup failure under a real Windows service has nowhere else to go — <c>Console.Error</c>
/// is invisible to a process the SCM started, and <c>UseWindowsService()</c>'s own <c>ILogger</c>-backed
/// EventLog provider doesn't exist yet for anything that fails before <see cref="DbDataSync.Api.DbDataSyncHost.Build"/>
/// has returned (see <see cref="ServeCommand.RunAsync"/>, which is the only real caller).
/// <para>
/// Writes <c>System.Diagnostics.EventLog.WriteEntry</c> directly, not through <c>ILogger</c> — exactly
/// for that reason: this has to work before there is a DI container, or any other logging pipeline, to
/// route through.
/// </para>
/// <para>
/// <c>[SupportedOSPlatform("windows")]</c> on every member, and each one also no-ops off Windows rather
/// than relying on the underlying package to throw — matching this repo's existing
/// <c>OperatingSystem.IsWindows()</c> guard convention (see <c>CliOptions</c>, <c>CertCommand</c>,
/// <c>ToolCommand</c>) rather than leaving the behavior off-Windows to whatever
/// <c>System.Diagnostics.EventLog</c> happens to do there.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsServiceEventLog
{
    /// <summary>Matches <see cref="ServiceCommand.ServiceName"/> — the same "DbDataSync" name used
    /// everywhere else a Windows-facing identifier is needed.</summary>
    internal const string SourceName = ServiceCommand.ServiceName;

    private const string LogName = "Application";

    /// <summary>
    /// Registers the Event Log source, once — <c>EventLog.CreateEventSource</c> needs elevated rights
    /// the first time a given source is used, so this runs at <c>service install</c> time (already
    /// elevated), not lazily at the moment a startup failure actually needs to write. Guarded by
    /// <see cref="EventLog.SourceExists"/> so re-running <c>service install</c> (upgrade, repair) never
    /// errors on an already-registered source.
    /// </summary>
    internal static void EnsureSourceRegistered()
    {
        if (!OperatingSystem.IsWindows())
            return;

        if (!EventLog.SourceExists(SourceName))
            EventLog.CreateEventSource(SourceName, LogName);
    }

    /// <summary>The real exception type and message — the specific detail Event Viewer showed nothing
    /// of before this phase (just Service Control Manager's own generic "failed to start" /
    /// "30000 millisecond timeout reached"). <paramref name="message"/>, when given, replaces the
    /// default type/message formatting — <see cref="ServeCommand.PrepareFailureMessage"/>'s own richer
    /// text (which already names the exception's message, plus a registered service's own
    /// account/platform) is what phase 135's own error actually needs surfaced here, not a generic
    /// <c>Type: message</c> line.</summary>
    internal static void WriteError(Exception exception, string? message = null)
    {
        if (!OperatingSystem.IsWindows())
            return;

        EventLog.WriteEntry(
            SourceName, message ?? $"{exception.GetType().Name}: {exception.Message}", EventLogEntryType.Error);
    }

    /// <summary>One milestone, not a duplicate console transcript — see <see cref="ServeCommand.RunAsync"/>'s
    /// own <c>ApplicationStarted</c> hook, the only real caller.</summary>
    internal static void WriteInformation(string message)
    {
        if (!OperatingSystem.IsWindows())
            return;

        EventLog.WriteEntry(SourceName, message, EventLogEntryType.Information);
    }
}
