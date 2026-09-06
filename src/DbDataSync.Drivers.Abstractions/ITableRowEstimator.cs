using System.Data.Common;
using DbDataSync.Core.Config;

namespace DbDataSync.Drivers.Abstractions;

/// <summary>
/// Opt-in capability for a driver that can put an approximate row count on one of its tables from the
/// engine's own catalog statistics — <c>sys.partitions</c>, <c>pg_class.reltuples</c> — rather than a
/// <c>COUNT(*)</c> scan.
/// <para>
/// Same shape as <see cref="IConnectionTester"/> and <see cref="ISegmentExpandingReader"/>: a driver
/// reaching an arbitrary engine through ODBC has no catalog it can name, so callers ask
/// <c>driver is ITableRowEstimator</c> rather than forcing every driver to implement (or throw from)
/// a method it cannot honour.
/// </para>
/// <para>
/// The number is a denominator for a progress readout, nothing that a decision hangs on. It can be
/// stale, and it counts the whole table — a caller applying a row filter has to say so itself.
/// </para>
/// </summary>
public interface ITableRowEstimator
{
    /// <summary>
    /// The engine's current row-count estimate for <paramref name="table"/>, or null when the engine
    /// has none yet (a table that has never been analysed) or the table cannot be resolved. Must not
    /// throw for either — an absent estimate is an answer.
    /// </summary>
    Task<long?> EstimateRowCountAsync(DbConnection connection, TableRef table, CancellationToken cancellationToken);
}
