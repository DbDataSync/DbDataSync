using System.Net;
using System.Reflection;
using DataSync.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using DataSync.Api.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace DataSync.Api.Tests;

/// <summary>
/// Every endpoint has had a decision made about it, and the decision is enumerated from the
/// controllers rather than from a list somebody maintains — a hand-written list of routes is a list
/// that goes stale the first time somebody adds one in a hurry.
/// </summary>
public sealed class AuthorizationCoverageTests
{
    private static IEnumerable<(Type Controller, MethodInfo Action)> Endpoints() =>
        typeof(AuthController).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any())
                .Select(m => (t, m)));

    /// <summary>
    /// The fallback policy closes anything unmarked, so this asserts the *shape* rather than the
    /// permission: an action is either explicitly anonymous, explicitly viewer-readable, or falls
    /// through to admin. What must not exist is an action nobody thought about that reads as open.
    /// </summary>
    [Fact]
    public void EveryEndpoint_IsEitherAnonymous_ViewerReadable_OrAdminByFallback()
    {
        var endpoints = Endpoints().ToList();

        Assert.NotEmpty(endpoints);

        foreach (var (controller, action) in endpoints)
        {
            var anonymous = action.GetCustomAttribute<AllowAnonymousAttribute>() is not null
                || controller.GetCustomAttribute<AllowAnonymousAttribute>() is not null;
            var policy = action.GetCustomAttribute<AuthorizeAttribute>()?.Policy
                ?? controller.GetCustomAttribute<AuthorizeAttribute>()?.Policy;

            Assert.True(
                anonymous || policy is null or Policies.Viewer or Policies.Admin,
                $"{controller.Name}.{action.Name} carries an authorize policy this app does not define ('{policy}').");
        }
    }

    /// <summary>
    /// **The two that look like reads and are not.** `connections/{name}/test` opens a connection to
    /// somebody's database and `scripts/{name}/test` compiles and executes operator-authored C#. Both
    /// are GET-shaped in intent and neither belongs to a viewer, which is why the dividing line is
    /// "does this make something happen" rather than the HTTP verb.
    /// </summary>
    [Theory]
    [InlineData("ConnectionsController", "Test")]
    [InlineData("ScriptsController", "Test")]
    [InlineData("ScriptsController", "Compile")]
    public void TheActionsThatLookLikeReads_AreNotViewerReadable(string controllerName, string actionName)
    {
        var (controller, action) = Endpoints()
            .Single(e => e.Controller.Name == controllerName && e.Action.Name == actionName);

        var policy = action.GetCustomAttribute<AuthorizeAttribute>()?.Policy
            ?? controller.GetCustomAttribute<AuthorizeAttribute>()?.Policy;

        Assert.NotEqual(Policies.Viewer, policy);
    }
}

/// <summary>
/// The same thing over the wire, because attributes are a claim about what happens and a request is
/// the thing that happens.
/// </summary>
public sealed class AuthorizationEnforcementTests(AuthenticatedApiFactory factory)
    : IClassFixture<AuthenticatedApiFactory>
{
    [Theory]
    [InlineData("/api/connections")]
    [InlineData("/api/replications")]
    [InlineData("/api/scripts")]
    public async Task WithoutASession_EverythingIsRefused(string path)
    {
        var response = await factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Health is the container's own probe. A probe that needs a session reports a healthy
    /// application as unreachable.</summary>
    [Fact]
    public async Task Health_IsReachableWithoutASession() =>
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync("/api/health")).StatusCode);

    /// <summary>The answer to "am I signed in" cannot itself require being signed in.</summary>
    [Fact]
    public async Task AuthStatus_IsReachableWithoutASession() =>
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync("/api/auth/status")).StatusCode);

    [Fact]
    public async Task AViewer_CanRead()
    {
        var client = await factory.SignedInAsAsync(DataSync.State.UserRole.Viewer);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/replications")).StatusCode);
    }

    [Theory]
    [InlineData("/api/replications/anything/runs")]
    [InlineData("/api/connections/anything/test")]
    [InlineData("/api/scripts/anything/compile")]
    public async Task AViewer_CannotMakeAnythingHappen(string path)
    {
        var client = await factory.SignedInAsAsync(DataSync.State.UserRole.Viewer);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(path, null)).StatusCode);
    }

    /// <summary>An admin is a viewer too — stated once in the policy rather than by putting both roles
    /// on every read endpoint.</summary>
    [Fact]
    public async Task AnAdmin_CanRead()
    {
        var client = await factory.SignedInAsAsync(DataSync.State.UserRole.Admin);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/replications")).StatusCode);
    }

    /// <summary>
    /// Disabling somebody takes effect on their next request. The session row is still there and still
    /// unexpired; what changed is the user it points at, which is re-read every time for exactly this.
    /// </summary>
    [Fact]
    public async Task DisablingAUser_TakesEffectOnTheNextRequest()
    {
        var (client, user) = await factory.SignedInWithUserAsync(DataSync.State.UserRole.Admin);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/replications")).StatusCode);

        factory.Users.SetEnabled(user.Id, enabled: false);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/replications")).StatusCode);
    }

    [Fact]
    public async Task SigningOut_EndsTheSession()
    {
        var client = await factory.SignedInAsAsync(DataSync.State.UserRole.Admin);

        (await client.PostAsync("/api/auth/sign-out", null)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/replications")).StatusCode);
    }
}
