using DbDataSync.Api.Configuration;
using DbDataSync.Updates;

namespace DbDataSync.Api.Services;

public sealed record UpdateHistoryEntry(string Phase, string? Message, string? FromVersion, string? ToVersion, DateTimeOffset AtUtc, string? RequestedBy);

/// <param name="Apply">Contains <see cref="VersionPlaceholder"/> where the version goes.</param>
/// <param name="Rollback">Null when there is no earlier version to switch back to.</param>
/// <param name="ConvertsFirst">This is a machine-wide install from before versioned slots: the first apply converts
/// it, once, and says so.</param>
/// <param name="PrintsOnly">A global tool: <see cref="Apply"/> prints the <c>dotnet tool</c> commands to run rather
/// than applying anything.</param>
public sealed record UpdateCommandsResponse(
    string Where, string List, string Status, string Apply, string? Rollback, string VersionPlaceholder,
    bool ConvertsFirst, bool PrintsOnly);

public sealed record SlotResponse(string Name, string? Version, bool Current, bool Ambiguous);

public sealed record SlotCheckResponse(string Level, string Message);

/// <param name="Root">The tool directory: the launcher, <c>current.txt</c> and <c>versions/</c>.</param>
public sealed record SlotsResponse(string Root, string? Current, IReadOnlyList<SlotResponse> Slots, IReadOnlyList<SlotCheckResponse> Checks);

/// <param name="Enabled"><c>DbDataSync:Updates:Mode</c>: whether releases are looked up. Commands are given either way.</param>
/// <param name="Commands">What to run on the server; null for a container or a copy that is not a tool install, with
/// <see cref="CommandsUnavailableReason"/> saying why.</param>
/// <param name="Slots">Null when this process was not started by the launcher.</param>
public sealed record UpdateStatusResponse(
    bool Enabled,
    string? RunningVersion,
    string InstallKind,
    IReadOnlyList<string> Channels,
    UpdateCommandsResponse? Commands,
    string? CommandsUnavailableReason,
    SlotsResponse? Slots,
    string Phase,
    string? Message,
    DateTimeOffset? AtUtc,
    string? FromVersion,
    string? ToVersion,
    string? RequestedBy,
    string LogPath,
    IReadOnlyList<UpdateHistoryEntry> History);

/// <param name="Url">A page a human can read about this exact version — nuget.org's package-version page
/// for stable/beta, the GitHub Release page for a snapshot. Never null: every channel has one.</param>
public sealed record ReleaseResponse(
    string Version, string Channel, DateTimeOffset? BuiltUtc, bool Installed, bool Newer, string Url);

