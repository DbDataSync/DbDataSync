using System.Runtime.CompilerServices;

namespace DbDataSync.Cli.Tests;

/// <summary>
/// These tests exercise <c>dbdatasync config secret set|list|remove</c> (and <c>invite</c>, which reads a
/// secret back) through the real <c>SecretCommand</c>, which builds
/// <c>new SecretStore("DbDataSync", true)</c>. On a machine with no OS credential store — every CI
/// runner, and this repo's own dev container — <c>SecretStore.Store</c> writes to the chain's
/// in-memory cache, which a <em>fresh</em> instance (a later <c>SecretCommand.Run</c> call, or
/// <c>dbdatasync invite</c>'s own store) cannot read. Any test that checks a secret survives across
/// instances then fails.
/// <para>
/// Pointing <c>DBDATASYNC_SECRETS_FILE</c> at a scratch JSON file puts ClrKernel's
/// <c>FileSecretProvider</c> — "the only writable store a container has" — into the chain ahead of
/// the read-only environment provider, which is exactly the fallback it exists for. One file for the
/// whole assembly; each test still removes its own refs.
/// </para>
/// </summary>
internal static class CliTestSecretsFile
{
    [ModuleInitializer]
    internal static void Configure()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dbdatasync-cli-tests-secrets-{Guid.NewGuid():N}.json");
        Environment.SetEnvironmentVariable("DBDATASYNC_SECRETS_FILE", path);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { File.Delete(path); }
            catch { /* a scratch file under the temp dir — best-effort cleanup */ }
        };
    }
}
