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
}
