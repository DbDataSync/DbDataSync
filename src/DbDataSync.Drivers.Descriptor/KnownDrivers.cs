namespace DbDataSync.Drivers.Descriptor;

/// <param name="Id">A stable id an operator names with <c>config driver install --from &lt;id&gt;</c>,
/// or the web console's one-click "add" (phase 120's <c>POST /api/drivers/from-catalog</c>) names as
/// <c>knownDriverId</c>.</param>
/// <param name="DisplayName">The descriptor's own <c>displayName</c> default when an install doesn't
/// override it.</param>
/// <param name="Description">One line, for a picker (phase 118's <c>GET /api/known-drivers</c>).</param>
/// <param name="BoundLibraryId">The <c>DbDataSync.Libraries.KnownLibraries</c> id this entry's
/// descriptor resolves its factory through — this project doesn't reference
/// <c>DbDataSync.Libraries</c>'s types to render one (only a plain id string), keeping the two
/// catalogs decoupled at the type level; <c>KnownDriversCatalogTests</c> is what checks the id
/// actually resolves.</param>
/// <param name="ResourceName">The embedded <c>Resources/&lt;ResourceName&gt;</c> file holding this
/// entry's <c>dialect</c>/<c>typeMap</c>/<c>capabilities</c> body — everything a <c>driver.yaml</c>
/// needs except <c>id</c>/<c>displayName</c>/<c>library</c>, which <see cref="KnownDrivers.Render"/>
/// fills in from the install itself.</param>
public sealed record KnownDriverEntry(string Id, string DisplayName, string Description, string BoundLibraryId, string ResourceName);

/// <summary>
/// Curated, ready-made <c>driver.yaml</c> bodies for <c>config driver install --from &lt;id&gt;</c> and
/// the web console's one-click "add" — a dialect and type map someone has already worked out for a
/// common engine, so an operator adding it edits rather than writes from a blank file. One entry ships
/// in v1, seeded from the plan doc's own worked example; the structure is built to grow.
/// </summary>
public static class KnownDrivers
{
    public static readonly IReadOnlyList<KnownDriverEntry> All =
    [
        new(
            "mysql.generic", "MySQL / MariaDB (generic)",
            "Watermark and batch-reload replication for MySQL or MariaDB, over MySqlConnector.",
            "mysql-connector", "mysql.generic.driver.yaml"),
        new(
            "mssql.odbc", "SQL Server (ODBC)",
            "Watermark and batch-reload replication for SQL Server over System.Data.Odbc, for an " +
                "environment that reaches it through an installed ODBC driver rather than " +
                "Microsoft.Data.SqlClient.",
            "system-data-odbc", "mssql.odbc.driver.yaml"),
    ];

    public static KnownDriverEntry? TryGetById(string id) =>
        All.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The full <c>driver.yaml</c> text for <paramref name="entry"/> — the caller-supplied
    /// <c>id</c>/<c>displayName</c>/<c>library</c> header, followed by the entry's embedded
    /// dialect/typeMap/capabilities body verbatim.</summary>
    public static string Render(KnownDriverEntry entry, string id, string displayName, string libraryId) =>
        $"""
        id: {id}
        displayName: {displayName}
        library: {libraryId}

        """ + ReadBody(entry);

    private static string ReadBody(KnownDriverEntry entry)
    {
        var assembly = typeof(KnownDrivers).Assembly;
        var resourceName = $"{assembly.GetName().Name}.Resources.{entry.ResourceName}";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found in '{assembly.GetName().Name}'.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
