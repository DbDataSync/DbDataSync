using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataSync.Api.Tests;

/// <summary>
/// Supplies <see cref="IConnectionItemsFeature"/> to every in-process test request, because
/// <c>TestServer</c> — what <c>WebApplicationFactory</c> runs against — does not.
/// <para>
/// **Why this exists at all.** On Windows the composition root registers <c>AddNegotiate()</c>, and
/// <c>NegotiateHandler</c> is an <see cref="Microsoft.AspNetCore.Authentication.IAuthenticationRequestHandler"/>:
/// the authentication middleware runs it on *every* request, whatever scheme actually ends up
/// authenticating, and the very first thing it does is reach for this feature — a Kestrel connection
/// concept with no <c>TestServer</c> equivalent — and throw
/// <c>NotSupportedException: Negotiate authentication requires a server that supports
/// IConnectionItemsFeature like Kestrel</c> when it is missing. Every request through an
/// authentication-enabled host therefore answered 500 on Windows, which is what made the great
/// majority of this project fail the moment a real <c>windows-latest</c> CI job existed (phase 140).
/// </para>
/// <para>
/// **Why a shim rather than not registering Negotiate.** Gating registration on <c>Auth:Disabled</c>
/// cannot fix this, for two independent reasons. Half the failures are
/// <see cref="AuthenticatedApiFactory"/>-based tests, whose whole point is an auth-*enabled* host —
/// they need Negotiate registered exactly as production has it. And the gate cannot even see a test's
/// configuration: <c>WebApplicationFactory</c> layers <c>ConfigureAppConfiguration</c> in during
/// <c>builder.Build()</c>, after the composition root has already read <c>builder.Configuration</c> to
/// decide (the same trap <c>DbDataSyncHost</c>'s own ApiOptions comment warns about), so
/// <c>Auth:Disabled=true</c> registered Negotiate anyway. This shim fixes both populations at once and
/// changes no production code.
/// </para>
/// <para>
/// **Why it is safe.** Nothing here authenticates through Negotiate — <c>AuthenticatedApiFactory</c>
/// mints sessions by writing rows precisely because Kerberos against a test host is not a thing, and
/// no test calls the one <c>[Authorize(AuthenticationSchemes = Negotiate)]</c> endpoint. With the
/// feature present and no <c>Authorization</c> header on the request, <c>NegotiateHandler</c> returns
/// without handling anything and the pipeline continues to the session scheme — which is the real
/// behaviour under test. The shim supplies the feature and no more; it is not a fake identity, and
/// there is no way to authenticate through it.
/// </para>
/// </summary>
internal sealed class TestServerConnectionItemsFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        // An IStartupFilter's own middleware runs ahead of everything the composition root's pipeline
        // adds, which is what puts this before UseAuthentication without the test project having to
        // know where in that pipeline authentication sits.
        app.Use(async (context, nextMiddleware) =>
        {
            if (context.Features.Get<IConnectionItemsFeature>() is null)
                context.Features.Set<IConnectionItemsFeature>(new TestConnectionItems());

            await nextMiddleware();
        });

        next(app);
    };

    /// <summary>
    /// Per request, not per connection — TestServer has no connection to hang it on, and nothing here
    /// needs one: the items bag only ever carries Negotiate's multi-leg handshake state, and a
    /// handshake never starts. <see cref="ConnectionItems"/> rather than a bare dictionary because
    /// that is what Kestrel supplies and its indexer returns null for a missing key where
    /// <c>Dictionary</c> throws — which is exactly how <c>NegotiateHandler</c> reads it.
    /// </summary>
    private sealed class TestConnectionItems : IConnectionItemsFeature
    {
        public IDictionary<object, object?> Items { get; set; } = new ConnectionItems();
    }
}

internal static class TestServerConnectionItems
{
    /// <summary>Registers <see cref="TestServerConnectionItemsFilter"/>. Called by both factories in
    /// this project; any third one needs it too.</summary>
    public static void AddTestServerConnectionItems(this IServiceCollection services) =>
        services.AddSingleton<IStartupFilter, TestServerConnectionItemsFilter>();
}
