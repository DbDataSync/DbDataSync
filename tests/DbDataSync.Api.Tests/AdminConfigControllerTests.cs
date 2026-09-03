using System.Net;
using System.Net.Http.Json;
using DbDataSync.Core.Config;
using DbDataSync.State;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// The admin config screen (phase 81): every documented DbDataSync:* key, its live effective value and
/// source, and — for the keys dbdatasync.config.yaml's writer can address — editing or adopting it.
/// <para>
/// Every test that cares whether a key is "file-sourced" builds its own <see cref="AuthenticatedApiFactory"/>
/// rather than sharing the class fixture: <c>DbDataSyncHost.InsertConfigFile</c> only wires
/// dbdatasync.config.yaml in as a configuration source if the file already exists the moment the host
/// boots (the first request any test makes against a shared factory), so the file has to be written
/// before that — and once the host is up, nothing here reloads it. The shared <paramref name="factory"/>
/// is used only for tests that do not depend on what, if anything, is already on disk.
/// </para>
/// </summary>
public sealed class AdminConfigControllerTests(AuthenticatedApiFactory factory) : IClassFixture<AuthenticatedApiFactory>
{
    private const string UrlKey = "DbDataSync:Url";
    private const string StateConnectionStringKey = "DbDataSync:StateConnectionString";

    private sealed record EntryDto(
        string Key, string? Value, string Source, bool Editable, bool CanAdopt, bool Masked, string Description);

