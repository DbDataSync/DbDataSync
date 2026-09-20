namespace DbDataSync.Api.Services;

/// <summary>
/// While an update drains, changes are paused: any request that would change something answers <c>409</c>
/// with a sentence saying why. Reads still work, so the console can show the update's own progress.
/// <para>
/// One place rather than a check in every controller that can start work — there are a dozen of those, and the
/// next one added would otherwise have to remember. The cost is that it also pauses edits to configuration for
/// the (bounded) drain window, which is a sensible thing to want while the process is about to restart.
/// Exempt: signing in, the update endpoints themselves, the health probe and the live-run hub.
/// </para>
/// </summary>
public sealed class UpdateDrainMiddleware(RequestDelegate next, UpdateDrainState drain)
{
    private static readonly string[] ExemptPrefixes = ["/api/auth", "/api/admin/update", "/api/health", "/hubs"];

    public Task InvokeAsync(HttpContext context)
    {
        if (drain.IsDraining && IsMutation(context.Request.Method) && !IsExempt(context.Request.Path))
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return context.Response.WriteAsJsonAsync(new
            {
                error = "An update is being applied, so changes are paused until the service has restarted.",
            });
        }

        return next(context);
    }

    private static bool IsMutation(string method) =>
        !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method);

    private static bool IsExempt(PathString path) =>
        ExemptPrefixes.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));
}
