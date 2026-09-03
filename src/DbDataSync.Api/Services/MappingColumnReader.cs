using DbDataSync.Core.Config;

namespace DbDataSync.Api.Services;

/// <summary>
/// Reads one side of a mapping's catalog and says either what is there or why it could not look —
/// the single introspection path behind both <see cref="MappingMetadataService"/>'s Refresh and the
/// capture <c>TableMappingsController.BulkCreate</c> performs as it creates.
/// <para>
/// It goes through <see cref="IColumnCatalog"/> rather than a driver catalog of its own, which is
/// what makes every one of those answers the same answer the column-mapping picker shows: one path,
/// so a connection with a bound <c>metadataProvider</c> script is honoured everywhere, and a cache
/// cannot disagree with the editor that populated it.
/// </para>
/// </summary>
public sealed class MappingColumnReader(IColumnCatalog metadata)
{
    /// <summary>
    /// A table that is not in the catalog is a normal state here, not a fault: the target of a
    /// mapping whose provisioning has yet to run genuinely has no columns, and the source of a
    /// query-configured reader has no catalog entry at all. Both come back as a stated reason.
    /// </summary>
    public async Task<SideRead> ReadAsync(TableRef table, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(table.Table))
            return Unreadable("This side names no table — a query source has no catalog to read.");

        try
        {
            var columns = await metadata.ListColumnsAsync(
                table.ConnectionName, table.Database, table.Schema, table.Table, cancellationToken);

            return new SideRead(
                columns.Select(c => new CachedColumn(c.Name, c.NativeType, c.IsNullable, c.IsPrimaryKey, c.IsIdentity))
                    .ToList(),
                null);
        }
        // Deliberately everything except cancellation. A caller's contract here is that a side which
        // cannot be read is a stated reason and never a failure, and the ways a catalog read can fail
        // are an open set: a connection that is not configured (FileNotFoundException), a login that
        // was refused or a host that is down (the driver's own DbException), a bound metadataProvider
        // script that threw (ScriptExecutionException), a driver that names no dialect for one to use
        // (InvalidOperationException). Enumerating them would mean the next kind nobody thought of
        // takes down a forty-table bulk create instead of annotating one line of its result.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Unreadable(ex.Message);
        }
    }

    public static SideRead Unreadable(string reason) => new(null, reason);
}

/// <param name="Columns">Null when the side could not be read at all — distinct from an empty list,
/// which is the catalog answering, and answering that there is no such table. See
/// <see cref="IsMissingTable"/>.</param>
public sealed record SideRead(List<CachedColumn>? Columns, string? Unavailable)
{
    /// <summary>
    /// The catalog was reached and reported nothing, which means the table is not there.
    /// <para>
    /// A read rather than a guess: no engine has a table with no columns — <c>CREATE TABLE</c> needs
    /// at least one and dropping the last one is refused — so an empty answer from a catalog query
    /// that itself succeeded can only mean the table does not exist. This is how the target of a
    /// not-yet-provisioned mapping presents, and it is much the commoner case than
    /// <see cref="Unavailable"/>: <c>MsSqlSchemaQueries.GetColumnsAsync</c> and its equivalents select
    /// from the catalog views and return no rows rather than raising.
    /// </para>
    /// </summary>
    public bool IsMissingTable => Columns is { Count: 0 };

    /// <summary>The columns to work from, or null when there are none to be had — a side that could
    /// not be read and a table that is not there are the same thing to a caller that wanted its
    /// shape.</summary>
    public List<CachedColumn>? Shape => IsMissingTable ? null : Columns;
}

/// <summary>
/// Pairs a source's columns with a target's by name, which is what a mapping created without anyone
/// naming columns has to start from.
/// <para>
/// **This is deliberately the same rule as the "Auto-map by name" button in
/// <c>ColumnMappingEditor.tsx</c>** (see <c>autoMap</c> there, and the note on that component about
/// what a not-yet-created target's columns are). A mapping created in bulk must arrive with the
/// columns an operator would have got by pressing that button, not with a second, quietly different
/// notion of what auto-mapping means. The two cannot share code across the language boundary, so they
/// are kept honest by <c>ColumnAutoMapTests</c> and by this comment pointing at each other.
/// </para>
/// </summary>
public static class ColumnAutoMap
{
    /// <param name="target">Null when the target's shape is not available — either it could not be
    /// read, or it is not there yet because provisioning has not run. Both mean the same thing here,
    /// and the editor is explicit about what that thing is: the target's columns *are* the source's,
    /// because <c>ProvisioningService</c> builds its <c>CREATE TABLE</c> from the column mappings, so
    /// what is mapped is literally what gets created.</param>
    public static List<ColumnMapping> Between(
        IReadOnlyList<CachedColumn>? source, IReadOnlyList<CachedColumn>? target)
    {
        if (source is null || source.Count == 0)
            return [];

        var sourceNames = source.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

        // Ordinally, as the editor's Set of source names is. Two columns differing only in case are
        // two columns to every engine this runs against, and treating them as one would map a value
        // into a column nobody chose.
        return (target is null || target.Count == 0 ? source : target)
            .Where(c => sourceNames.Contains(c.Name))
            .Select(c => new ColumnMapping { SourceColumn = c.Name, TargetColumn = c.Name })
            .ToList();
    }
}
