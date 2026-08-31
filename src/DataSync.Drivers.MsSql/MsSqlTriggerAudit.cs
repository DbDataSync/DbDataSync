using DataSync.Drivers.Generic;
using DataSync.Core.Sql;

namespace DataSync.Drivers.MsSql;

/// <summary>
/// SQL Server's trigger-audit DDL — the half of the mechanism that does not generalise.
/// <para>
/// T-SQL triggers are **statement-level**: one firing sees every affected row through the
/// <c>inserted</c> and <c>deleted</c> pseudo-tables, and a row-by-row trigger is the classic way to
/// make a bulk update take minutes. So this is written as set-based inserts, which is the only shape
/// worth putting on somebody's write path.
/// </para>
/// <para>
/// An update is <c>inserted</c> and <c>deleted</c> both having rows; an insert is <c>inserted</c>
/// only; a delete is <c>deleted</c> only. That is how one trigger covers all three without three
/// triggers to keep in step.
/// </para>
/// </summary>
public static class MsSqlTriggerAudit
{
    public static string TriggerName(string table) => $"DS_Trg_{table}";

    public static string CreateShadowTable(string schema, string table, IReadOnlyList<string> keyColumnDefinitions)
    {
        var shadow = MsSqlDialect.Instance.QualifyTable(schema, TriggerAuditStatement.ShadowTableName(table));
        var keys = string.Join(",\n    ", keyColumnDefinitions);

        return $"""
            CREATE TABLE {shadow} (
                {SqlIdentifier.Quote(TriggerAuditStatement.SequenceColumn)} BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                {SqlIdentifier.Quote(TriggerAuditStatement.OperationColumn)} CHAR(1) NOT NULL,
                {keys},
                {SqlIdentifier.Quote(TriggerAuditStatement.ChangedAtColumn)} DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
            );
            """;
    }

    public static string CreateTrigger(string schema, string table, IReadOnlyList<string> keyColumns)
    {
        var dialect = MsSqlDialect.Instance;
        var baseTable = dialect.QualifyTable(schema, table);
        var shadow = dialect.QualifyTable(schema, TriggerAuditStatement.ShadowTableName(table));
        var trigger = dialect.QualifyTable(schema, TriggerName(table));

        var op = SqlIdentifier.Quote(TriggerAuditStatement.OperationColumn);
        var keyList = string.Join(", ", keyColumns.Select(SqlIdentifier.Quote));
        var insertedKeys = string.Join(", ", keyColumns.Select(k => $"i.{SqlIdentifier.Quote(k)}"));
        var deletedKeys = string.Join(", ", keyColumns.Select(k => $"d.{SqlIdentifier.Quote(k)}"));
        var joinCondition = string.Join(" AND ",
            keyColumns.Select(k => $"i.{SqlIdentifier.Quote(k)} = d.{SqlIdentifier.Quote(k)}"));

        // SET NOCOUNT ON so the trigger's own inserts do not add row counts to what the application's
        // statement reports — an ORM reading an unexpected count is a real way to break a caller that
        // has nothing to do with replication.
        return $"""
            CREATE TRIGGER {trigger}
            ON {baseTable}
            AFTER INSERT, UPDATE, DELETE
            AS
            BEGIN
                SET NOCOUNT ON;

                INSERT INTO {shadow} ({op}, {keyList})
                SELECT 'U', {insertedKeys}
                FROM inserted AS i
                JOIN deleted AS d ON {joinCondition};

                INSERT INTO {shadow} ({op}, {keyList})
                SELECT 'I', {insertedKeys}
                FROM inserted AS i
                WHERE NOT EXISTS (SELECT 1 FROM deleted AS d WHERE {joinCondition});

                INSERT INTO {shadow} ({op}, {keyList})
                SELECT 'D', {deletedKeys}
                FROM deleted AS d
                WHERE NOT EXISTS (SELECT 1 FROM inserted AS i WHERE {joinCondition});
            END;
            """;
    }
}
