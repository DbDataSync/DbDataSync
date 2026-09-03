using System.Net;
using ClrKernel.Core.Secrets;
using DbDataSync.Api.Auth;
using DbDataSync.State;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DbDataSync.Api.Tests;

/// <summary>
/// The API with authentication **on**, which <see cref="TestApiFactory"/> deliberately does not have —
/// every other test in this project is about what an endpoint does, not about who may call it, and
/// making all of them sign in first would obscure that.
/// <para>
/// Sessions are minted by writing rows, not by negotiating: Kerberos against a test host is not
/// something a Linux container does, and a test-only authentication handler would be a second way in
/// that has to be impossible to enable in a real deployment. Writing a session row exercises the real
/// handler, the real store and the real policies — everything except the one method that cannot run
/// here.
/// </para>
/// </summary>
public sealed class AuthenticatedApiFactory : WebApplicationFactory<Program>
{
    public string RepoRoot { get; } = Directory.CreateTempSubdirectory("dbdatasync-auth-tests-").FullName;

    public UserStore Users => Services.GetRequiredService<UserStore>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DbDataSync:RepoRoot"] = RepoRoot,
                ["DbDataSync:StateDbPath"] = Path.Combine(RepoRoot, "state.db"),
                // Named, so AuthOptions.WindowsEnabled is true and the deployment is a configured one
                // rather than the authentication-disabled escape hatch.
                ["DbDataSync:Auth:AdminGroup"] = "DbDataSyncAdmins",
                ["DbDataSync:Auth:ViewerGroup"] = "DbDataSyncViewers",
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<SecretStore>();
            services.AddSingleton(SecretStore.ForProviders([new InMemorySecretProvider()]));
        });
    }

    public async Task<HttpClient> SignedInAsAsync(UserRole role) => (await SignedInWithUserAsync(role)).Client;

    public Task<(HttpClient Client, UserRecord User)> SignedInWithUserAsync(UserRole role)
    {
        var user = Users.CreateUser($"{role} {Guid.NewGuid():N}", $"{role}@example.com".ToLowerInvariant(), role);
        Users.AddCredential(user.Id, CredentialMethods.Windows, $"S-1-5-21-{Guid.NewGuid():N}");

        var session = Services.GetRequiredService<SessionStore>().Create(user.Id);

        var client = CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{AuthOptions.SessionCookie}={session.Id}");
        return Task.FromResult((client, user));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(RepoRoot))
            Directory.Delete(RepoRoot, recursive: true);
    }
}
