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
    /// them. Ignored by a JDBC-backed <see cref="Base"/> (<c>DbDataSync.Drivers.Jdbc.JdbcGenericDriver</c>
    /// resolves its own <c>ikvm</c> requirement independently of this field) — still worth setting to
    /// <c>ikvm</c> there for a human reading the file, even though nothing reads it.</summary>
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

    /// <summary>
    /// Which class to build this descriptor into — an assembly-qualified type name (the same shape
    /// <c>library.json</c>'s own <c>factoryType</c> already uses), resolved via reflection to a public
    /// static <c>FromDescriptor(DriverDescriptorYaml)</c> method. Omitted (the common case) defaults to
    /// <c>DbDataSync.Drivers.Generic.GenericDriver</c>, built the existing way (through
    /// <see cref="Library"/> and a <see cref="System.Data.Common.DbProviderFactory"/>) — every
    /// pre-phase-168V <c>driver.yaml</c> keeps working unchanged. Phase 168V's other kind is
    /// <c>DbDataSync.Drivers.Jdbc.JdbcGenericDriver, DbDataSync.Drivers.Jdbc</c>, which reads
    /// <see cref="Jdbc"/> instead of <see cref="Library"/>.
    /// </summary>
    public string? Base { get; set; }

    /// <summary>Required when <see cref="Base"/> names a JDBC-backed driver. Ignored otherwise.</summary>
    public JdbcDescriptorYaml? Jdbc { get; set; }

    /// <summary>
    /// The query a "Test Connection" run executes to show a small sample of live data — distinct from
    /// the driver's own reachability probe (always a fixed, permission-free round trip; see
    /// <c>GenericDriverBase.TestAsync</c>'s own <c>SELECT 1</c>). Null falls back to
    /// <c>GenericDriverBase&lt;TSpec&gt;.DefaultTestQuery</c>'s own default (<c>SELECT 1</c>) — a
    /// connection can still set its own <c>ConnectionConfig.TestQuery</c> to override this per
    /// connection rather than per driver.
    /// </summary>
    public string? TestQuery { get; set; }
}

/// <param name="DriverClass">The JDBC driver's fully-qualified Java class name —
/// <c>org.postgresql.Driver</c>, for pgJDBC.</param>
/// <param name="DriverJarPaths">Phase 169V — one or more **names inside <c>&lt;repo&gt;/files/</c>**, not
/// filesystem paths (<c>architecture/planning/todo/user-provided-files-store.md</c>). More than one jar
/// is a real, not exotic, shape — Oracle's wallet support ships across four jars, Db2 ships a separate
/// license jar. <c>DbDataSync.Drivers.Jdbc.JdbcGenericDriver.FromDescriptor</c> resolves each name against
/// the store before building a <c>JdbcDriverSpec</c>.</param>
public sealed class JdbcDescriptorYaml
{
    public required string DriverClass { get; set; }
    // List<string>, not IReadOnlyList<string> — YamlDotNet's default node deserializer can't construct
    // an interface type directly, the same reason DescriptorCapabilitiesYaml's own list fields below are
    // concrete too. Found live (YamlException: "No node deserializer was able to deserialize..."), not
    // assumed from the other class's own precedent alone.
    public required List<string> DriverJarPaths { get; set; }

    /// <summary>Phase 178N — e.g. <c>"jdbc:postgresql://{host}:{port}/{database}"</c>. Omitted means a
    /// connection must supply the whole JDBC URL itself via <c>ConnectionString</c> (the pre-178N
    /// contract); see <c>JdbcGenericDriver.BuildUnifiedJdbcUrlAndProperties</c>'s own doc comment for how
    /// the four supported placeholders are resolved. Never write <c>{password}</c> here — it is never
    /// substituted, and <c>JdbcGenericDriver</c>'s constructor rejects a template that contains it.</summary>
    public string? UrlTemplate { get; set; }

    /// <summary>Phase 178N. **Not** <see cref="DescriptorConnectionStringKeysYaml"/> — that type was tried
    /// first and found wrong here (not assumed): its properties carry non-nullable, ADO.NET-flavoured C#
    /// defaults (<c>Host = "Host"</c>, etc.), so a *partial* override — a yaml setting only
    /// <c>username</c>, the exact shape phase 179N's own form writes for a single-field override — would
    /// deserialize with every other field already populated at its ADO.NET default rather than left
    /// unset, silently applying the wrong key spelling to the ones the operator never touched. Every
    /// field of <see cref="JdbcConnectionStringKeysYaml"/> is genuinely nullable with no default, so
    /// <c>JdbcGenericDriver.FromDescriptor</c> can tell "not set" from "set to that string" per field and
    /// fall back to <c>JdbcGenericDriver.DefaultConnectionStringKeys</c> (<c>host</c>/<c>port</c>/
    /// <c>database</c>/<c>user</c>/<c>password</c>) one field at a time.</summary>
    public JdbcConnectionStringKeysYaml? ConnectionStringKeys { get; set; }
}

