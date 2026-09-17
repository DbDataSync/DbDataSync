using DbDataSync.Drivers.Generic;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Oracle;

/// <summary>
/// Oracle's trigger-audit DDL — the half of the mechanism that does not generalise.
/// <para>
/// Oracle sits between the other two engines' shapes: like Postgres (and unlike MySQL), one trigger
/// handles all three events — <c>AFTER INSERT OR UPDATE OR DELETE</c>, branching inside on the
/// <c>INSERTING</c>/<c>UPDATING</c>/<c>DELETING</c> boolean predicates. Like MySQL (and unlike
/// Postgres), the body is inline PL/SQL — no separate function needed. Row references are
/// colon-prefixed, <c>:NEW</c>/<c>:OLD</c>, not bare <c>NEW</c>/<c>OLD</c>. Every statement here was
/// run against a live Oracle 23ai instance before this class was written, including the
/// <c>GENERATED ALWAYS AS IDENTITY</c> shadow-table column and the trigger's own compound-branch body.
/// </para>
/// <para>
/// **Floored at Oracle 12c+.** <c>GENERATED ALWAYS AS IDENTITY</c> — the shadow table's own sequence
/// column, playing the same role <c>BIGSERIAL</c>/<c>AUTO_INCREMENT</c> play for Postgres/MySQL — is
/// 12c+ only. A pre-12c sequence-based fallback is out of scope: <c>Oracle.ManagedDataAccess.Core</c>
/// (this driver's pinned client) is the modern .NET-Core-targeted ODP.NET line, exercised almost
/// exclusively against 12c+ databases in practice, so building untested support for a combination
/// unlikely to exist would cost real effort for no real coverage.
/// </para>
/// </summary>
public static class OracleTriggerAudit
{
    public static string TriggerName(string table) => $"ds_trg_{table}";

    public static string CreateShadowTable(string schema, string table, IReadOnlyList<string> keyColumnDefinitions)
    {
        var dialect = OracleDialect.Instance;
        var shadow = dialect.QualifyTable(schema, TriggerAuditStatement.ShadowTableName(table));
        var keys = string.Join(",\n    ", keyColumnDefinitions);

        return $"""
            CREATE TABLE {shadow} (
                {dialect.QuoteIdentifier(TriggerAuditStatement.SequenceColumn)} NUMBER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                {dialect.QuoteIdentifier(TriggerAuditStatement.OperationColumn)} CHAR(1) NOT NULL,
                {keys},
                {dialect.QuoteIdentifier(TriggerAuditStatement.ChangedAtColumn)} TIMESTAMP(6) DEFAULT SYSTIMESTAMP NOT NULL
            )
            """;
    }

    public static string CreateTrigger(string schema, string table, IReadOnlyList<string> keyColumns)
    {
        var dialect = OracleDialect.Instance;
        var baseTable = dialect.QualifyTable(schema, table);
        var shadow = dialect.QualifyTable(schema, TriggerAuditStatement.ShadowTableName(table));
        var trigger = dialect.QualifyTable(schema, TriggerName(table));

        var op = dialect.QuoteIdentifier(TriggerAuditStatement.OperationColumn);
        var keyList = string.Join(", ", keyColumns.Select(dialect.QuoteIdentifier));
        var newKeys = string.Join(", ", keyColumns.Select(k => $":NEW.{dialect.QuoteIdentifier(k)}"));
        var oldKeys = string.Join(", ", keyColumns.Select(k => $":OLD.{dialect.QuoteIdentifier(k)}"));

        return $"""
            CREATE OR REPLACE TRIGGER {trigger}
            AFTER INSERT OR UPDATE OR DELETE ON {baseTable}
            FOR EACH ROW
            BEGIN
                IF INSERTING THEN
                    INSERT INTO {shadow} ({op}, {keyList}) VALUES ('I', {newKeys});
                ELSIF UPDATING THEN
                    INSERT INTO {shadow} ({op}, {keyList}) VALUES ('U', {newKeys});
                ELSE
                    INSERT INTO {shadow} ({op}, {keyList}) VALUES ('D', {oldKeys});
                END IF;
            END;
            """;
    }
}
