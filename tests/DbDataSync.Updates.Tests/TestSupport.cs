using System.Net;
using System.Text;

namespace DbDataSync.Updates.Tests;

/// <summary>What a request looked like when it was sent — captured eagerly, because the code under test
/// disposes the message the moment it has its response.</summary>
internal sealed record SentRequest(Uri Uri, string? Authorization, string? UserAgent, string? Accept);

internal sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<SentRequest> Sent { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Sent.Add(new SentRequest(
            request.RequestUri!,
            request.Headers.Authorization?.ToString(),
            request.Headers.UserAgent.ToString(),
            request.Headers.Accept.ToString()));
        return Task.FromResult(respond(request));
    }

    public HttpClient Client() => new(this);
}

internal static class Http
{
    public static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Bytes(byte[] body) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    public static HttpResponseMessage Status(HttpStatusCode status) => new(status);
}

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dbdatasync-updates-" + Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal static class Fixture
{
    public static string Read(string name) => File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}
