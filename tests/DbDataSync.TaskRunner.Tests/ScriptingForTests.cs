using DbDataSync.Core.Config;
using DbDataSync.Scripting;

namespace DbDataSync.TaskRunner.Tests;

internal static class Scripting
{
    /// <summary>
    /// A real host over a scratch cache directory. Nothing in these tests binds a script, so it never
    /// compiles anything — but the executor resolves through it on every mapping, so a null would only
    /// prove the tests avoid the code path.
    /// </summary>
    public static ScriptHost ForTests(ConfigRepository configRepository, string repoRoot) =>
        new(configRepository, new ScriptCompiler(new ScriptCacheDirectory(Path.Combine(repoRoot, "script-cache"))));
}
