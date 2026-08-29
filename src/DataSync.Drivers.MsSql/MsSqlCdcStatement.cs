namespace DataSync.Drivers.MsSql;

/// <summary>
/// Builds the SQL Server CDC read. Separated from the reader, like
/// <see cref="MsSqlChangeTrackingStatement"/>, so the select list and the boundary arithmetic — the
/// two parts with real defects available in them — can be asserted without a live SQL Server.
/// </summary>
public static class MsSqlCdcStatement
{
    /// <summary>The change-table columns every CDC function returns ahead of the source's own.</summary>
    public const string OperationColumn = "__$operation";

    /// <summary>Fixed leading ordinal: the operation, then the mapped columns.</summary>
    public const int OperationOrdinal = 0;

    /// <summary>
    /// The two shapes CDC offers, and the reason the reader prefers one.
    /// <para>
    /// <c>fn_cdc_get_net_changes_*</c> collapses a key's changes within the window to a single row —
    /// the semantics Change Tracking has, and the ones a mirror wants: a row updated fifty times
    /// between passes is one row to write, not fifty. It exists only when the capture instance was
    /// created with <c>@supports_net_changes = 1</c>, which needs a primary key.
    /// </para>
    /// <para>
    /// <c>fn_cdc_get_all_changes_*</c> is always there and reports every intermediate change. It is
    /// the fallback, and the reader logs when it is used, because a pass reading fifty rows to write
    /// one is a thing an operator should be able to find out about.
    /// </para>
    /// </summary>
    public enum CdcFunction
    {
        NetChanges,
        AllChanges,
    }

    /// <summary>
    /// The read.
    /// <para>
    /// <c>sys.fn_cdc_increment_lsn</c> on the lower bound is not a nicety. CDC's window functions are
    /// **inclusive of <c>@from</c>**, so passing the stored LSN unchanged re-reads the last pass's
    /// final change on every pass. It is harmless to the data — writers upsert — and it makes every
    /// pass look like it found work, which is exactly how a monitoring graph starts lying.
    /// </para>
    /// </summary>
    /// <param name="captureInstance">
    /// The capture instance, not the table: CDC names its functions after the instance
    /// (<c>dbo_Orders</c> by default), a table can have two, and they can be named anything. Composed
    /// into the function name because a function name is not something SQL Server lets you
    /// parameterise — which is why <see cref="MsSqlCdcCatalog"/> reads it from the catalog rather than
    /// accepting it from config.
    /// </param>
    public static string BuildRead(
        string captureInstance, CdcFunction function, IReadOnlyList<string> columns,
        Func<string, string>? renderColumn = null)
    {
        renderColumn ??= c => SqlIdentifier.Quote(c);

        var selected = new List<string> { OperationColumn };
        selected.AddRange(columns.Select(renderColumn));

        var name = function == CdcFunction.NetChanges
            ? $"cdc.fn_cdc_get_net_changes_{captureInstance}"
            : $"cdc.fn_cdc_get_all_changes_{captureInstance}";

        // The two functions do not return the same bookkeeping columns: __$seqval orders changes
        // *within* a transaction and only all-changes has it, because net changes has already
        // collapsed them. Ordering by it unconditionally is an "Invalid column name" on the mode this
        // reader prefers — found by the integration tests, and not by reading the documentation.
        var order = function == CdcFunction.NetChanges
            ? "__$start_lsn"
            : "__$start_lsn, __$seqval";

        // 'all' for net changes means "give me the net row and tell me the operation"; 'all' for all
        // changes means "every change, without before-images". Same literal, and both are what this
        // reader wants — before-images are phase 32's explicit non-goal.
        return $"""
            DECLARE @from binary(10) = sys.fn_cdc_increment_lsn(@storedLsn);
            SELECT {string.Join(", ", selected)}
            FROM {name}(@from, @toLsn, N'all')
            ORDER BY {order};
            """;
    }

    /// <summary>
    /// The first pass, before any position exists: every row, as an insert — the same shape Change
    /// Tracking's first pass takes, and for the same reason. CDC's change table only holds what has
    /// happened since capture was enabled, so a table that already had rows would otherwise start
    /// half-replicated with nothing to say so.
    /// </summary>
    public static string BuildFullLoad(string schema, string table, string projection, string? filter) =>
        MsSqlChangeTrackingStatement.BuildFullLoad(schema, table, projection, filter);

    /// <summary>
    /// <c>__$operation</c>: 1 delete, 2 insert, 3 update-before, 4 update-after.
    /// <para>
    /// 3 only appears with <c>'all update old'</c>, which this reader does not ask for — so seeing one
    /// means the statement was not the one built here, and guessing at it would be worse than saying
    /// so.
    /// </para>
    /// </summary>
    public static ChangeOperationCode Operation(int code) => code switch
    {
        1 => ChangeOperationCode.Delete,
        2 => ChangeOperationCode.Insert,
        4 => ChangeOperationCode.Update,
        3 => throw new InvalidOperationException(
            "CDC returned an update-before image (__$operation = 3), which this reader never requests. " +
            "The statement that produced it was not the one this reader builds."),
        _ => throw new InvalidOperationException($"Unknown CDC __$operation value '{code}'."),
    };

    public enum ChangeOperationCode
    {
        Insert,
        Update,
        Delete,
    }
}
