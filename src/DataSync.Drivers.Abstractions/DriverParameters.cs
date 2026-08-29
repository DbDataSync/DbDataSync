using DataSync.Core.Config;

namespace DataSync.Drivers.Abstractions;

/// <summary>Parameter declarations shared across drivers, so the same setting is not described three
/// slightly different ways.</summary>
public static class DriverParameters
{
    /// <summary>Values keys, so the form, the validator and the drivers agree on one spelling.</summary>
    public const string AddressMode = "addressMode";
    public const string Host = "host";
    public const string Port = "port";
    public const string ConnectionString = "connectionString";
    public const string Database = "database";
    public const string AuthMode = "authMode";
    public const string UserId = "userId";
    public const string Password = "password";
    public const string Properties = "properties";

    public const string HostAddressing = "host";
    public const string ConnectionStringAddressing = "connectionString";

    private const string ConnectionCard = "";
    private const string AuthCard = "Authentication";

    /// <summary>
    /// The free-form bag appended to a connection string. Every driver has had one since phase 3;
    /// declaring it here is what turns it from something the connection screen assumes into something
    /// a driver states — and therefore something a driver could narrow, or drop, or replace with
    /// named settings of its own.
    /// </summary>
    public static ParameterDescriptor ConnectionProperties { get; } = new()
    {
        Name = Properties,
        Label = "Custom properties",
        Description = "Appended to the connection string. Engine-specific settings this driver does not name.",
        Type = ParameterType.Property,
        Cardinality = ParameterCardinality.Any,
        Layout = new ParameterLayout("Driver settings"),
    };

    /// <summary>
    /// Everything a connection takes, with visibility already worked out from
    /// <paramref name="values"/>.
    /// <para>
    /// Shared rather than restated per driver because both engines answer these questions identically
    /// — how you address a database and how you authenticate to it are not where MsSql and Postgres
    /// differ. What does differ is the default port, which is the argument. A driver that genuinely
    /// needs a different set overrides
    /// <see cref="IDriver.ConnectionParameters(IReadOnlyDictionary{string, string})"/> and says so.
    /// </para>
    /// <para>
    /// **Name and driver type are deliberately absent.** A connection's name is its identity, not one
    /// of its settings, and the driver is the selector that decides which set of settings applies —
    /// it cannot be declared by the thing it selects.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ParameterDescriptor> ForConnection(
        IReadOnlyDictionary<string, string> values, int? defaultPort)
    {
        // Absent means "as a new connection starts", so an empty bag answers the same question a fresh
        // form asks. Without this the first thing a form ever sees would be a connection with no
        // addressing mode at all, which is not a state any connection is in.
        var byHost = Value(values, AddressMode, HostAddressing) != ConnectionStringAddressing;
        var sqlAuth = Value(values, AuthMode, nameof(Core.Config.AuthMode.SqlAuth)) == nameof(Core.Config.AuthMode.SqlAuth);

        return
        [
            new ParameterDescriptor
            {
                Name = AddressMode,
                Label = "Address",
                Type = ParameterType.Dropdown,
                DropdownOptions = [HostAddressing, ConnectionStringAddressing],
                DropdownLabels = new Dictionary<string, string>
                {
                    [HostAddressing] = "Host & port",
                    [ConnectionStringAddressing] = "Connection string",
                },
                Default = HostAddressing,
                // The two settings anything else depends on, and the only two that make a form ask
                // again. Everything below defaults to false and costs nothing.
                Recalc = true,
                Layout = new ParameterLayout(ConnectionCard),
            },
            new ParameterDescriptor
            {
                Name = Host,
                Label = "Host",
                // Hidden rather than disabled in the other mode: a greyed-out Host beside a connection
                // string invites the question of which one is being used.
                Visible = byHost,
                Layout = new ParameterLayout(ConnectionCard, "address", 3),
            },
            new ParameterDescriptor
            {
                Name = Port,
                Label = "Port",
                Type = ParameterType.Number,
                Default = defaultPort?.ToString(),
                Visible = byHost,
                Layout = new ParameterLayout(ConnectionCard, "address"),
            },
            new ParameterDescriptor
            {
                Name = ConnectionString,
                Label = "Connection string",
                Description =
                    "The credential is never part of this — it is kept in the secret store and applied " +
                    "when connecting, because config is committed to git.",
                Visible = !byHost,
                Layout = new ParameterLayout(ConnectionCard),
            },
            new ParameterDescriptor
            {
                Name = Database,
                Label = "Database",
                Layout = new ParameterLayout(ConnectionCard),
            },
            new ParameterDescriptor
            {
                Name = AuthMode,
                Label = "Auth mode",
                Type = ParameterType.Dropdown,
                DropdownOptions =
                [
                    nameof(Core.Config.AuthMode.SqlAuth),
                    nameof(Core.Config.AuthMode.IntegratedAuth),
                    nameof(Core.Config.AuthMode.None),
                ],
                DropdownLabels = new Dictionary<string, string>
                {
                    [nameof(Core.Config.AuthMode.SqlAuth)] = "SQL Auth",
                    [nameof(Core.Config.AuthMode.IntegratedAuth)] = "Integrated Auth",
                    [nameof(Core.Config.AuthMode.None)] = "None — supplied by the address or environment",
                },
                Default = nameof(Core.Config.AuthMode.SqlAuth),
                Recalc = true,
                Layout = new ParameterLayout(AuthCard),
            },
            new ParameterDescriptor
            {
                Name = UserId,
                Label = "User ID",
                Visible = sqlAuth,
                Layout = new ParameterLayout(AuthCard),
            },
            new ParameterDescriptor
            {
                Name = Password,
                Label = "Password",
                Type = ParameterType.Secret,
                Visible = sqlAuth,
                Layout = new ParameterLayout(AuthCard),
            },
            ConnectionProperties,
        ];
    }

    /// <summary>
    /// A connection as the flat values bag <see cref="ForConnection"/> and
    /// <see cref="ParameterValidation"/> read.
    /// <para>
    /// **The password is not in it.** Only the two <c>Recalc</c> settings actually change the answer,
    /// and a bag that carries a plaintext credential is a bag that ends up in a log line or an error
    /// message eventually. Leaving it out costs nothing and closes that off.
    /// </para>
    /// </summary>
    public static Dictionary<string, string> ValuesOf(ConnectionInput input)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AddressMode] = input.ConnectionString is null ? HostAddressing : ConnectionStringAddressing,
            [AuthMode] = input.AuthMode.ToString(),
        };

        Set(values, Host, input.Host);
        Set(values, Port, input.Port?.ToString());
        Set(values, ConnectionString, input.ConnectionString);
        Set(values, Database, input.Database);
        Set(values, UserId, input.UserId);

        foreach (var (key, value) in input.Properties)
            values[ParameterValidation.KeyFor(ConnectionProperties, key)] = value;

        return values;
    }

    private static void Set(Dictionary<string, string> values, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            values[name] = value;
    }

    private static string Value(IReadOnlyDictionary<string, string> values, string name, string fallback) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
}
