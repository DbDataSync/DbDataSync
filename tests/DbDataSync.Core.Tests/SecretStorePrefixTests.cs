using ClrKernel.Core.Secrets;
using DbDataSync.Core.Secrets;

namespace DbDataSync.Core.Tests;

/// <summary>
/// Phase 93: <c>ClrKernel.Core.Secrets</c> 0.9.2 → 0.11.0 added a <c>prefix</c> constructor parameter to
/// <see cref="SecretStore"/>, and both places this app constructs one (<c>DbDataSyncHost.cs</c>'s
/// composition root and <c>DbDataSync.TaskRunner/Program.cs</c>'s entry point — both now write
/// <c>new SecretStore("DbDataSync", true)</c>, identical expressions) now pass <c>"DbDataSync"</c>
/// rather than leaving it unset, which would have kept resolving under the package's own default
/// ("ClrKernel") branding.
/// <para>
/// These tests exercise that exact construction expression rather than DI-wiring either composition
/// root, since it is literally the same call in both places — there is no separate logic path per site
/// to duplicate here.
/// </para>
/// </summary>
public sealed class SecretStorePrefixTests
{
    [Fact]
    public void NewSecretStore_WithDbDataSyncPrefix_ReportsThatPrefix()
    {
        var store = new SecretStore("DbDataSync", true);

        Assert.Equal("DbDataSync", store.Prefix);
    }

    [Fact]
    public void EnvName_UsesTheDbDataSyncPrefix_NotThePackagesClrKernelDefault()
    {
        var store = new SecretStore("DbDataSync", true);
        var secretRef = SecretRefs.ForConnection("orders-db");

        var envName = store.EnvName(secretRef);

        // Not an unchanged assertion passing by accident: a store built with SecretPrefix.Default (i.e.
        // one nobody configured a prefix for) would produce this exact old hardcoded name instead, since
        // this app's own SecretRefs.EnvironmentVariableFor (now retired) hand-rolled precisely this
        // "ClrKernel"-branded folding before this phase.
        Assert.StartsWith("DBDATASYNC_SECRET_", envName);
        Assert.DoesNotContain("CLRKERNEL", envName);
    }

    [Fact]
    public void EnvName_ForAConnectionSecretRef_ComposesBothPrefixesWithoutConflatingThem()
    {
        // Two different prefixes in play: SecretRefs.ForConnection's own "dbdatasync:" ref-namespacing
        // (phase 92, unrelated to this phase) and SecretStore's new provider-naming "DbDataSync" prefix
        // (this phase). Composing them is expected to repeat "DBDATASYNC" — that repetition is inherent
        // to the two prefixes serving different purposes, not a bug this phase should paper over.
        var store = new SecretStore("DbDataSync", true);
        var secretRef = SecretRefs.ForConnection("foo");

        Assert.Equal("dbdatasync:connection:foo", secretRef);
        Assert.Equal("DBDATASYNC_SECRET_DBDATASYNC_CONNECTION_FOO", store.EnvName(secretRef));
    }

    [Fact]
    public void ForProviders_WithDbDataSyncPrefix_ReportsThatPrefix()
    {
        // The overload TestApiFactory and ConfigRepositoryTests use to swap in an in-memory-only store
        // for tests — has to carry the same prefix as production, or a test's env-var fallback would be
        // computed under the wrong name than the real composition root would ever look for.
        var store = SecretStore.ForProviders("DbDataSync", [new InMemorySecretProvider()]);

        Assert.Equal("DbDataSync", store.Prefix);
        Assert.Equal(
            "DBDATASYNC_SECRET_DBDATASYNC_CONNECTION_FOO",
            store.EnvName(SecretRefs.ForConnection("foo")));
    }
}
