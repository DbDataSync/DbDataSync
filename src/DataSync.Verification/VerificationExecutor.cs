using System.Data.Common;
using DataSync.Core.Config;
using DataSync.Drivers.Generic;

namespace DataSync.Verification;

/// <summary>One side's connection, dialect and table, as the executor needs them.</summary>
public sealed record VerificationEndpoint(DbConnection Connection, SqlDialect Dialect, string Schema, string Table);

/// <summary>
/// Runs one check against both sides and compares the answers.
/// <para>
/// The two queries run **one after the other, source first**, and each records when it ran. They are
/// deliberately not concurrent: a check is a diagnostic, not a hot path, and two reads a known
/// distance apart are easier to reason about than two that overlapped by an unknown amount. The gap is
/// reported so an operator can weigh a difference against it.
/// </para>
/// </summary>
public static class VerificationExecutor
{
    public static async Task<VerificationResult> RunAsync(
        VerificationCheckConfig check,
        IReadOnlyList<ColumnMapping> columnMappings,
        VerificationEndpoint source,
        VerificationEndpoint target,
        CancellationToken cancellationToken)
    {
        var (sourceSql, targetSql) = BuildStatements(check, columnMappings, source, target);

        var (sourceSide, sourceRead) = await ReadAsync(source.Connection, sourceSql, check, cancellationToken);
        var (targetSide, targetRead) = await ReadAsync(target.Connection, targetSql, check, cancellationToken);

        return new VerificationResult(
            check.Name,
            check.GroupBy,
            MeasureNames(check),
            check.DifferenceThreshold,
            sourceRead,
            targetRead,
            VerificationComparison.Compare(
                sourceSide with { ExecutedAtUtc = sourceRead },
                targetSide with { ExecutedAtUtc = targetRead },
                check.DifferenceThreshold));
    }

    /// <summary>
    /// What each side is asked. A built-in generates both from one declaration; a
    /// <see cref="VerificationCheckKind.Sql"/> check uses the operator's own statement — the same one
    /// on both sides when they wrote only one, which is their judgement that the engines are close
    /// enough here rather than something inferred for them.
    /// </summary>
    public static (string Source, string Target) BuildStatements(
        VerificationCheckConfig check,
        IReadOnlyList<ColumnMapping> columnMappings,
        VerificationEndpoint source,
        VerificationEndpoint target)
    {
        switch (check.Kind)
        {
            case VerificationCheckKind.RowCount:
                return (
                    VerificationStatement.BuildRowCount(
                        source.Dialect, source.Schema, source.Table,
                        VerificationStatement.ResolveSource(source.Dialect, columnMappings, check.GroupBy), check.Filter),
                    VerificationStatement.BuildRowCount(
                        target.Dialect, target.Schema, target.Table,
                        VerificationStatement.ResolveTarget(target.Dialect, check.GroupBy), check.Filter));

            case VerificationCheckKind.Sum:
                return (
                    VerificationStatement.BuildSum(
                        source.Dialect, source.Schema, source.Table,
                        VerificationStatement.ResolveSource(source.Dialect, columnMappings, check.GroupBy),
                        VerificationStatement.ResolveSource(source.Dialect, columnMappings, check.Measures), check.Filter),
                    VerificationStatement.BuildSum(
                        target.Dialect, target.Schema, target.Table,
                        VerificationStatement.ResolveTarget(target.Dialect, check.GroupBy),
                        VerificationStatement.ResolveTarget(target.Dialect, check.Measures), check.Filter));

            case VerificationCheckKind.Sql:
                if (string.IsNullOrWhiteSpace(check.SourceSql))
                    throw new ConfigValidationException($"Verification check '{check.Name}' has no SQL.");
                return (check.SourceSql, check.TargetSql ?? check.SourceSql);

            default:
                throw new ConfigValidationException(
                    $"Verification check '{check.Name}' is of kind '{check.Kind}', which nothing runs yet.");
        }
    }

    /// <summary>
    /// A row count's measure has no column to be named after, so it is fixed and both sides agree on
    /// it. Everything else is named by what the check declared.
    /// </summary>
    private static IReadOnlyList<string> MeasureNames(VerificationCheckConfig check) =>
        check.Kind == VerificationCheckKind.RowCount ? [VerificationStatement.RowCountColumn] : check.Measures;

    private static async Task<(VerificationSide Side, DateTimeOffset ReadAt)> ReadAsync(
        DbConnection connection, string sql, VerificationCheckConfig check, CancellationToken cancellationToken)
    {
        var readAt = DateTimeOffset.UtcNow;

        using var command = connection.CreateCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var groups = new List<IReadOnlyList<string>>();
        var measures = new List<IReadOnlyDictionary<string, double>>();
        var measureNames = MeasureNames(check);

        while (await reader.ReadAsync(cancellationToken))
        {
            groups.Add([.. check.GroupBy.Select(name => VerificationComparison.GroupValue(Read(reader, name)))]);

            var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var measure in measureNames)
            {
                // A measure the statement did not return is absent rather than zero. Zero is a number
                // somebody measured; this is a check whose SQL does not match what it declared.
                if (Read(reader, measure) is { } value and not DBNull)
                    values[measure] = Convert.ToDouble(value);
            }
            measures.Add(values);
        }

        return (new VerificationSide(groups, measures, readAt), readAt);
    }

    /// <summary>
    /// By name, and null when the statement did not return the column. A hand-written check whose SQL
    /// disagrees with what it declared is an operator error, and it should surface as a missing value
    /// rather than an index out of range.
    /// </summary>
    private static object? Read(DbDataReader reader, string column)
    {
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (string.Equals(reader.GetName(i), column, StringComparison.OrdinalIgnoreCase))
                return reader.IsDBNull(i) ? null : reader.GetValue(i);
        }
        return null;
    }
}
