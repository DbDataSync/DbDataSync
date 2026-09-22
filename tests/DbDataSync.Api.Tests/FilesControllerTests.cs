using System.Net;
using System.Net.Http.Json;
using DbDataSync.Api.Auth;
using DbDataSync.State;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>Phase 173V — the <c>files/</c> store's own API. <see cref="FilesApiFactory"/> seeds one
/// JDBC-backed <c>driver.yaml</c> naming a file that doesn't exist on disk yet, so a single fixture can
/// cover both the plain list/upload/delete surface and the <c>usedBy</c> computation.</summary>
[Trait("Category", "Integration")]
public sealed class FilesControllerTests(FilesApiFactory factory) : IClassFixture<FilesApiFactory>
{
    private sealed record FileDto(string Name, long SizeBytes, DateTimeOffset UploadedAt, List<string> UsedBy);
    private sealed record UploadResultDto(string Name, bool Succeeded, string? Error);

    [Theory]
    [InlineData("GET", "")]
    [InlineData("DELETE", "/something.jar")]
    public async Task AViewer_IsRefused(string method, string suffix)
    {
        var client = await factory.SignedInAsAsync(UserRole.Viewer);

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), $"/api/files{suffix}"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UploadThenList_RoundTrips()
    {
        var client = await factory.SignedInAsAsync(UserRole.Admin);
        var name = $"roundtrip-{Guid.NewGuid():N}.jar";

        var uploaded = await UploadAsync(client, name, "hello"u8.ToArray());
        Assert.True(uploaded.Single().Succeeded);

        var files = await client.GetFromJsonAsync<List<FileDto>>("/api/files");
        var file = files!.Single(f => f.Name == name);
        Assert.Equal(5, file.SizeBytes);

        await client.DeleteAsync($"/api/files/{name}");
    }

    [Fact]
    public async Task UploadingTheSameNameTwice_IsReportedAsAFailure_NotAServerError()
    {
        var client = await factory.SignedInAsAsync(UserRole.Admin);
        var name = $"conflict-{Guid.NewGuid():N}.jar";

        var first = await UploadAsync(client, name, "one"u8.ToArray());
        Assert.True(first.Single().Succeeded);

        var second = await UploadAsync(client, name, "two"u8.ToArray());
        Assert.False(second.Single().Succeeded);
        Assert.Contains("already exists", second.Single().Error);

        await client.DeleteAsync($"/api/files/{name}");
    }

    [Fact]
    public async Task ANonJarExtension_IsRefused()
    {
        var client = await factory.SignedInAsAsync(UserRole.Admin);

        var results = await UploadAsync(client, "not-a-jar.txt", "x"u8.ToArray());

        Assert.False(results.Single().Succeeded);
        Assert.Contains("not an accepted file type", results.Single().Error);
    }

    [Fact]
    public async Task DeletingAMissingFile_Returns404()
    {
        var client = await factory.SignedInAsAsync(UserRole.Admin);

        var response = await client.DeleteAsync("/api/files/does-not-exist.jar");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeletingAFileStillNamedByADriver_IsRefused_UnlessForced()
    {
        var client = await factory.SignedInAsAsync(UserRole.Admin);
        await UploadAsync(client, FilesApiFactory.JarName, "pretend"u8.ToArray());

        var files = await client.GetFromJsonAsync<List<FileDto>>("/api/files");
        var file = files!.Single(f => f.Name == FilesApiFactory.JarName);
        Assert.Contains(FilesApiFactory.DriverId, file.UsedBy);

        var refused = await client.DeleteAsync($"/api/files/{FilesApiFactory.JarName}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        var forced = await client.DeleteAsync($"/api/files/{FilesApiFactory.JarName}?force=true");
        Assert.Equal(HttpStatusCode.NoContent, forced.StatusCode);
    }

    private static async Task<List<UploadResultDto>> UploadAsync(HttpClient client, string fileName, byte[] content)
    {
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(content);
        form.Add(part, "files", fileName);

        var response = await client.PostAsync("/api/files", form);
        var body = await response.Content.ReadFromJsonAsync<List<UploadResultDto>>();
        return body!;
    }
}
