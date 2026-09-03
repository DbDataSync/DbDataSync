using System.Data.Common;
using System.Runtime.CompilerServices;
using DbDataSync.Core.Config;

namespace DbDataSync.Core.Sql;

/// <summary>
/// The resolved connect and command timeouts for a source or target connection, and the one place a
/// command gets stamped with the second of them.
/// <para>
/// Connect timeout needs no help from here beyond <see cref="ResolveConnectSeconds"/> — it is a
/// connection-string key, so each driver sets it on its own builder. Command timeout is a runtime
/// property on <see cref="DbCommand"/> for both providers, so there is nowhere to put it *once* unless
/// something carries it from the config to every command. That is what this does.
/// </para>
/// <para>
/// The carrier is a <see cref="ConditionalWeakTable{TKey,TValue}"/> keyed by the connection instance,
/// stamped by the driver in <c>CreateConnection</c> — where the <see cref="ConnectionConfig"/> is
/// resolved and the connection is born, and the only moment both are in the same hand. The alternative
/// was threading an <c>int</c> through every reader, writer, staging provider and provisioner
/// signature in the driver projects, which is the same edit as setting the timeout at each call site
/// and buys nothing. Weak keys mean this holds nothing alive: the entry dies with the connection.
/// </para>
/// <para>
/// **The state store is deliberately not a client of this.** <c>DbDataSync.State</c> owns an internal,
/// single-writer store this process talks to over a local hop, a different risk profile from a network
/// hop to a database somebody else operates, and out of scope per the planning doc.
/// </para>
/// </summary>
public static class ConnectionTimeouts
{
    /// <summary>
    /// What an unset <see cref="ConnectionConfig.ConnectTimeoutSeconds"/> means. Deliberately not each
    /// provider's own 15s: connecting is the step that fails on a busy or distant instance, and 15s has
    /// been the cause of enough spurious failures elsewhere that doubling it is the safer default to
    /// pick while we are here choosing one at all.
    /// </summary>
    public const int DefaultConnectSeconds = 30;

    /// <summary>
    /// What an unset <see cref="ConnectionConfig.CommandTimeoutSeconds"/> means. Both providers default
    /// to 30 seconds, which is a reasonable number for an interactive query and a badly wrong one for
    /// the work this tool does — a snapshot read, a MERGE over a staged batch or a provisioning
    /// statement can legitimately run for many minutes, and 30s turns that into a torn run. 30 minutes
    /// is long enough not to interrupt real work and short enough to still be a limit.
    /// </summary>
    public const int DefaultCommandSeconds = 1800;

    /// <summary>
    /// Keyed by connection instance, so a command can find the timeout its connection was configured
    /// with without the caller passing one. <c>StrongBox</c> because the table stores reference types.
    /// </summary>
    private static readonly ConditionalWeakTable<DbConnection, StrongBox<int>> Stamped = new();

    /// <summary>The connect timeout to put on a connection-string builder: the configured value if
    /// there is one, else <see cref="DefaultConnectSeconds"/>. <c>0</c> passes straight through and
    /// means unlimited to both providers — no sentinel translation, because none is needed.</summary>
    public static int ResolveConnectSeconds(ConnectionConfig connection) =>
        connection.ConnectTimeoutSeconds ?? DefaultConnectSeconds;

    /// <summary>
    /// Whether the operator's own connection string already sets a connect timeout, under any of the
    /// spellings <paramref name="keys"/> gives — which is what decides whether our default applies or
    /// defers.
    /// <para>
    /// A plain <see cref="DbConnectionStringBuilder"/> rather than each provider's typed one, because
    /// the typed builders answer the wrong question: <c>NpgsqlConnectionStringBuilder.ContainsKey</c>
    /// and <c>SqlConnectionStringBuilder.ShouldSerialize</c> both report <c>true</c> for a known key
    /// that nobody set, so "did they say so?" always came back yes and the default never applied. The
    /// untyped builder holds only the keys actually present in the string, which is the question.
    /// </para>
    /// </summary>
    public static bool AddressCarriesOwnConnectTimeout(ConnectionConfig connection, params string[] keys)
    {
        if (string.IsNullOrWhiteSpace(connection.ConnectionString))
            return false;

        // ContainsKey is case- and whitespace-insensitive here, so "connect timeout" matches too.
        var supplied = new DbConnectionStringBuilder { ConnectionString = connection.ConnectionString };
        return keys.Any(supplied.ContainsKey);
    }

    /// <summary>As <see cref="ResolveConnectSeconds"/>, for command timeout.</summary>
    public static int ResolveCommandSeconds(ConnectionConfig connection) =>
        connection.CommandTimeoutSeconds ?? DefaultCommandSeconds;

    /// <summary>
    /// Records what every command on this connection should time out after. Called by a driver's
    /// <c>CreateConnection</c> on the connection it just built.
    /// </summary>
    /// <returns>The same connection, so a driver can <c>return</c> through this in one expression.</returns>
    public static T WithCommandTimeout<T>(this T connection, ConnectionConfig config) where T : DbConnection
    {
        Stamped.AddOrUpdate(connection, new StrongBox<int>(ResolveCommandSeconds(config)));
        return connection;
    }

    /// <summary>
    /// What the connection was stamped with, or <see cref="DefaultCommandSeconds"/> for a connection
    /// that never passed through a driver — a test's hand-built connection, mostly. Exposed for the
    /// tests that assert the stamp arrived.
    /// </summary>
    public static int CommandTimeoutOf(DbConnection connection) =>
        Stamped.TryGetValue(connection, out var seconds) ? seconds.Value : DefaultCommandSeconds;

    /// <summary>
    /// <see cref="DbConnection.CreateCommand"/>, plus the timeout the connection was opened with.
    /// **Every source/target command in <c>src/</c> goes through this rather than
    /// <c>CreateCommand()</c>** — that is the whole mechanism. A new reader or writer that uses it gets
    /// the operator's configured timeout by construction; one that calls <c>CreateCommand()</c> quietly
    /// gets the provider's 30 seconds, which is the failure this replaced.
    /// </summary>
    public static DbCommand CreateTimedCommand(this DbConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandTimeout = CommandTimeoutOf(connection);
        return command;
    }
}
