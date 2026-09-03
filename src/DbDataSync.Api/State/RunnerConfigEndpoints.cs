using DbDataSync.Core.Config;
using DbDataSync.State.Remote;

namespace DbDataSync.Api.State;

/// <summary>
/// The config writes a spawned TaskRunner performs, served by the process that owns the repository.
/// <para>
/// A file of its own beside <see cref="RunnerStateEndpoints"/>, for the reason
/// <see cref="IRunnerConfig"/> is an interface of its own: what these routes reach is
/// <see cref="ConfigRepository"/> and a git commit, not the state store. Both are mapped from
/// <see cref="StateHost"/>, which is where a reader goes to find out what that server exposes.
/// </para>
/// <para>
/// **Under <see cref="StateProtocol.Route"/> deliberately, despite the name.** That prefix is what
/// <see cref="RunnerStateGuard"/> matches on — a route group with a prefix of its own would be
/// anonymous, on a loopback listener, with no token check, which is one refactor's distance from being
/// the hole the guard exists to close. Sharing the guarded prefix costs a slightly wide-sounding
/// constant; not sharing it costs the check.
/// </para>
/// </summary>
public static class RunnerConfigEndpoints
{
    public static void Map(IEndpointRouteBuilder app, IRunnerConfig config)
    {
        var group = app.MapGroup(StateProtocol.Route);

        // Same reasoning as RunnerStateEndpoints': this authenticates a child process with
        // RunnerToken, not a user. See RunnerStateGuard.
        group.AllowAnonymous();

        group.MapPost("/report-provisioned-target-columns", (ReportProvisionedTargetColumnsRequest r) =>
        {
            // An empty list is refused rather than written. The cache's "nothing captured" state is
            // load-bearing — phase 91's consumers throw MetadataNotCachedException on it, naming the
            // Refresh an operator should press — and a report of no columns could only come from a
            // provisioning pass that found a table with none, which is not a table that can be
            // replicated. Writing it would clear a good cache on the strength of a bad read.
            if (r.Columns.Count == 0)
                return Results.BadRequest("A provisioned-target-columns report must name at least one column.");

            config.ReportProvisionedTargetColumns(r.ReplicationName, r.MappingName, r.Columns);
            return Results.Ok();
        });
    }
}
