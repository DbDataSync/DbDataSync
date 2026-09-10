namespace DbDataSync.Cli.Tests;

/// <summary>
/// Records what <see cref="ToolCommand"/> would have done, without ever touching this host's real
/// <c>/usr/local/bin</c>, its real Machine <c>PATH</c>, or <c>/etc/paths.d</c> — see
/// <see cref="IToolPathEnvironment"/>'s own doc comment for why that matters here specifically (unlike
/// most fakes in this test suite, the real Unix implementation genuinely runs on this Linux sandbox, so
/// the seam is what keeps a test from mutating the actual machine it runs on).
/// </summary>
internal sealed class FakeToolPathEnvironment : IToolPathEnvironment
{
    public bool IsElevated { get; set; } = true;
    public Dictionary<string, string> Symlinks { get; } = [];
    public List<string> ChmodCalls { get; } = [];
    public string? MachinePath { get; set; } = "";
    public Dictionary<string, string> PathsDFiles { get; } = [];

    public string? ReadSymlinkTarget(string path) => Symlinks.GetValueOrDefault(path);

    public void CreateSymlink(string path, string target) => Symlinks[path] = target;

    public void RemoveSymlink(string path) => Symlinks.Remove(path);

    public void Chmod(string path) => ChmodCalls.Add(path);

    public string? ReadMachinePath() => MachinePath;

    public void WriteMachinePath(string value) => MachinePath = value;

    public string? ReadPathsD(string path) => PathsDFiles.GetValueOrDefault(path);

    public void WritePathsD(string path, string content) => PathsDFiles[path] = content;

    public void DeletePathsD(string path) => PathsDFiles.Remove(path);
}
