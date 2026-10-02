namespace DbDataSync.Api.Configuration;

/// <summary>
/// Which ASP.NET Core server hosts the console/API. <see cref="Kestrel"/> is the default everywhere;
/// <see cref="HttpSys"/> is Windows-only and exists for one reason — HTTP.sys is the kernel-mode
/// listener, so several processes can share a port, told apart by host header.
/// </summary>
public enum WebServer { Kestrel, HttpSys }

/// <summary>
/// Reads <c>DbDataSync:App:Server</c>. Strict where the other mode strings are lenient
/// (<see cref="Auth.ConfigEnum"/> falls back on anything it cannot parse): a typo here would quietly
/// start Kestrel, and the symptom — "port already in use" against whatever the operator meant to share
/// the port with — points nowhere near the setting.
/// </summary>
public static class WebServerSelection
{
    public const string ConfigKey = "DbDataSync:App:Server";
    public const WebServer Default = WebServer.Kestrel;

    public static WebServer Resolve(IConfiguration configuration)
    {
        var value = configuration[ConfigKey];
        if (string.IsNullOrWhiteSpace(value))
            return Default;

        if (Enum.TryParse<WebServer>(value.Trim(), ignoreCase: true, out var server) && Enum.IsDefined(server))
            return server;

        throw new InvalidOperationException(
            $"'{ConfigKey}' is '{value}', which is not a web server this build knows. Use 'kestrel' or 'httpsys'.");
    }
}
