using System.Data.Common;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.Scripting.Abstractions;
using DbDataSync.Core.Sql;

namespace DbDataSync.Verification;

/// <summary>One side's connection, dialect and table, as the executor needs them.</summary>
/// <param name="Columns">This side's catalog, fetched only when a script check needs it — a built-in
/// derives everything it asks from the mapping and should not pay for a round trip to learn what it
/// already knows.</param>
public sealed record VerificationEndpoint(
    DbConnection Connection,
    SqlDialect Dialect,
    string Schema,
    string Table,
    IScriptDialect? ScriptDialect = null,
    IReadOnlyList<ColumnMetadata>? Columns = null,
    TableRef? Ref = null);

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
        CancellationToken cancellationToken,
        IVerificationQueryBuilder? queryBuilder = null)
    {
        var (sourceSql, targetSql) = BuildStatements(check, columnMappings, source, target, queryBuilder);
        var shape = DescribeShape(check, columnMappings, source, queryBuilder);

        var (sourceSide, sourceRead) = await ReadAsync(source.Connection, sourceSql, shape, cancellationToken);
        var (targetSide, targetRead) = await ReadAsync(target.Connection, targetSql, shape, cancellationToken);

        return new VerificationResult(
            check.Name,
            shape.GroupColumns,
            shape.MeasureColumns,
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
        VerificationEndpoint target,
        IVerificationQueryBuilder? queryBuilder = null)
    {
        // Target side only. A source has no history to filter, by definition — and filtering it would
        // be comparing a subset of the source against all of the target, which is the same mistake
        // pointing the other way.
        var currentOnly = check.CompareCurrentOnly
            ? VerificationStatement.CurrentOnlyPredicate(
                target.Dialect, target.Schema, target.Table, check.CurrentColumn)
            : null;

        switch (check.Kind)
        {
            case VerificationCheckKind.RowCount:
                return (
                    VerificationStatement.BuildRowCount(
                        source.Dialect, source.Schema, source.Table,
                        VerificationStatement.ResolveSource(source.Dialect, columnMappings, check.GroupBy), check.Filter),
                    VerificationStatement.BuildRowCount(
                        target.Dialect, target.Schema, target.Table,
                        VerificationStatement.ResolveTarget(target.Dialect, check.GroupBy), check.Filter,
                        currentOnly));

            case VerificationCheckKind.Sum:
                return (
                    VerificationStatement.BuildSum(
                        source.Dialect, source.Schema, source.Table,
                        VerificationStatement.ResolveSource(source.Dialect, columnMappings, check.GroupBy),
                        VerificationStatement.ResolveSource(source.Dialect, columnMappings, check.Measures), check.Filter),
                    VerificationStatement.BuildSum(
                        target.Dialect, target.Schema, target.Table,
                        VerificationStatement.ResolveTarget(target.Dialect, check.GroupBy),
                        VerificationStatement.ResolveTarget(target.Dialect, check.Measures), check.Filter,
                        currentOnly));

            case VerificationCheckKind.Sql:
                if (string.IsNullOrWhiteSpace(check.SourceSql))
                    throw new ConfigValidationException($"Verification check '{check.Name}' has no SQL.");
                return (check.SourceSql, check.TargetSql ?? check.SourceSql);

            case VerificationCheckKind.Script:
                if (queryBuilder is null)
                {
                    throw new ConfigValidationException(
                        $"Verification check '{check.Name}' names script '{check.ScriptName}', which could not be resolved.");
                }
                // Built once per side, so a script can produce genuinely different SQL for two engines
                // — which is what a per-dialect check covers and a generic one does not.
                return (
                    queryBuilder.BuildQuery(ContextFor(check, columnMappings, source, VerificationSideKind.Source)).CommandText,
                    queryBuilder.BuildQuery(ContextFor(check, columnMappings, target, VerificationSideKind.Target)).CommandText);

            default:
                throw new ConfigValidationException(
                    $"Verification check '{check.Name}' is of kind '{check.Kind}', which nothing runs yet.");
        }
    }

    /// <summary>
    /// What the two result sets are shaped like. A built-in and a hand-written check say so in their
    /// config; a generated one says so through its own <see cref="IVerificationQueryBuilder.DescribeResult"/>,
    /// because a query that decides its own columns is the only thing that can describe them.
    /// </summary>
    private static VerificationQueryShape DescribeShape(
        VerificationCheckConfig check,
        IReadOnlyList<ColumnMapping> columnMappings,
        VerificationEndpoint source,
        IVerificationQueryBuilder? queryBuilder) =>
        check.Kind == VerificationCheckKind.Script && queryBuilder is not null
            ? queryBuilder.DescribeResult(ContextFor(check, columnMappings, source, VerificationSideKind.Source))
            : new VerificationQueryShape(check.GroupBy, MeasureNames(check));

    private static VerificationQueryContext ContextFor(
        VerificationCheckConfig check,
        IReadOnlyList<ColumnMapping> columnMappings,
        VerificationEndpoint side,
        VerificationSideKind kind) =>
        new(
            kind,
            side.Ref ?? new TableRef
            {
                ConnectionName = "", Database = "", Schema = side.Schema, Table = side.Table,
            },
            columnMappings,
            side.Columns ?? [],
            check.Filter,
            side.ScriptDialect ?? throw new ConfigValidationException(
                $"Verification check '{check.Name}' needs a script dialect, which this side did not supply."),
            new ScriptParameters(check.Parameters));

    /// <summary>
    /// A row count's measure has no column to be named after, so it is fixed and both sides agree on
    /// it. Everything else is named by what the check declared.
    /// </summary>
    private static IReadOnlyList<string> MeasureNames(VerificationCheckConfig check) =>
        check.Kind == VerificationCheckKind.RowCount ? [VerificationStatement.RowCountColumn] : check.Measures;

    private static async Task<(VerificationSide Side, DateTimeOffset ReadAt)> ReadAsync(
        DbConnection connection, string sql, VerificationQueryShape shape, CancellationToken cancellationToken)
    {
        var readAt = DateTimeOffset.UtcNow;

        using var command = connection.CreateTimedCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var groups = new List<IReadOnlyList<string>>();
        var measures = new List<IReadOnlyDictionary<string, double>>();

        while (await reader.ReadAsync(cancellationToken))
        {
            groups.Add([.. shape.GroupColumns.Select(name => VerificationComparison.GroupValue(Read(reader, name)))]);

            var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var measure in shape.MeasureColumns)
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
