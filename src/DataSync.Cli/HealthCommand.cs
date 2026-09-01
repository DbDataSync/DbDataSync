using DataSync.Core.Config;

namespace DataSync.Cli;

/// <summary>
/// Asks a running DataSync whether it is answering, and exits 0 or 1.
/// <para>
/// It exists because a container health check has to be *something in the image*, and the ASP.NET
/// runtime image has no curl and no bash — the first version of this was a shell script using
/// <c>/dev/tcp</c>, which is a bash feature, run under <c>/bin/sh</c>, which is dash on Debian. It
/// failed every time and reported a perfectly healthy container as unhealthy, which is how an
/// orchestrator gets told to restart something that was working.
/// </para>
/// <para>
/// Doing it in the tool needs no package to keep patched and behaves the same on any base image.
/// </para>
/// </summary>
public static class HealthCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        // Falls back to a resolved config's DataSync:Url before the hardcoded container default, so
        // `datasync health` against a config-backed install doesn't need --url repeated on every call.
        // The container's own HEALTHCHECK always passes --url explicitly (no config file to find inside
        // the image's working directory), so it is unaffected either way.
        var root = DataSyncRoot.Resolve(args);
        var url = CliOptions.Read(args, "--url")
            ?? DataSyncConfigFile.Read(root).GetValueOrDefault("DataSync:Url")
            ?? "http://127.0.0.1:8080";
        var endpoint = $"{url.TrimEnd('/')}/api/health";

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            var response = await client.GetAsync(endpoint);
            if (response.IsSuccessStatusCode)
                return 0;

            Console.Error.WriteLine($"{endpoint} answered {(int)response.StatusCode}.");
            return 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine($"{endpoint} did not answer: {ex.Message}");
            return 1;
        }
    }
}
