namespace DbDataSync.Cli.Tests;

/// <summary>
/// Records what <see cref="SystemdService"/> would have done, without ever touching this host's real
/// <c>/etc/systemd/system</c>, its real user database, or its real systemd — see
/// <see cref="ISystemdEnvironment"/>'s own doc comment for why that matters here specifically (unlike
/// most fakes in this test suite, the real implementation genuinely runs on this Linux sandbox, so the
/// seam is what keeps a test from mutating the actual machine it runs on).
/// </summary>
internal sealed class FakeSystemdEnvironment : ISystemdEnvironment
{
    public List<string> ExistingUsers { get; } = [];
    public List<string> CreatedUsers { get; } = [];
    public (string Path, string Content)? WrittenUnit { get; private set; }
    public List<string> DeletedUnitPaths { get; } = [];
    public List<(string Path, string User, string Group)> ChownCalls { get; } = [];
    public List<string[]> SystemctlCalls { get; } = [];

    public int CreateSystemUserResult { get; set; }
    public int SystemctlResult { get; set; }

    public bool UserExists(string user) => ExistingUsers.Contains(user);

    public int CreateSystemUser(string user)
    {
        CreatedUsers.Add(user);
        return CreateSystemUserResult;
    }

    public void WriteUnitFile(string path, string content) => WrittenUnit = (path, content);

    public void DeleteUnitFile(string path) => DeletedUnitPaths.Add(path);

    public void ChownRecursive(string path, string user, string group) => ChownCalls.Add((path, user, group));

    public int RunSystemctl(params string[] args)
    {
        SystemctlCalls.Add(args);
        return SystemctlResult;
    }
}
