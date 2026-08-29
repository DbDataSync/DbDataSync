using DataSync.Drivers.Generic;

namespace DataSync.Drivers.Postgres;

/// <summary>
/// PostgreSQL's trigger-audit DDL — the half of the mechanism that does not generalise.
/// <para>
/// Postgres triggers are **row-level and need a function**: there is no inline trigger body, so this
/// is a <c>CREATE FUNCTION ... RETURNS trigger</c> plus a <c>CREATE TRIGGER</c> that calls it. The
/// function sees one row at a time through <c>NEW</c> and <c>OLD</c>, which is the opposite of SQL
/// Server's statement-level <c>inserted</c>/<c>deleted</c> — the clearest illustration of why there is
/// no portable trigger DDL and no point pretending there is.
/// </para>
/// </summary>
public static class PostgresTriggerAudit
{
    public static string TriggerName(string table) => $"ds_trg_{table}";

    public static string FunctionName(string table) => $"ds_capture_{table}";

    public static string CreateShadowTable(string schema, string table, IReadOnlyList<string> keyColumnDefinitions)
    {
        var dialect = PostgresDialect.Instance;
        var shadow = dialect.QualifyTable(schema, TriggerAuditStatement.ShadowTableName(table));
        var keys = string.Join(",\n    ", keyColumnDefinitions);

        return $"""
            CREATE TABLE {shadow} (
                {dialect.QuoteIdentifier(TriggerAuditStatement.SequenceColumn)} BIGSERIAL PRIMARY KEY,
                {dialect.QuoteIdentifier(TriggerAuditStatement.OperationColumn)} CHAR(1) NOT NULL,
                {keys},
                {dialect.QuoteIdentifier(TriggerAuditStatement.ChangedAtColumn)} TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            """;
    }

    public static string CreateFunctionAndTrigger(string schema, string table, IReadOnlyList<string> keyColumns)
    {
        var dialect = PostgresDialect.Instance;
        var baseTable = dialect.QualifyTable(schema, table);
        var shadow = dialect.QualifyTable(schema, TriggerAuditStatement.ShadowTableName(table));
        var function = dialect.QualifyTable(schema, FunctionName(table));
        var trigger = dialect.QuoteIdentifier(TriggerName(table));

        var op = dialect.QuoteIdentifier(TriggerAuditStatement.OperationColumn);
        var keyList = string.Join(", ", keyColumns.Select(dialect.QuoteIdentifier));
        var newKeys = string.Join(", ", keyColumns.Select(k => $"NEW.{dialect.QuoteIdentifier(k)}"));
        var oldKeys = string.Join(", ", keyColumns.Select(k => $"OLD.{dialect.QuoteIdentifier(k)}"));

        // RETURN NULL is correct for an AFTER trigger and is what keeps this from altering the row the
        // application wrote — a BEFORE trigger returning the wrong thing silently changes data, which
        // is the failure worth being deliberate about.
        return $"""
            CREATE OR REPLACE FUNCTION {function}() RETURNS trigger AS $$
            BEGIN
                IF (TG_OP = 'DELETE') THEN
                    INSERT INTO {shadow} ({op}, {keyList}) VALUES ('D', {oldKeys});
                ELSIF (TG_OP = 'UPDATE') THEN
                    INSERT INTO {shadow} ({op}, {keyList}) VALUES ('U', {newKeys});
                ELSE
                    INSERT INTO {shadow} ({op}, {keyList}) VALUES ('I', {newKeys});
                END IF;
                RETURN NULL;
            END;
            $$ LANGUAGE plpgsql;

            CREATE TRIGGER {trigger}
            AFTER INSERT OR UPDATE OR DELETE ON {baseTable}
            FOR EACH ROW EXECUTE FUNCTION {function}();
            """;
    }
}
