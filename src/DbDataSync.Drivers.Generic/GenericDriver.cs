using System.Data.Common;
using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;

namespace DbDataSync.Drivers.Generic;

/// <summary>
/// An <see cref="IDriver"/> built entirely from a <see cref="GenericDriverSpec"/> — no engine-specific
/// C#. <see cref="Drivers.Postgres.PostgresDriver"/>'s own doc comment already said a new engine is "a
/// dialect, a connection factory and a catalog"; this is that claim made into a class anyone can
/// construct, rather than a new driver project per engine that happens to need nothing but the generic
/// pipeline. It is what phase 109d's YAML descriptor deserialises into, and what a hand-written
/// registration (this phase) can already stand up today.
/// </summary>
public sealed class GenericDriver(GenericDriverSpec spec)
    : GenericDriverBase<GenericDriverSpec>(spec, spec.ValueBinder ?? new GenericValueBinder(spec.Dialect, spec.ProviderFactory))
{
    public int? DefaultPort => Spec.DefaultPort;

    /// <summary>
    /// Assembles the connection string through a plain <see cref="DbConnectionStringBuilder"/> —
    /// key/value pairs by name, not a provider-typed builder — using the key spellings
    /// <see cref="GenericDriverSpec.ConnectionStringKeys"/> declares. The credential goes on top of an
    /// operator-supplied connection string exactly as every compiled driver does: config never carries
    /// it, and it is escaped correctly by going through the builder rather than being concatenated.
    /// </summary>
    public override DbConnection CreateConnection(ConnectionConfig connection, string? credential)
    {
        var keys = Spec.ConnectionStringKeys;
        var builder = new DbConnectionStringBuilder();
        if (!string.IsNullOrWhiteSpace(connection.ConnectionString))
            builder.ConnectionString = connection.ConnectionString;
        else
            builder[keys.Host] = connection.Host;

        builder[keys.Database] = connection.Database ?? Spec.DefaultDatabase;

        if (connection.Port is int port && keys.Port is not null)
            builder[keys.Port] = port;

        if (connection.ConnectTimeoutSeconds is int connectTimeout)
            builder[keys.ConnectTimeout] = connectTimeout;
        else if (!ConnectionTimeouts.AddressCarriesOwnConnectTimeout(connection, keys.ConnectTimeout))
            builder[keys.ConnectTimeout] = ConnectionTimeouts.DefaultConnectSeconds;

        if (connection.AuthMode == AuthMode.None)
        {
            // Whatever the address or the environment provides. DbDataSync adds nothing.
        }
        else if (connection.AuthMode == AuthMode.IntegratedAuth)
        {
            if (keys.IntegratedSecurity is not null)
            {
                builder[keys.IntegratedSecurity] = true;
            }
            else
            {
                // No integrated-auth flag on this engine (Postgres-style GSSAPI/peer auth): a username
                // is still required, DbDataSync supplies nothing else.
                builder[keys.Username] = connection.UserId
                    ?? throw new InvalidOperationException($"UserId is required even for IntegratedAuth on '{Spec.Id}'.");
            }
        }
        else
        {
            builder[keys.Username] = connection.UserId
                ?? throw new InvalidOperationException("UserId is required for SqlAuth connections.");
            builder[keys.Password] = credential
                ?? throw new InvalidOperationException("A resolved credential is required for SqlAuth connections.");
        }

        foreach (var (key, value) in connection.Properties)
            builder[key] = value;

        var providerConnection = Spec.ProviderFactory.CreateConnection()
            ?? throw new InvalidOperationException($"The provider factory for '{Spec.Id}' did not produce a connection.");
        providerConnection.ConnectionString = builder.ConnectionString;
        return providerConnection.WithCommandTimeout(connection);
    }

    /// <summary>
    /// The ADO.NET-standard <c>Databases</c> schema collection, which every provider built on
    /// <see cref="DbConnection.GetSchema(string)"/> implements — the one catalog operation
    /// <c>information_schema</c> itself cannot answer (it describes the *current* database, not the
    /// server). Column name varies by provider (<c>database_name</c> is Npgsql's; SqlClient's is the
    /// same), so this reads the schema table's first column rather than naming one.
    /// </summary>
    public override async Task<IReadOnlyList<string>> ListDatabasesAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var schema = await connection.GetSchemaAsync("Databases", cancellationToken);
        var results = new List<string>();
        foreach (System.Data.DataRow row in schema.Rows)
            results.Add(Convert.ToString(row[0]) ?? "");
        return results;
    }

    protected override Task SwitchDatabaseAsync(DbConnection connection, string database, CancellationToken cancellationToken) =>
        Spec.Dialect.UseDatabaseAsync(connection, database, cancellationToken);
}
