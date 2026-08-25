using System.Data.Common;
using DataSync.Core.Config;
using DataSync.Drivers.Abstractions;

namespace DataSync.Drivers.MsSql;

/// <summary>
/// Applies a staged change set via a single MERGE statement (architecture/detailed-design.md §3.5's
/// primary writer path). Requires the target's primary key column(s) to be included in
/// <see cref="ColumnMapping"/> — used both as the MERGE join key and to distinguish which mapped
/// columns are safe to include in the UPDATE SET list.
/// </summary>
public sealed class MsSqlMergeWriter : IChangeWriter
{
    public string Kind => MsSqlDriverKinds.Merge;

    public async Task<WriteResult> ApplyAsync(
        DbConnection targetConnection,
        TableRef target,
        StagedChangeSet staged,
        IReadOnlyList<ColumnMapping> columnMappings,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        targetConnection.ChangeDatabase(target.Database);

        var pkColumns = await MsSqlSchemaQueries.GetPrimaryKeyColumnsAsync(targetConnection, target.Schema, target.Table, cancellationToken);
        if (pkColumns.Count == 0)
            throw new InvalidOperationException($"Target table '{target.Schema}.{target.Table}' has no primary key; MERGE requires one.");

        var mappedTargetColumns = columnMappings.Select(m => m.TargetColumn).Distinct().ToList();
        var missingPk = pkColumns.Where(pk => !mappedTargetColumns.Contains(pk, StringComparer.OrdinalIgnoreCase)).ToList();
        if (missingPk.Count > 0)
            throw new InvalidOperationException(
                $"Column mappings must include the target primary key column(s): {string.Join(", ", missingPk)}.");

        var quotedTarget = $"{SqlIdentifier.Quote(target.Schema)}.{SqlIdentifier.Quote(target.Table)}";
        var onClause = string.Join(" AND ", pkColumns.Select(pk => $"tgt.{SqlIdentifier.Quote(pk)} = src.{SqlIdentifier.Quote(pk)}"));
        var nonPkColumns = mappedTargetColumns.Where(c => !pkColumns.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
        var insertColumns = string.Join(", ", mappedTargetColumns.Select(SqlIdentifier.Quote));
        var insertValues = string.Join(", ", mappedTargetColumns.Select(c => $"src.{SqlIdentifier.Quote(c)}"));

        var updateClause = nonPkColumns.Count > 0
            ? $"WHEN MATCHED AND src.__Operation <> 'D' THEN UPDATE SET {string.Join(", ", nonPkColumns.Select(c => $"tgt.{SqlIdentifier.Quote(c)} = src.{SqlIdentifier.Quote(c)}"))}"
            : ""; // every mapped column is part of the PK — nothing to update, only insert/delete apply

        using var cmd = targetConnection.CreateCommand();
        cmd.CommandText = $"""
            MERGE INTO {quotedTarget} AS tgt
            USING {staged.StagingLocation} AS src
            ON {onClause}
            WHEN MATCHED AND src.__Operation = 'D' THEN DELETE
            {updateClause}
            WHEN NOT MATCHED BY TARGET AND src.__Operation <> 'D' THEN INSERT ({insertColumns}) VALUES ({insertValues});
            """;

        var rowsAffected = await cmd.ExecuteNonQueryAsync(cancellationToken);
        return new WriteResult(rowsAffected);
    }
}
