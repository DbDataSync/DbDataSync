namespace DbDataSync.Updates;

/// <summary>Where this process came from, as the launcher told it (phase 196L). Null when it was not started by a
/// launcher: a pre-196L install, a global tool, a development build, a container.</summary>
public sealed record LauncherContext(string Root, string Slot)
{
    public static LauncherContext? Current() =>
        AppContext.GetData(SlotPaths.RootDataKey) is string root
        && SlotPaths.ParseSlot(AppContext.GetData(SlotPaths.SlotDataKey) as string) is { } slot
            ? new LauncherContext(root, slot)
            : null;
}

/// <param name="Version">What the slot holds; null when it is empty. When it holds several (see
/// <see cref="Ambiguous"/>) this is null too — there is no one version to name.</param>
/// <param name="Ambiguous">More than one install in one slot: the launcher refuses to guess between them.</param>
public sealed record SlotState(string Slot, string Directory, string? Version, bool Ambiguous);

public enum SlotCheckLevel
{
    /// <summary>Worth knowing, and normal in some situations (after a rollback, say).</summary>
    Note,

    /// <summary>The launcher will not start, or will not start what someone expects.</summary>
    Warning,
}

public sealed record SlotCheck(SlotCheckLevel Level, string Message);

/// <summary>
/// A slot install on disk (phase 196L): which slot is current, what each holds, and the one write that moves between
/// them. See <see cref="SlotPaths"/> for the layout.
/// </summary>
public sealed class SlotLayout(string root)
{
    public string Root { get; } = Path.GetFullPath(root);

    public string PointerPath => SlotPaths.PointerPath(Root);

    /// <summary>The slot the pointer names; null when there is no valid pointer.</summary>
    public string? Current => SlotPaths.ReadPointer(Root);

    /// <summary>Whether this directory has been converted to slots at all.</summary>
    public bool Exists => File.Exists(PointerPath) || System.IO.Directory.Exists(Path.Combine(Root, SlotPaths.VersionsDirectoryName));

    public SlotState Slot(string slot)
    {
        var directory = SlotPaths.SlotDirectory(Root, slot);
        var versions = SlotPaths.InstalledVersions(directory);
        var payloads = SlotPaths.FindPayloads(directory);
        return versions.Count > 1 || payloads.Count > 1
            ? new SlotState(slot, directory, null, true)
            : new SlotState(slot, directory, payloads.Count == 1 ? versions.Single() : null, false);
    }

    /// <summary>
    /// Makes <paramref name="slot"/> current. A temp file renamed over the pointer, so a crash or a full disk leaves
    /// either the old pointer or the new one, never a torn one — this write is the commit point of an update.
    /// </summary>
    public void Flip(string slot)
    {
        if (SlotPaths.ParseSlot(slot) != slot)
            throw new ArgumentException($"'{slot}' is not a slot.", nameof(slot));

        System.IO.Directory.CreateDirectory(Root);
        var temp = Path.Combine(Root, $".{SlotPaths.PointerFileName}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, slot + "\n");
        File.Move(temp, PointerPath, overwrite: true);
    }

    /// <summary>
    /// Phase 196L's sanity check: the pointer against what is actually on disk. **Never** what decides what runs —
    /// the pointer does that — only a way to notice the two disagreeing before someone is surprised by it.
    /// </summary>
    public IReadOnlyList<SlotCheck> Check()
    {
        var checks = new List<SlotCheck>();
        var current = Current;
        if (current is null)
        {
            checks.Add(new SlotCheck(SlotCheckLevel.Warning,
                $"{PointerPath} is missing or does not say \"a\" or \"b\", so the launcher will not start anything."));
            return checks;
        }

        var live = Slot(current);
        var other = Slot(SlotPaths.Other(current));

        if (live.Ambiguous)
            checks.Add(new SlotCheck(SlotCheckLevel.Warning,
                $"Slot {current} holds more than one install, so the launcher will not start. Reinstall it with `dbdatasync launcher repair`."));
        else if (live.Version is null)
            checks.Add(new SlotCheck(SlotCheckLevel.Warning,
                $"The pointer names slot {current}, which is empty, so the launcher will not start." +
                (other.Version is not null ? $" Slot {other.Slot} holds {other.Version}: write \"{other.Slot}\" into {PointerPath}." : "")));

        if (other.Ambiguous)
            checks.Add(new SlotCheck(SlotCheckLevel.Warning,
                $"Slot {other.Slot} holds more than one install. The next update clears it."));

        if (live.Version is not null && other.Version is not null
            && ReleaseVersion.TryParse(live.Version, out var liveVersion)
            && ReleaseVersion.TryParse(other.Version, out var otherVersion)
            && liveVersion < otherVersion)
        {
            checks.Add(new SlotCheck(SlotCheckLevel.Note,
                $"Running {live.Version}, the older of the two installed; slot {other.Slot} holds {other.Version}. " +
                $"That is what a rollback leaves. `dbdatasync update --to {other.Version} --apply` goes back to it without downloading anything."));
        }

        return checks;
    }
}
