namespace DbDataSync.Drivers.Descriptor;

/// <summary>
/// The shape of a <c>&lt;repo&gt;/drivers/&lt;id&gt;/driver.yaml</c> file, exactly as YamlDotNet
/// deserialises it — property names camelCased to match the file, no derived state. <see cref="DriverDescriptorReader"/>
/// turns this into what a <c>GenericDriver</c> actually needs.
/// </summary>
public sealed class DriverDescriptorYaml
{
    public required string Id { get; set; }
    public required string DisplayName { get; set; }

    /// <summary>The id of the <c>&lt;repo&gt;/libraries/&lt;id&gt;/library.json</c> this descriptor
    /// resolves its <see cref="System.Data.Common.DbProviderFactory"/> through — the library carries
    /// its own <c>factoryType</c> and package list, so the descriptor names it rather than repeating
    /// them.</summary>
    public required string Library { get; set; }

    public required DescriptorDialectYaml Dialect { get; set; }

    /// <summary>Native type name (with its own <c>(p,s)</c>-style placeholder args, e.g.
    /// <c>"decimal(p,s)"</c>) → canonical mapping. A name with no parenthesised part
    /// (<c>int</c>, <c>json</c>) takes no arguments.</summary>
    public Dictionary<string, TypeMapEntryYaml> TypeMap { get; set; } = new();

    public required DescriptorCapabilitiesYaml Capabilities { get; set; }

    /// <summary>Required when <see cref="DescriptorDialectYaml.Catalog"/> is <c>query</c> — phase 167V.
    /// Ignored otherwise.</summary>
    public MetadataQueriesYaml? MetadataQueries { get; set; }
}

/// <param name="QuoteIdentifier">backtick | doubleQuote | bracket</param>
/// <param name="RowLimit">limitOffset | offsetFetch</param>
/// <param name="Catalog">informationSchema | query — phase 167V added <c>query</c>, an operator's own
/// SQL (see <see cref="MetadataQueriesYaml"/>), for a vendor whose catalog fits neither
/// <c>information_schema</c> nor (for a JDBC-backed engine specifically) <c>DatabaseMetaData</c>.</param>
/// <param name="DefaultDatabase">What a connection assembled from host/port (no explicit database)
/// connects to before <c>UseDatabaseAsync</c> switches it, or when the engine doesn't support
/// switching at all. Not in the plan doc's worked example, which showed no connection assembly at
/// all — added here because <c>GenericDriverSpec</c> genuinely needs one (SQL Server has
/// <c>master</c>, Postgres has <c>postgres</c>; MySQL has no equivalent notion, so this defaults to
/// empty, which every provider tested here accepts as "no initial database").</param>
/// <param name="ConnectionStringKeys">Per-engine connection-string key spellings (Npgsql's <c>Timeout</c>
/// vs SqlClient's <c>Connect Timeout</c>) — also not in the worked example, also genuinely needed;
/// omitted entirely to fall back to <c>GenericConnectionStringKeys</c>'s SqlClient-shaped defaults.</param>
public sealed class DescriptorDialectYaml
{
    public required string QuoteIdentifier { get; set; }
    public required string ParameterPrefix { get; set; }
    public required string RowLimit { get; set; }
    public required string Catalog { get; set; }
    public bool SupportsChangeDatabase { get; set; } = true;
    public string DefaultDatabase { get; set; } = "";
    public int? DefaultPort { get; set; }
    public DescriptorConnectionStringKeysYaml? ConnectionStringKeys { get; set; }
}

public sealed class DescriptorConnectionStringKeysYaml
{
    public string Host { get; set; } = "Host";
    public string? Port { get; set; } = "Port";
    public string Database { get; set; } = "Database";
    public string Username { get; set; } = "User Id";
    public string Password { get; set; } = "Password";
    public string ConnectTimeout { get; set; } = "Connect Timeout";
    public string? IntegratedSecurity { get; set; }
}

public sealed class DescriptorCapabilitiesYaml
{
    public List<string> Readers { get; set; } = [];
    public List<string> Staging { get; set; } = [];
    public List<string> Writers { get; set; } = [];
}

/// <summary>
/// The <c>catalog: query</c> escape hatch's own SQL — phase 167V. <c>{{schema}}</c>/<c>{{table}}</c> in
/// <see cref="ColumnQuery"/>'s text are substituted with a quoted string literal before execution (a
/// value being compared, e.g. <c>WHERE table_schema = {{schema}}</c> — not an identifier to quote);
/// <see cref="TableQuery"/> takes neither (it lists every table, the same shape
/// <c>InformationSchemaQueries.ListTablesAsync</c> already has). Row shapes — which columns are
/// required, which are optional and what they default to when missing — are
/// <c>DbDataSync.Drivers.Generic.QueryTableRow</c>/<c>QueryColumnRow</c>'s own doc comments.
/// </summary>
public sealed class MetadataQueriesYaml
{
    public required string TableQuery { get; set; }
    public required string ColumnQuery { get; set; }
}

/// <summary>
/// One <c>typeMap</c> value — either a bare kind name (<c>datetime: Timestamp</c>) or a mapping
/// spelling out <see cref="Precision"/>/<see cref="Scale"/>/<see cref="Length"/>/<see cref="Max"/>/
/// <see cref="Unicode"/>, whose values are either a literal (a fixed decimal(10,2)) or one of this
/// entry's own key's placeholder names — see <see cref="DescriptorDialect.ToCanonicalType"/>.
/// A custom <c>IYamlTypeConverter</c> (<see cref="TypeMapEntryYamlConverter"/>) reads both forms into
/// this one class rather than needing two.
/// </summary>
public sealed class TypeMapEntryYaml
{
    public required string Kind { get; set; }
    public string? Precision { get; set; }
    public string? Scale { get; set; }
    public string? Length { get; set; }
    public bool Max { get; set; }
    public bool Unicode { get; set; }
}