/// <summary>See <see cref="JdbcDescriptorYaml.ConnectionStringKeys"/>'s own doc comment for why this
/// isn't <see cref="DescriptorConnectionStringKeysYaml"/> — every field here is nullable with no default,
/// on purpose.</summary>
public sealed class JdbcConnectionStringKeysYaml
{
    public string? Host { get; set; }
    public string? Port { get; set; }
    public string? Database { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? ConnectTimeout { get; set; }
    public string? IntegratedSecurity { get; set; }
}

/// <param name="QuoteIdentifier">backtick | doubleQuote | bracket</param>
/// <param name="RowLimit">limitOffset (<c>LIMIT n</c>) | offsetFetch (ANSI <c>FETCH FIRST n ROWS ...</c>)
/// | topN (<c>TOP (n) ...</c> at the front, SQL-Server/Sybase-style) — see
/// <see cref="DbDataSync.Core.Sql.RowLimitStyle"/>.</param>
/// <param name="Catalog">Omitted (or <c>default</c>) | <c>query</c> — phase 168V. Omitted means this
/// descriptor's <c>base</c> kind's own default catalog: <c>information_schema</c> for
/// <c>GenericDriver</c>, <c>java.sql.DatabaseMetaData</c> for <c>JdbcGenericDriver</c>. <c>query</c> (see
/// <see cref="MetadataQueriesYaml"/>) is an operator's own SQL, for a vendor whose default doesn't
/// fit — phase 167V.</param>
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
    public string? Catalog { get; set; }
    public bool SupportsChangeDatabase { get; set; } = true;
    public string DefaultDatabase { get; set; } = "";
    public int? DefaultPort { get; set; }
    public DescriptorConnectionStringKeysYaml? ConnectionStringKeys { get; set; }

    /// <summary>
    /// False for every ADO.NET provider so far (SqlClient/Npgsql/MySqlConnector all match a
    /// <c>DbParameter.ParameterName</c> carrying <paramref name="ParameterPrefix"/>'s own sigil against
    /// the marker in the rendered SQL text themselves). True for a JDBC-backed engine: a JDBC
    /// <c>PreparedStatement</c> has only ordinal <c>?</c> placeholders, so the command layer itself does
    /// the name→position translation (matching <c>@name</c> markers) and needs the bare name to compare
    /// against — see phase 165V's Finding 1.
    /// <para>
    /// **Ignored, not just defaulted, for a JDBC-backed <c>base</c>** — <c>JdbcGenericDriver.FromDescriptor</c>
    /// forces this true regardless of what a <c>driver.yaml</c> sets, because it isn't a real per-driver
    /// choice: <c>JdbcCommand</c>'s name→position translation always looks up the bare name, for every
    /// JDBC vendor, unconditionally. Originally left as an opt-in, default-<c>false</c> flag an operator
    /// had to remember to set — which produced exactly the bug this paragraph now prevents: an
    /// unsegmented read binds no parameters and never touches this path, so a descriptor-driven JDBC
    /// driver missing this looked correct until the first bulk load with a segmenting strategy bound a
    /// range/list parameter and failed with "CommandText references parameter '@segMin' with no matching
    /// entry in Parameters" — a confusing, several-layers-removed error for what was really a missing
    /// one-line dialect setting. Still a real field (an ADO.NET-backed descriptor still reads it
    /// normally); only the JDBC path no longer trusts it.
    /// </para>
    /// </summary>
    public bool ParameterNameIsBare { get; set; }

    /// <summary>
    /// Whether this engine's <see cref="RowLimit"/> syntax can express "and every row sharing the
    /// boundary value too" — see <see cref="DbDataSync.Core.Sql.SqlDialect.RenderTieSafeRowLimit"/>'s own
    /// doc comment for why that's the whole point of a bounded read. Null (the default, and every
    /// descriptor written before this field existed) means "use <see cref="RowLimit"/>'s own
    /// conventional default": true for <c>offsetFetch</c>/<c>topN</c> (matching every dialect in this
    /// codebase that speaks either), false for <c>limitOffset</c> (no engine here has a tie-safe
    /// <c>LIMIT</c> variant — see <c>MySqlDialect</c>'s own doc comment). Set explicitly only to state a
    /// real exception to that default — an <c>offsetFetch</c> engine whose <c>FETCH FIRST</c> doesn't
    /// actually support <c>WITH TIES</c> despite being otherwise ANSI-shaped, say.
    /// </summary>
    public bool? SupportsTieSafeRowLimit { get; set; }
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
