using System.Net;
using DbDataSync.Core.Config;

namespace DbDataSync.Cli.Tests;

/// <summary>
/// <c>dbdatasync health</c> falls back to a resolved config's <c>DbDataSync:App:Url</c> when <c>--url</c> isn't
/// passed, before falling back to the hardcoded container default — phase 79.
/// </summary>
public sealed class HealthCommandTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-health-tests-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task NoUrlFlag_ConfiguredUrlInFile_IsUsed()
    {
        using var server = new AnsweringServer();
        DbDataSyncConfigFile.SetValue(_root, "DbDataSync:App", "Url", server.Url);

        var exitCode = await RunHealthAsync(["--repo", _root]);

        Assert.Equal(0, exitCode);
        Assert.True(server.WasHit);
    }

    [Fact]
    public async Task UrlFlag_WinsOverTheConfiguredUrl()
    {
        using var server = new AnsweringServer();
        DbDataSyncConfigFile.SetValue(_root, "DbDataSync:App", "Url", "http://127.0.0.1:1"); // nothing listens here

        var exitCode = await RunHealthAsync(["--repo", _root, "--url", server.Url]);

        Assert.Equal(0, exitCode);
        Assert.True(server.WasHit);
    }

    private static Task<int> RunHealthAsync(string[] args) => HealthCommand.RunAsync(args);

    /// <summary>A real loopback HTTP listener answering /api/health — exercising HealthCommand's actual
    /// HttpClient call rather than mocking it away.</summary>
    private sealed class AnsweringServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        public bool WasHit { get; private set; }
        public string Url { get; }

        public AnsweringServer()
        {
            var port = GetFreePort();
            Url = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = ServeAsync(_cts.Token);
        }

        private async Task ServeAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var context = await _listener.GetContextAsync();
                    WasHit = true;
                    context.Response.StatusCode = 200;
                    context.Response.Close();
                }
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested || _cts.IsCancellationRequested)
            {
                // Listener stopped while awaiting a context — expected on Dispose.
            }
        }

        private static int GetFreePort()
        {
            using var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream,
                System.Net.Sockets.ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)socket.LocalEndPoint!).Port;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _listener.Close();
        }
    }
}
