namespace DbDataSync.Drivers.Abstractions;

/// <summary>
/// The versioning policy for the compiled-plugin contract (<see cref="IDriver.ContractVersion"/>) —
/// per <c>architecture/planning/todo/nuget-loaded-drivers.md</c> Q3, "no promises, fail loud" while
/// DbDataSync is pre-1.0. A single current version and no support window: a plugin targets a specific
/// DbDataSync minor, and a mismatch is refused with a message saying so, rather than a compatibility
/// shim nobody has asked for yet.
/// </summary>
public static class DriverContract
{
    /// <summary>The only <see cref="IDriver.ContractVersion"/> a compiled plugin may declare right now.
    /// Bump this — and this alone — the day <c>IDriver</c>'s shape changes in a way an existing
    /// compiled plugin could not tolerate.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The oldest <see cref="IDriver.ContractVersion"/> the loader still accepts. Equal to
    /// <see cref="CurrentVersion"/> until a real compatibility window is needed — see the plan doc's
    /// Q3, "no promises" leaning.</summary>
    public const int MinSupportedVersion = 1;

    public static bool IsSupported(int contractVersion) => contractVersion is >= MinSupportedVersion and <= CurrentVersion;
}
