using ClrKernel.Core.Secrets;
using DbDataSync.Core.Secrets;

namespace DbDataSync.State;

public sealed partial class StateDatabase
{
    /// <summary>
    /// The one place that turns "which engine, which connection string" into an open
    /// <see cref="StateDatabase"/> — used by <c>DbDataSyncHost.cs</c> (the API's own startup) and
    /// <c>InviteCommand</c> alike, so the two never drift on how a non-SQLite state store is reached.
    /// <para>
    /// Before phase 79, <c>InviteCommand</c> didn't make this decision at all — it called
    /// <c>new StateDatabase(stateDb)</c> unconditionally, so an admin locked out of a
    /// <see cref="StateEngineIds.MsSql"/> or <see cref="StateEngineIds.Postgres"/> deployment had no way to
    /// mint a recovery invite. Extracting the branch <c>DbDataSyncHost.cs</c> already had, rather than
    /// writing a second one, is what makes that fix safe.
    /// </para>
    /// <para>
    /// Lives here rather than in <c>DbDataSync.Api</c> (where <c>ApiOptions</c> is defined) because
    /// <c>DbDataSync.Api</c> already depends on <c>DbDataSync.State</c> for <see cref="StateDatabase"/>
    /// itself — an <c>ApiOptions</c> parameter here would be a dependency cycle. Takes the same values
    /// as fields instead; <c>ClrKernel.Core.Secrets</c> is already on this project's graph transitively
    /// (via the <c>DbDataSync.Core</c> project reference), so nothing new is added to
    /// <c>DbDataSync.State.csproj</c> for this.
    /// </para>
    /// </summary>
    public static StateDatabase FromOptions(
        string engine, string stateDbPath, string? stateConnectionString, SecretStore secrets)
    {
        // SQLite keeps its own constructor and its own setting, so a deployment that has never heard
        // of phase 63 (or phase 79) reaches exactly the code it always did.
        if (engine == StateEngineIds.Sqlite)
            return new StateDatabase(stateDbPath);

        if (stateConnectionString is null)
            throw new InvalidOperationException(
                $"DbDataSync:StateEngine is '{engine}', which needs DbDataSync:StateConnectionString. " +
                "Only SQLite is configured by path.");

        // The password never lives in the connection string that gets configured — it is resolved
        // through the secret store under the one fixed ref phase 79 documents, and spliced on here at
        // connect time, the same splice-at-connect-time shape DriverConnectionFactory already uses for
        // a connection's own CredentialSecretRef. Appending rather than parsing the string apart:
        // SqlClient and Npgsql both tolerate a trailing ";Password=..." segment, and neither engine's
        // connection-string syntax lets a later key lose to an earlier one of the same name.
        var connectionString = secrets.TryResolve(SecretRefs.ForAppSetting("stateConnectionString"), out var password)
            ? $"{stateConnectionString};Password={password}"
            : stateConnectionString;

        return new StateDatabase(engine, connectionString);
    }
}
