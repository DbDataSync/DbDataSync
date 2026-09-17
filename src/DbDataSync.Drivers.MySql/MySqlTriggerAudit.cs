using DbDataSync.Drivers.Generic;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.MySql;

/// <summary>
/// MySQL/MariaDB's trigger-audit DDL — the half of the mechanism that does not generalise.
/// <para>
/// MySQL cannot combine <c>INSERT OR UPDATE OR DELETE</c> in one trigger definition, unlike Postgres
/// (which branches on <c>TG_OP</c> inside one function) or Oracle's <c>INSERTING</c>/<c>UPDATING</c>/
/// <c>DELETING</c>. So this creates three triggers, one per event, each with an inline body — no
/// separate function needed, like SQL Server and unlike Postgres. Confirmed identical between MySQL and
/// MariaDB in <c>change-tracking-mysql-and-mariadb-triggers.md</c>; nothing here branches on which fork
/// it is talking to.
/// </para>
/// </summary>
public static class MySqlTriggerAudit
{
    /// <summary><c>ai</c>/<c>au</c>/<c>ad</c> — after-insert, after-update, after-delete. The three
    /// trigger name suffixes <see cref="MySqlProvisioner"/> checks for when deciding whether this table
    /// is already fully set up.</summary>
    public static IReadOnlyList<string> TriggerSuffixes { get; } = ["ai", "au", "ad"];

    public static string TriggerName(string table, string suffix) => $"ds_trg_{table}_{suffix}";

    public static string CreateShadowTable(string schema, string table, IReadOnlyList<string> keyColumnDefinitions)
    {
        var dialect = MySqlDialect.Instance;
        var shadow = dialect.QualifyTable(schema, TriggerAuditStatement.ShadowTableName(table));
        var keys = string.Join(",\n    ", keyColumnDefinitions);

        return $"""
            CREATE TABLE {shadow} (
                {dialect.QuoteIdentifier(TriggerAuditStatement.SequenceColumn)} BIGINT AUTO_INCREMENT PRIMARY KEY,
                {dialect.QuoteIdentifier(TriggerAuditStatement.OperationColumn)} CHAR(1) NOT NULL,
                {keys},
                {dialect.QuoteIdentifier(TriggerAuditStatement.ChangedAtColumn)} TIMESTAMP(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
            ) ENGINE=InnoDB;
            """;
    }

    /// <summary>
    /// Each preceded by <c>DROP TRIGGER IF EXISTS</c> — three independent objects rather than
    /// Postgres's one, so a prior partially-failed run leaving one or two of the three in place is a
    /// real possibility in a way it barely is for a single-trigger engine. This makes the step safely
    /// re-runnable rather than failing on a duplicate-trigger error the second time it applies.
    /// </summary>
    public static string CreateTriggers(string schema, string table, IReadOnlyList<string> keyColumns)
    {
        var dialect = MySqlDialect.Instance;
        var baseTable = dialect.QualifyTable(schema, table);
        var shadow = dialect.QualifyTable(schema, TriggerAuditStatement.ShadowTableName(table));
        var op = dialect.QuoteIdentifier(TriggerAuditStatement.OperationColumn);
        var keyList = string.Join(", ", keyColumns.Select(dialect.QuoteIdentifier));

        var insertTrigger = dialect.QuoteIdentifier(TriggerName(table, "ai"));
        var updateTrigger = dialect.QuoteIdentifier(TriggerName(table, "au"));
        var deleteTrigger = dialect.QuoteIdentifier(TriggerName(table, "ad"));

        var newKeys = string.Join(", ", keyColumns.Select(k => $"NEW.{dialect.QuoteIdentifier(k)}"));
        var oldKeys = string.Join(", ", keyColumns.Select(k => $"OLD.{dialect.QuoteIdentifier(k)}"));

        return $"""
            DROP TRIGGER IF EXISTS {insertTrigger};
            CREATE TRIGGER {insertTrigger} AFTER INSERT ON {baseTable}
            FOR EACH ROW INSERT INTO {shadow} ({op}, {keyList}) VALUES ('I', {newKeys});

            DROP TRIGGER IF EXISTS {updateTrigger};
            CREATE TRIGGER {updateTrigger} AFTER UPDATE ON {baseTable}
            FOR EACH ROW INSERT INTO {shadow} ({op}, {keyList}) VALUES ('U', {newKeys});

            DROP TRIGGER IF EXISTS {deleteTrigger};
            CREATE TRIGGER {deleteTrigger} AFTER DELETE ON {baseTable}
            FOR EACH ROW INSERT INTO {shadow} ({op}, {keyList}) VALUES ('D', {oldKeys});
            """;
    }
}