/// <summary>
/// The web console's Updates screen: what is running, what is available, and **the commands that update it**.
/// <para>
/// Phase 196L retired the console's own apply button and everything behind it — the request file, the wind-down, the
/// exit to be restarted, the privileged pre-start step. An update is applied from a shell on the server
/// (<c>dbdatasync update --apply</c>): into the slot that is not running, then a switch. This service describes that;
/// it changes nothing. It does read the state the CLI records, so an update in progress shows here too.
/// </para>
/// </summary>
public sealed class UpdateService(
    ApiOptions options,
    IHttpClientFactory httpClientFactory,
    UpdateHostFacts facts)
{
    private readonly UpdateStateStore _store = new(new UpdateWorkspace(options.RepoRoot));

    public UpdateStateStore Store => _store;

    public string? RunningVersion => facts.RunningVersion;

    public bool Enabled => options.SelfUpdateMode != UpdatesMode.Disabled;

    public string DisabledReason => "Looking up releases is turned off. Set DbDataSync:Updates:Mode to manual.";

    public bool IsChannelEnabled(ReleaseChannel channel) => options.SelfUpdateChannels.Contains(channel);

    public UpdateStatusResponse Status()
    {
        var state = _store.ReadState();
        var current = state.Current;
        var slots = Slots();
        var (commands, reason) = Commands(slots);

        return new UpdateStatusResponse(
            Enabled,
            facts.RunningVersion,
            facts.Location.Kind.ToString(),
            options.SelfUpdateChannels.Select(c => c.ToString().ToLowerInvariant()).ToList(),
            commands,
            reason,
            slots,
            (current?.Phase ?? UpdatePhase.Idle).ToString().ToLowerInvariant(),
            current?.Message,
            current?.AtUtc,
            current?.FromVersion,
            current?.ToVersion,
            current?.RequestedBy,
            _store.Workspace.LogPath,
            state.History.Select(h => new UpdateHistoryEntry(
                h.Phase.ToString().ToLowerInvariant(), h.Message, h.FromVersion, h.ToVersion, h.AtUtc, h.RequestedBy)).ToList());
    }

    private SlotsResponse? Slots()
    {
        if (facts.Launcher is not { } launcher)
            return null;

        var layout = new SlotLayout(launcher.Root);
        var current = layout.Current;
        return new SlotsResponse(
            layout.Root,
            current,
            new[] { SlotPaths.SlotA, SlotPaths.SlotB }.Select(name =>
            {
                var slot = layout.Slot(name);
                return new SlotResponse(name, slot.Version, name == current, slot.Ambiguous);
            }).ToList(),
            layout.Check().Select(c => new SlotCheckResponse(c.Level.ToString().ToLowerInvariant(), c.Message)).ToList());
    }

    /// <summary>The commands for this install. Every one names the data directory: the server knows it, and an explicit
    /// <c>--repo</c> is never wrong where a defaulted one could be (a service run against a non-default directory).</summary>
    private (UpdateCommandsResponse? Commands, string? Reason) Commands(SlotsResponse? slots)
    {
        switch (facts.Location.Kind)
        {
            case InstallKind.Container:
                return (null, "This is a container image. Update it by pulling a newer image tag and recreating the container.");
            case InstallKind.NotAToolInstall:
                return (null, "This copy was not installed as a dotnet tool (a development build or unpacked binaries), so there is nothing to update in place.");
        }

        var cli = UpdateCliCommands.For(facts.IsWindows, options.RepoRoot);
        var global = facts.Location.Kind == InstallKind.Global;
        var canRollBack = slots?.Slots.Any(s => !s.Current && s.Version is not null) == true;

        return (new UpdateCommandsResponse(
            cli.Where,
            cli.List,
            cli.Status,
            global ? $"dbdatasync update --to {UpdateCliCommands.VersionPlaceholder}" : cli.Apply,
            canRollBack ? cli.Rollback : null,
            UpdateCliCommands.VersionPlaceholder,
            ConvertsFirst: !global && facts.Launcher is null,
            PrintsOnly: global), null);
    }

    /// <summary>Lists releases on the enabled channels (or one of them), newest first. A channel that cannot be
    /// read is reported in <paramref name="warnings"/> as long as another can; all failing throws.</summary>
    public async Task<IReadOnlyList<ReleaseResponse>> ListReleasesAsync(
        ReleaseChannel? channel, int limit, List<string> warnings, CancellationToken cancellationToken)
    {
        var installed = ReleaseVersion.TryParse(facts.RunningVersion, out var parsed) ? parsed : null;
        var channels = channel is { } one ? [one] : options.SelfUpdateChannels;
        var catalog = NewCatalog();

        var results = new List<ReleaseResponse>();
        var failures = 0;
        foreach (var each in channels)
        {
            try
            {
                foreach (var release in await catalog.ListAsync(each, limit, cancellationToken))
                {
                    results.Add(new ReleaseResponse(
                        release.Version.Text, each.ToString().ToLowerInvariant(), release.BuiltUtc,
                        installed is not null && release.Version.Equals(installed),
                        installed is not null && release.Version > installed,
                        ReleaseUrlFor(release)));
                }
            }
            catch (ReleaseSourceException ex)
            {
                failures++;
                warnings.Add($"{each.ToString().ToLowerInvariant()}: {ex.Message}");
            }
        }

        if (failures == channels.Count && failures > 0)
            throw new ReleaseSourceException(string.Join(" ", warnings));

        return results;
    }

    /// <summary>A page a human can read about this exact release. A snapshot already carries its own
    /// GitHub Release page (<see cref="ReleaseInfo.ReleaseUrl"/>, read off the release's own <c>html_url</c>);
    /// stable/beta come from nuget.org's flat-container index, which has no such field, so that one is
    /// built from the package id and version — nuget.org's own URL scheme for a specific version page.</summary>
    private static string ReleaseUrlFor(ReleaseInfo release) =>
        release.ReleaseUrl?.ToString()
        ?? $"https://www.nuget.org/packages/{ReleaseSources.PackageId}/{release.Version.Text}";

    private string UserAgent => $"DbDataSync-update/{(ReleaseVersion.TryParse(facts.RunningVersion, out var v) ? v.Text : "unknown")}";

    private ReleaseCatalog NewCatalog() => new(
        httpClientFactory.CreateClient(),
        Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? Environment.GetEnvironmentVariable("GH_TOKEN"),
        UserAgent);
}
