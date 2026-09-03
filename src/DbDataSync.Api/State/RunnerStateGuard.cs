using System.Net;
using DbDataSync.State.Remote;

namespace DbDataSync.Api.State;

/// <summary>
/// Refuses any request to the runner-state route that is not from loopback, or that does not carry the
/// token this process generated for its children.
/// <para>
/// The second half of a pair. The first is binding the state endpoint to loopback, where the OS refuses
/// the connection and no application code has to be correct. This is what survives a misconfiguration
/// of that — an operator who widens the binding, or a refactor that merges the two endpoints back
/// together.
/// </para>
/// <para>
/// **Loopback is not trusted.** Any process belonging to any local user can reach 127.0.0.1, so the
/// token is not belt-and-braces: it is the part that distinguishes this API's own children from
/// anything else on the host.
/// </para>
/// </summary>
public sealed class RunnerStateGuard(RequestDelegate next, RunnerToken token)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments(StateProtocol.Route))
        {
            await next(context);
            return;
        }

        var remote = context.Connection.RemoteIpAddress;
        // A null remote address is a connection this process cannot attribute — an in-memory test
        // server, or a transport that does not report one. Treated as local, because the alternative is
        // that nothing can reach it at all.
        var isLoopback = remote is null || IPAddress.IsLoopback(remote);

        if (!isLoopback)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!token.Matches(context.Request.Headers[StateProtocol.TokenHeader]))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await next(context);
    }
}

/// <summary>
/// The secret this process hands its children, so it can tell them apart from anything else that can
/// reach loopback.
/// <para>
/// Generated per API process and never written to disk. It reaches a child through the **environment**
/// rather than the command line: on Linux <c>/proc/&lt;pid&gt;/cmdline</c> is world-readable and
/// <c>/proc/&lt;pid&gt;/environ</c> is not, so a token in an argument is visible to every local user
/// through <c>ps</c> — precisely the threat it exists to answer.
/// </para>
/// </summary>
public sealed class RunnerToken
{
    private readonly string _value = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    public string Value => _value;

    /// <summary>Fixed-time comparison — the token is a bearer secret and a length-or-prefix leak is
    /// cheap to avoid here.</summary>
    public bool Matches(string? candidate) =>
        candidate is not null
        && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(candidate), System.Text.Encoding.UTF8.GetBytes(_value));
}