    [Theory]
    [InlineData("GET", "/api/admin/config", false)]
    [InlineData("PUT", "/api/admin/config/DbDataSync%3AUrl", true)]
    [InlineData("PUT", "/api/admin/config/DbDataSync%3AStateConnectionString/secret", true)]
    public async Task AViewer_IsRefusedByAllThreeEndpoints(string method, string path, bool hasBody)
    {
        var client = await factory.SignedInAsAsync(UserRole.Viewer);

        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (hasBody)
            request.Content = JsonContent.Create(new { value = "x" });

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnAdmin_SeesEveryDocumentedKey()
    {
        var client = await factory.SignedInAsAsync(UserRole.Admin);

        var entries = await client.GetFromJsonAsync<List<EntryDto>>("/api/admin/config");

        Assert.NotNull(entries);
        Assert.Contains(entries!, e => e.Key == UrlKey);
        Assert.Contains(entries!, e => e.Key == StateConnectionStringKey);
        Assert.Contains(entries!, e => e.Key == "DbDataSync:Auth:Disabled");
        Assert.Contains(entries!, e => e.Key == "DbDataSync:Auth:Passkeys:Origins");

        // The nested Auth:*/Passkeys:* keys are shown but this screen cannot write them — see
        // AdminConfigService's class doc comment for why.
        var disabled = entries!.Single(e => e.Key == "DbDataSync:Auth:Disabled");
        Assert.False(disabled.Editable);
        Assert.False(disabled.CanAdopt);
    }

    /// <summary>
    /// dbdatasync.config.yaml only ever addresses the top-level DbDataSync:&lt;Key&gt; shape — an Auth:*
    /// key is shown (real value, real source) but PUT refuses it rather than writing something
    /// SetValue cannot actually express.
    /// </summary>
    [Fact]
    public async Task ANestedKey_CannotBeWritten()
    {
        var client = await factory.SignedInAsAsync(UserRole.Admin);

        var response = await client.PutAsJsonAsync("/api/admin/config/DbDataSync%3AAuth%3ADisabled", new { value = "true" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>The secret endpoint writes through SecretStore under the fixed ref, exactly like
    /// `dbdatasync secret set` — never through dbdatasync.config.yaml.</summary>
    [Fact]
    public async Task TheSecretEndpoint_StoresThroughSecretStore_NotTheFile()
    {
        var client = await factory.SignedInAsAsync(UserRole.Admin);

        var response = await client.PutAsJsonAsync(
            $"/api/admin/config/{Uri.EscapeDataString(StateConnectionStringKey)}/secret", new { value = "hunter2" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.DoesNotContain(
            "hunter2", DbDataSyncConfigFile.Read(factory.RepoRoot).GetValueOrDefault(StateConnectionStringKey) ?? "");
    }

    /// <summary>
    /// A file-sourced value round-trips: PUT lands on disk, a git commit records it, and GET keeps
    /// reporting source "file" with the value now on disk — see AdminConfigService.ToEntry's comment on
    /// why GET re-reads the file rather than the (boot-time, otherwise stale) IConfiguration snapshot.
    /// </summary>
    [Fact]
    public async Task AFileSourcedValue_RoundTrips()
    {
        using var scoped = new AuthenticatedApiFactory();
        DbDataSyncConfigFile.SetValue(scoped.RepoRoot, "DbDataSync", "Url", "http://initial/");
        var client = await scoped.SignedInAsAsync(UserRole.Admin);

        var before = await client.GetFromJsonAsync<List<EntryDto>>("/api/admin/config");
        var beforeEntry = before!.Single(e => e.Key == UrlKey);
        Assert.Equal("file", beforeEntry.Source);
        Assert.Equal("http://initial/", beforeEntry.Value);
        Assert.True(beforeEntry.Editable);

        var putResponse = await client.PutAsJsonAsync(
            $"/api/admin/config/{Uri.EscapeDataString(UrlKey)}", new { value = "http://changed/" });
        putResponse.EnsureSuccessStatusCode();

        Assert.Equal("http://changed/", DbDataSyncConfigFile.Read(scoped.RepoRoot)[UrlKey]);

        var after = await client.GetFromJsonAsync<List<EntryDto>>("/api/admin/config");
        var afterEntry = after!.Single(e => e.Key == UrlKey);
        Assert.Equal("file", afterEntry.Source);
        Assert.Equal("http://changed/", afterEntry.Value);
    }

    /// <summary>The other half of masking: a file-sourced StateConnectionString has no credential to
    /// begin with (phase 79 rejects one at write time), so it is shown as-is, never masked.</summary>
    [Fact]
    public async Task AFileSourcedStateConnectionString_IsShownAsIs()
    {
        using var scoped = new AuthenticatedApiFactory();
        DbDataSyncConfigFile.SetValue(
            scoped.RepoRoot, "DbDataSync", "StateConnectionString", "Server=sql01;Database=DbDataSyncState;");
        var client = await scoped.SignedInAsAsync(UserRole.Admin);

        var entries = await client.GetFromJsonAsync<List<EntryDto>>("/api/admin/config");
        var entry = entries!.Single(e => e.Key == StateConnectionStringKey);

        Assert.Equal("file", entry.Source);
        Assert.False(entry.Masked);
        Assert.Equal("Server=sql01;Database=DbDataSyncState;", entry.Value);
    }

    /// <summary>
    /// An env-var-sourced key is read-only with its source labeled, and "adopt" (the same PUT the
    /// direct-edit case uses) moves it into the file — even though, with no hot-reload, the environment
    /// variable keeps winning in *this* running process.
    /// </summary>
    [Fact]
    public async Task AnEnvVarSourcedValue_IsReadOnly_AndAdoptMovesItIntoTheFile()
    {
        Environment.SetEnvironmentVariable("DbDataSync__Url", "http://from-env/");
        try
        {
            using var scoped = new AuthenticatedApiFactory();
            var client = await scoped.SignedInAsAsync(UserRole.Admin);

            var before = await client.GetFromJsonAsync<List<EntryDto>>("/api/admin/config");
            var beforeEntry = before!.Single(e => e.Key == UrlKey);
            Assert.Equal("environment variable", beforeEntry.Source);
            Assert.Equal("http://from-env/", beforeEntry.Value);
            Assert.False(beforeEntry.Editable);
            Assert.True(beforeEntry.CanAdopt);

            var adopt = await client.PutAsJsonAsync(
                $"/api/admin/config/{Uri.EscapeDataString(UrlKey)}", new { value = beforeEntry.Value });
            adopt.EnsureSuccessStatusCode();

            Assert.Equal("http://from-env/", DbDataSyncConfigFile.Read(scoped.RepoRoot)[UrlKey]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DbDataSync__Url", null);
        }
    }

    /// <summary>
    /// StateConnectionString's secret is never present in any response body — file-sourced (where phase
    /// 79 guarantees there is none to begin with) or, here, sourced from an environment variable that an
    /// operator seeded with a raw Password= the old way, before adopting dbdatasync.config.yaml.
    /// </summary>
    [Fact]
    public async Task AnEnvVarStateConnectionStringWithAPassword_IsNeverSentToTheBrowser()
    {
        Environment.SetEnvironmentVariable("DbDataSync__StateConnectionString", "Server=sql01;Password=hunter2;");
        try
        {
            using var scoped = new AuthenticatedApiFactory();
            var client = await scoped.SignedInAsAsync(UserRole.Admin);

            var response = await client.GetAsync("/api/admin/config");
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("hunter2", body);

            var entries = await client.GetFromJsonAsync<List<EntryDto>>("/api/admin/config");
            var entry = entries!.Single(e => e.Key == StateConnectionStringKey);
            Assert.Equal("environment variable", entry.Source);
            Assert.True(entry.Masked);
            Assert.Null(entry.Value);
            Assert.False(entry.CanAdopt);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DbDataSync__StateConnectionString", null);
        }
    }
}
