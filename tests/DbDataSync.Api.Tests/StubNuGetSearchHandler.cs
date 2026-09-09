namespace DbDataSync.Api.Tests;

/// <summary>A settable stand-in for the real NuGet search index, wired into
/// <see cref="LibrarySearchApiFactory"/> in place of the real network call — <see cref="Respond"/> is
/// changed per test, and throwing from it stands in for a timeout/connection failure exactly as
/// <c>HttpClient</c> itself would surface one.</summary>
public sealed class StubNuGetSearchHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
        _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("""{"data":[]}"""),
        };

    public bool WasCalled { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        WasCalled = true;
        return Task.FromResult(Respond(request));
    }
}
