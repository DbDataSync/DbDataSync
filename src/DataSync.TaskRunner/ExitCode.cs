namespace DataSync.TaskRunner;

/// <summary>
/// Distinguishable failure categories so the Supervisor (Phase 5) can react to a run's outcome
/// without parsing stdout — per architecture/implementation-plan.md Phase 4.
/// </summary>
public enum ExitCode
{
    Success = 0,
    ConfigError = 1,
    ConnectivityError = 2,
    DataError = 3,
    AlreadyRunning = 4,

    /// <summary>
    /// The process that owns the state store did not respond within the grace period. Everything this
    /// run had already achieved was written to a journal beside the state database, for the owner to
    /// apply when it returns — see phase 39.
    /// <para>
    /// Distinct from <see cref="ConnectivityError"/>, which is a *source or target* database being
    /// unreachable. This one says the work may well have succeeded and the record of it is on disk.
    /// </para>
    /// </summary>
    StateOwnerUnavailable = 5,
}
