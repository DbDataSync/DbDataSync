using DbDataSync.Api.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// <c>DbDataSync:App:Server</c> — Kestrel by default, HTTP.sys opt-in and Windows only. See
/// <see cref="WebServerSelection"/> for why an unknown value is refused rather than ignored.
/// </summary>
public sealed class WebServerSelectionTests : IDisposable
{
    private readonly string _repoRoot = Directory.CreateTempSubdirectory("dbdatasync-webserver-tests-").FullName;

    public void Dispose() => Directory.Delete(_repoRoot, recursive: true);

    private static IConfiguration Config(string? value) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(value is null ? [] : new Dictionary<string, string?> { [WebServerSelection.ConfigKey] = value })
            .Build();

    [Fact]
    public void Unset_IsKestrel() =>
        Assert.Equal(WebServer.Kestrel, WebServerSelection.Resolve(Config(null)));

    [Theory]
    [InlineData("httpsys", WebServer.HttpSys)]
    [InlineData("HttpSys", WebServer.HttpSys)]
    [InlineData(" HTTPSYS ", WebServer.HttpSys)]
    [InlineData("kestrel", WebServer.Kestrel)]
    [InlineData("", WebServer.Kestrel)]
    public void KnownValues_AreCaseInsensitive(string value, WebServer expected) =>
        Assert.Equal(expected, WebServerSelection.Resolve(Config(value)));

    [Theory]
    [InlineData("http.sys")]
    [InlineData("iis")]
    [InlineData("7")]
    public void UnknownValues_AreRefusedNamingTheKey(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => WebServerSelection.Resolve(Config(value)));
        Assert.Contains(WebServerSelection.ConfigKey, ex.Message);
    }

    private string[] BaseArgs(params string[] extra) =>
    [
        "--DbDataSync:App:RepoRoot", _repoRoot,
        "--DbDataSync:State:DbPath", Path.Combine(_repoRoot, "state.db"),
        "--DbDataSync:App:TaskRunnerDllPath", Path.Combine(_repoRoot, "DbDataSync.TaskRunner.dll"),
        "--DbDataSync:Auth:Network:Admin", "loopback",
        .. extra,
    ];

    [NonWindowsFact]
    public void HttpSysOffWindows_FailsAtStartupSayingSo()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => DbDataSyncHost.Build(BaseArgs("--DbDataSync:App:Server", "httpsys")));

        Assert.Contains("only exists on Windows", ex.Message);
    }

    [Fact]
    public void KestrelIsTheDefaultHost()
    {
        using var app = DbDataSyncHost.Build(BaseArgs());

        Assert.Equal(WebServer.Kestrel, app.Services.GetRequiredService<ApiOptions>().Server);
    }

    /// <summary>
    /// Builds (does not start) the host under HTTP.sys with Windows sign-in configured, and checks the
    /// auth bridge: a scheme under Negotiate's own name that forwards to HTTP.sys's handler, so the
    /// sign-in endpoint's attribute is unchanged. Windows only — nothing else has HTTP.sys to resolve.
    /// <para>
    /// The forward target is checked against the real <c>HttpSysDefaults.AuthenticationScheme</c>, not
    /// against the scheme provider: HTTP.sys adds its scheme when the server is constructed at startup,
    /// which a built-but-unstarted host has not done. (An earlier version of this bridge forwarded to a
    /// name written from memory and nothing noticed until this ran on Windows.)
    /// </para>
    /// </summary>
    [WindowsOnlyFact]
    [Trait("Category", "Windows")]
    public async Task HttpSysOnWindows_BridgesNegotiateToTheHttpSysScheme()
    {
        using var app = DbDataSyncHost.Build(BaseArgs(
            "--DbDataSync:App:Server", "httpsys", "--DbDataSync:Auth:Windows:AdminGroup", "DbDataSync Admins"));

        var negotiate = await app.Services.GetRequiredService<IAuthenticationSchemeProvider>().GetSchemeAsync("Negotiate");
        Assert.NotNull(negotiate);

        var forwardsTo = app.Services.GetRequiredService<IOptionsMonitor<PolicySchemeOptions>>().Get("Negotiate").ForwardDefault;
        var real = (string?)System.Reflection.Assembly.Load("Microsoft.AspNetCore.Server.HttpSys")
            .GetType("Microsoft.AspNetCore.Server.HttpSys.HttpSysDefaults")!
            .GetField("AuthenticationScheme")!.GetRawConstantValue();

        Assert.Equal(real, forwardsTo);
        Assert.Equal(real, HttpSysHosting.AuthenticationScheme);
    }
}
